using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeHollow.FeedReader;

namespace RssReader;

public class FeedService
{
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

        try
        {
            parsedFeed = await FeedReader.ReadAsync(feed.Url);
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
                ImageUrl    = ExtractImageUrl(item, rawContent),
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

    private static string? ExtractImageUrl(FeedItem item, string rawContent)
    {
        var imgUrl = ExtractFromImgTags(rawContent, item.Link);
        if (imgUrl != null) return imgUrl;

        return ExtractFromSpecificItem(item);
    }

    private static string? ExtractFromImgTags(string html, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

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

    private static string? ExtractFromSpecificItem(FeedItem item)
    {
        var si = item.SpecificItem;
        if (si == null) return null;

        if (si is CodeHollow.FeedReader.Feeds.MediaRssFeedItem mediaItem)
        {
            var url = ExtractFromMediaRssItem(mediaItem);
            if (url != null) return url;
        }
        else if (si is CodeHollow.FeedReader.Feeds.Rss20FeedItem rss20Item)
        {
            var url = ExtractEnclosureUrl(rss20Item.Enclosure);
            if (url != null) return url;
        }
        else if (si is CodeHollow.FeedReader.Feeds.Rss092FeedItem rss092Item)
        {
            var url = ExtractEnclosureUrl(rss092Item.Enclosure);
            if (url != null) return url;
        }

        return ExtractFromXElement(si.Element);
    }

    private static string? ExtractFromMediaRssItem(
        CodeHollow.FeedReader.Feeds.MediaRssFeedItem mediaItem)
    {
        foreach (var media in mediaItem.Media)
        {
            var url = PickMediaUrl(media);
            if (url != null) return url;

            if (media.Thumbnails != null)
            {
                foreach (var thumb in media.Thumbnails)
                {
                    if (!string.IsNullOrWhiteSpace(thumb.Url) && IsAbsoluteUrl(thumb.Url))
                        return thumb.Url;
                }
            }
        }

        foreach (var group in mediaItem.MediaGroups)
        {
            foreach (var media in group.Media)
            {
                var url = PickMediaUrl(media);
                if (url != null) return url;
            }
        }

        return ExtractEnclosureUrl(mediaItem.Enclosure);
    }

    private static string? PickMediaUrl(CodeHollow.FeedReader.Feeds.MediaRSS.Media media)
    {
        if (string.IsNullOrWhiteSpace(media.Url)) return null;
        if (!IsAbsoluteUrl(media.Url)) return null;

        var m = media.Medium;
        if (m == CodeHollow.FeedReader.Feeds.MediaRSS.Medium.Image ||
            m == CodeHollow.FeedReader.Feeds.MediaRSS.Medium.Unknown)
            return media.Url;

        return null;
    }

    private static string? ExtractEnclosureUrl(
        CodeHollow.FeedReader.Feeds.FeedItemEnclosure? enc)
    {
        if (enc?.Url == null) return null;
        if (!IsAbsoluteUrl(enc.Url)) return null;

        var encType = enc.MediaType ?? "";
        if (encType.Length == 0 || encType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return enc.Url;

        return null;
    }

    private static string? ExtractFromXElement(XElement element)
    {
        if (element == null) return null;

        var url = PickFirstMediaElement(element, MediaNs + "content")
               ?? PickFirstMediaElement(element, MediaNs + "thumbnail")
               ?? PickEnclosureUrl(element)
               ?? PickAtomEnclosureUrl(element);

        return url;
    }

    private static string? PickFirstMediaElement(XElement element, XName name)
    {
        var el = element.Descendants(name)
            .FirstOrDefault(e => e.Attribute("url") != null);

        if (el == null)
        {
            var groupEl = element.Element(MediaNs + "group");
            if (groupEl != null)
                el = groupEl.Elements(name)
                    .FirstOrDefault(e => e.Attribute("url") != null);
        }

        var url = System.Net.WebUtility.HtmlDecode(el?.Attribute("url")?.Value?.Trim() ?? "");
        return IsAbsoluteUrl(url) ? url : null;
    }

    private static string? PickEnclosureUrl(XElement element)
    {
        var enc = element.Element("enclosure")
               ?? element.Descendants("enclosure").FirstOrDefault();

        if (enc == null) return null;

        var type = enc.Attribute("type")?.Value ?? "";
        if (type.Length > 0 && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        var url = System.Net.WebUtility.HtmlDecode(enc.Attribute("url")?.Value?.Trim() ?? "");
        return IsAbsoluteUrl(url) ? url : null;
    }

    private static string? PickAtomEnclosureUrl(XElement element)
    {
        foreach (var link in element.Descendants())
        {
            if (!link.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))
                continue;

            var rel = link.Attribute("rel")?.Value ?? "";
            if (!rel.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
                continue;

            var type = link.Attribute("type")?.Value ?? "";
            if (type.Length > 0 && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = System.Net.WebUtility.HtmlDecode(
                link.Attribute("href")?.Value?.Trim() ?? "");

            if (IsAbsoluteUrl(href))
                return href;
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