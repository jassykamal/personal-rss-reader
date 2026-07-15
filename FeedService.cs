using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeHollow.FeedReader;

namespace RssReader;

public class FeedService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly XNamespace MediaNs = "http://search.yahoo.com/mrss/";

    private readonly StorageService _storage;

    public FeedService(StorageService storage)
    {
        _storage = storage;
    }

    public async Task<string?> ValidateFeedAsync(string url)
    {
        try
        {
            var feed = await FeedReader.ReadAsync(url);
            return string.IsNullOrWhiteSpace(feed?.Title) ? null : feed.Title;
        }
        catch
        {
            return null;
        }
    }

    public async Task RefreshFeedAsync(Feed feed)
    {
        CodeHollow.FeedReader.Feed parsedFeed;
        Dictionary<string, string>? mediaLookup = null;

        try
        {
            parsedFeed = await FeedReader.ReadAsync(feed.Url);

            var rawXml = await HttpClient.GetStringAsync(feed.Url);
            mediaLookup = ParseMediaFromXml(rawXml);
        }
        catch
        {
            return;
        }

        var data = _storage.Load();
        data.Articles.RemoveAll(a => a.FeedId == feed.Id);

        foreach (var item in parsedFeed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Link))
                continue;

            var rawContent = item.Description ?? item.Content ?? string.Empty;

            var article = new Article
            {
                FeedId      = feed.Id,
                FeedTitle   = feed.Title,
                Title       = item.Title ?? "(No title)",
                Url         = item.Link  ?? string.Empty,
                Description = StripHtml(rawContent),
                ImageUrl    = ExtractBestImage(rawContent, item.Link, mediaLookup),
                PublishedAt = item.PublishingDate?.ToUniversalTime() ?? DateTime.UtcNow
            };

            data.Articles.Add(article);
        }

        var storedFeed = data.Feeds.FirstOrDefault(f => f.Id == feed.Id);
        if (storedFeed != null)
            storedFeed.LastRefreshed = DateTime.UtcNow;

        _storage.Save(data);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var noTags = Regex.Replace(html, "<[^>]*>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var clean = Regex.Replace(decoded, @"\s+", " ").Trim();

        return clean;
    }

    private static string? ExtractBestImage(string rawContent, string? articleLink,
                                             Dictionary<string, string>? mediaLookup)
    {
        var imgUrl = ExtractFromImgTags(rawContent, articleLink);
        if (imgUrl != null) return imgUrl;

        if (mediaLookup != null && !string.IsNullOrWhiteSpace(articleLink) &&
            mediaLookup.TryGetValue(articleLink, out var mediaUrl))
            return mediaUrl;

        return null;
    }

    private static string? ExtractFromImgTags(string html, string? baseUrl)
    {
        var matches = Regex.Matches(
            html,
            "<img[^>]+src=[\"'](?<src>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match match in matches)
        {
            var src = System.Net.WebUtility.HtmlDecode(match.Groups["src"].Value.Trim());
            if (string.IsNullOrWhiteSpace(src)) continue;

            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var resolved = ResolveToAbsolute(src, baseUrl);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private static Dictionary<string, string> ParseMediaFromXml(string rawXml)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = XDocument.Parse(rawXml);
            if (doc.Root == null) return lookup;

            var items = FindFeedItems(doc);

            foreach (var itemEl in items)
            {
                var link = ExtractItemLink(itemEl);
                if (string.IsNullOrWhiteSpace(link)) continue;

                var imageUrl = ExtractMediaFromItem(itemEl);
                if (imageUrl != null)
                    lookup[link] = imageUrl;
            }
        }
        catch
        {
        }

        return lookup;
    }

    private static List<XElement> FindFeedItems(XDocument doc)
    {
        var items = doc.Descendants("item").ToList();
        if (items.Count > 0) return items;

        items = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count > 0) return items;

        items = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return items;
    }

    private static string? ExtractItemLink(XElement item)
    {
        var linkEl = item.Element("link");
        if (linkEl != null)
        {
            var text = linkEl.Value.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;

            var href = linkEl.Attribute("href")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(href)) return href;
        }

        var links = item.Elements()
            .Where(e => e.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var l in links)
        {
            var href = l.Attribute("href")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(href)) return href;
        }

        var guid = item.Element("guid")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(guid)) return guid;

        var atomId = item.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(atomId)) return atomId;

        return null;
    }

    private static string? ExtractMediaFromItem(XElement item)
    {
        foreach (var el in item.Elements())
        {
            if (el.Name == MediaNs + "content" || el.Name == MediaNs + "thumbnail")
            {
                var url = el.Attribute("url")?.Value;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(url).Trim();
                    if (IsAbsoluteUrl(decoded))
                        return decoded;
                }
            }

            if (el.Name == MediaNs + "group")
            {
                foreach (var child in el.Elements())
                {
                    if (child.Name == MediaNs + "content" || child.Name == MediaNs + "thumbnail")
                    {
                        var url = child.Attribute("url")?.Value;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var decoded = System.Net.WebUtility.HtmlDecode(url).Trim();
                            if (IsAbsoluteUrl(decoded))
                                return decoded;
                        }
                    }
                }
            }
        }

        var enclosure = item.Element("enclosure")
            ?? item.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("enclosure", StringComparison.OrdinalIgnoreCase));

        if (enclosure != null)
        {
            var type = enclosure.Attribute("type")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(type) ||
                type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var url = enclosure.Attribute("url")?.Value;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(url).Trim();
                    if (IsAbsoluteUrl(decoded))
                        return decoded;
                }
            }
        }

        var atomLinkEnclosure = item.Elements()
            .Where(e => e.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(e =>
            {
                var rel = e.Attribute("rel")?.Value ?? "";
                return rel.Equals("enclosure", StringComparison.OrdinalIgnoreCase);
            });

        if (atomLinkEnclosure != null)
        {
            var type = atomLinkEnclosure.Attribute("type")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(type) ||
                type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var href = atomLinkEnclosure.Attribute("href")?.Value;
                if (!string.IsNullOrWhiteSpace(href))
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(href).Trim();
                    if (IsAbsoluteUrl(decoded))
                        return decoded;
                }
            }
        }

        return null;
    }

    private static string? ResolveToAbsolute(string url, string? baseUrl)
    {
        if (IsAbsoluteUrl(url))
            return url;

        if (!string.IsNullOrWhiteSpace(baseUrl) &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, url, out var resolved))
            return resolved.ToString();

        return null;
    }

    private static bool IsAbsoluteUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
}