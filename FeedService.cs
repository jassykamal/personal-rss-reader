using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeHollow.FeedReader;

namespace RssReader;

public class FeedService
{
    private static readonly XNamespace MediaNs = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace PodcastNs = "https://podcastindex.org/namespace/1.0";
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    private readonly StorageService _storage;
    private readonly HttpClient _httpClient;

    public FeedService(StorageService storage)
    {
        _storage = storage;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "RssReader/1.0");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/rss+xml, application/atom+xml, application/xml, text/xml, */*");
    }

    public async Task<string?> ValidateFeedAsync(string url)
    {
        try
        {
            var feed = await FeedReader.ReadAsync(url);
            if (!string.IsNullOrWhiteSpace(feed?.Title))
                return feed.Title;
        }
        catch { /* FeedReader failed, try XML fallback */ }

        return await TryExtractTitleFromXmlAsync(url);
    }

    private async Task<string?> TryExtractTitleFromXmlAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(url);
            var xml = XDocument.Parse(response);
            var root = xml.Root;
            if (root == null) return null;

            XElement? channel = null;

            if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                channel = root;
            }
            else
            {
                foreach (var el in root.Elements())
                {
                    if (el.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase))
                    {
                        channel = el;
                        break;
                    }
                }
            }

            if (channel == null) return null;

            foreach (var el in channel.Elements())
            {
                if (el.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
                {
                    var title = el.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<FeedType> DetectFeedTypeAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(url);
            var xml = XDocument.Parse(response);
            var root = xml.Root;
            if (root == null) return FeedType.News;

            var nsAttrs = root.Attributes()
                .Where(a => a.IsNamespaceDeclaration)
                .Select(a => a.Value)
                .ToList();

            var hasItunesNs = nsAttrs.Any(v =>
                v.Contains("itunes.com", StringComparison.OrdinalIgnoreCase) ||
                v.Contains("itunes.dtd", StringComparison.OrdinalIgnoreCase));

            var hasPodcastNs = nsAttrs.Any(v =>
                v.Contains("podcastindex.org", StringComparison.OrdinalIgnoreCase));

            var hasAudioEnclosure = root.Descendants().Any(e =>
            {
                if (!e.Name.LocalName.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
                    return false;
                var type = e.Attribute("type")?.Value ?? "";
                return type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            });

            var hasItunesElements = root.Descendants().Any(d =>
            {
                var ns = d.Name.NamespaceName;
                return ns.Contains("itunes.com", StringComparison.OrdinalIgnoreCase) ||
                       ns.Contains("itunes.dtd", StringComparison.OrdinalIgnoreCase);
            });

            var hasMediaAudio = root.Descendants().Any(e =>
            {
                if (!e.Name.LocalName.Equals("content", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!e.Name.NamespaceName.Contains("mrss", StringComparison.OrdinalIgnoreCase) &&
                    !e.Name.NamespaceName.Contains("yahoo", StringComparison.OrdinalIgnoreCase))
                    return false;
                var type = e.Attribute("type")?.Value ?? "";
                var medium = e.Attribute("medium")?.Value ?? "";
                return type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                       medium.Equals("audio", StringComparison.OrdinalIgnoreCase);
            });

            if (hasItunesNs || hasPodcastNs || hasAudioEnclosure || hasItunesElements || hasMediaAudio)
                return FeedType.Podcast;

            return FeedType.News;
        }
        catch
        {
            return FeedType.News;
        }
    }

    public async Task RefreshFeedAsync(Feed feed)
    {
        CodeHollow.FeedReader.Feed? parsedFeed = null;

        try
        {
            parsedFeed = await FeedReader.ReadAsync(feed.Url);
        }
        catch
        {
            /* FeedReader failed — use raw XML fallback below */
        }

        var data = _storage.Load();
        data.Articles.RemoveAll(a => a.FeedId == feed.Id);

        var feedType = await DetectFeedTypeAsync(feed.Url);

        var storedFeed = data.Feeds.FirstOrDefault(f => f.Id == feed.Id);
        if (storedFeed != null)
        {
            storedFeed.FeedType = feedType;
            storedFeed.LastRefreshed = DateTime.UtcNow;
        }

        if (parsedFeed != null)
        {
            if (storedFeed != null)
                storedFeed.Title = parsedFeed.Title ?? storedFeed.Title;
            PopulateArticlesFromFeedReader(feed, parsedFeed, data, feedType);
        }
        else
        {
            await PopulateArticlesFromRawXmlAsync(feed, data, feedType);
        }

        _storage.Save(data);
    }

    private void PopulateArticlesFromFeedReader(
        Feed feed, CodeHollow.FeedReader.Feed parsedFeed, AppData data, FeedType feedType)
    {
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

            if (feedType == FeedType.Podcast)
            {
                var (audioUrl, duration, author) = ExtractPodcastEpisodeMetadata(item);
                article.AudioUrl = audioUrl;
                article.Duration = duration;
                article.EpisodeAuthor = author;
            }

            data.Articles.Add(article);
        }
    }

    private async Task PopulateArticlesFromRawXmlAsync(
        Feed feed, AppData data, FeedType feedType)
    {
        string xmlText;
        try
        {
            xmlText = await _httpClient.GetStringAsync(feed.Url);
        }
        catch
        {
            return;
        }

        var xml = XDocument.Parse(xmlText);
        var root = xml.Root;
        if (root == null) return;

        var isAtom = root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase);
        XElement? channel = isAtom ? root : null;

        if (!isAtom)
        {
            foreach (var el in root.Elements())
            {
                if (el.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase))
                {
                    channel = el;
                    break;
                }
            }
        }

        if (channel == null) return;

        foreach (var el in channel.Elements())
        {
            if (el.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                feed.Title = string.IsNullOrWhiteSpace(el.Value) ? feed.Title : el.Value.Trim();
            }
        }

        foreach (var el in channel.Elements())
        {
            var localName = el.Name.LocalName;
            if (!localName.Equals("item", StringComparison.OrdinalIgnoreCase) &&
                !localName.Equals("entry", StringComparison.OrdinalIgnoreCase))
                continue;

            var itemTitle = "";
            var itemLink = "";
            var itemDesc = "";
            var itemDate = DateTime.UtcNow;
            string? itemImage = null;
            string? audioUrl = null;
            string? duration = null;
            string? episodeAuthor = null;

            foreach (var child in el.Elements())
            {
                var cname = child.Name.LocalName;
                var cns = child.Name.NamespaceName;

                if (cname.Equals("title", StringComparison.OrdinalIgnoreCase))
                {
                    itemTitle = child.Value.Trim();
                }
                else if (cname.Equals("link", StringComparison.OrdinalIgnoreCase))
                {
                    var href = child.Attribute("href")?.Value ?? child.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        var rel = child.Attribute("rel")?.Value ?? "";
                        if (!rel.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
                            itemLink = href;
                    }
                }
                else if (cname.Equals("description", StringComparison.OrdinalIgnoreCase) ||
                         cname.Equals("summary", StringComparison.OrdinalIgnoreCase) ||
                         cname.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                         (cname.Equals("encoded", StringComparison.OrdinalIgnoreCase) &&
                          cns.Contains("content", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrWhiteSpace(itemDesc) || child.Value.Trim().Length > itemDesc.Length)
                        itemDesc = child.Value.Trim();
                }
                else if (cname.Equals("pubDate", StringComparison.OrdinalIgnoreCase) ||
                         cname.Equals("published", StringComparison.OrdinalIgnoreCase) ||
                         cname.Equals("updated", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.TryParse(child.Value, out var dt))
                        itemDate = dt;
                }
                else if (cname.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
                {
                    var encUrl = child.Attribute("url")?.Value?.Trim();
                    var encType = child.Attribute("type")?.Value ?? "";
                    if (!string.IsNullOrWhiteSpace(encUrl) && Uri.TryCreate(encUrl, UriKind.Absolute, out _))
                    {
                        if (encType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                            audioUrl = encUrl;
                        else if (encType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                            itemImage = encUrl;
                    }
                }
                else if (cname.Equals("duration", StringComparison.OrdinalIgnoreCase) &&
                         cns.Contains("itunes", StringComparison.OrdinalIgnoreCase))
                {
                    duration = child.Value.Trim();
                }
                else if (cname.Equals("author", StringComparison.OrdinalIgnoreCase) &&
                         cns.Contains("itunes", StringComparison.OrdinalIgnoreCase))
                {
                    episodeAuthor = child.Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(itemTitle) && string.IsNullOrWhiteSpace(itemLink))
                continue;

            if (string.IsNullOrWhiteSpace(itemLink) && !string.IsNullOrWhiteSpace(audioUrl))
                itemLink = audioUrl;

            if (itemImage == null)
                itemImage = ExtractFromImgTags(itemDesc, itemLink);

            var article = new Article
            {
                FeedId      = feed.Id,
                FeedTitle   = feed.Title,
                Title       = string.IsNullOrWhiteSpace(itemTitle) ? "(No title)" : itemTitle,
                Url         = itemLink,
                Description = StripHtml(itemDesc),
                ImageUrl    = itemImage,
                PublishedAt = itemDate,
                AudioUrl    = audioUrl,
                Duration    = duration,
                EpisodeAuthor = episodeAuthor
            };

            data.Articles.Add(article);
        }
    }

    private static void ExtractPodcastFeedMetadata(Feed feed, CodeHollow.FeedReader.Feed parsedFeed)
    {
        if (parsedFeed.SpecificFeed?.Element != null)
        {
            var element = parsedFeed.SpecificFeed.Element;
            feed.Author ??= PickFirstElementValue(element, ItunesNs + "author")
                         ?? PickFirstElementValue(element, ItunesNs + "owner")
                         ?? PickFirstElementValue(element, ItunesNs + "name");

            feed.ArtworkUrl ??= PickFirstImageHref(element, ItunesNs + "image")
                             ?? PickFirstImageHref(element, PodcastNs + "image");

            if (feed.ArtworkUrl == null && parsedFeed.ImageUrl != null)
                feed.ArtworkUrl = parsedFeed.ImageUrl;
        }
    }

    private static (string? AudioUrl, string? Duration, string? Author) ExtractPodcastEpisodeMetadata(FeedItem item)
    {
        string? audioUrl = null;
        string? duration = null;
        string? author = null;

        if (item.SpecificItem != null)
        {
            var element = item.SpecificItem.Element;
            if (element != null)
            {
                audioUrl = ExtractAudioEnclosure(item)
                        ?? PickFirstEnclosureAudio(element)
                        ?? PickMediaAudio(element);

                duration = PickFirstElementValue(element, ItunesNs + "duration")
                        ?? PickFirstElementValue(element, PodcastNs + "duration");

                author = PickFirstElementValue(element, ItunesNs + "author");
            }
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            audioUrl = ExtractAudioEnclosure(item);
        }

        return (audioUrl, duration, author);
    }

    private static string? ExtractAudioEnclosure(FeedItem item)
    {
        var si = item.SpecificItem;
        if (si == null) return null;

        CodeHollow.FeedReader.Feeds.FeedItemEnclosure? enclosure = null;

        if (si is CodeHollow.FeedReader.Feeds.Rss20FeedItem rss20)
            enclosure = rss20.Enclosure;
        else if (si is CodeHollow.FeedReader.Feeds.Rss092FeedItem rss092)
            enclosure = rss092.Enclosure;
        else if (si is CodeHollow.FeedReader.Feeds.MediaRssFeedItem media)
            enclosure = media.Enclosure;

        if (enclosure?.Url == null) return null;
        if (!IsAbsoluteUrl(enclosure.Url)) return null;

        var mime = enclosure.MediaType ?? "";
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || mime.Length == 0)
            return enclosure.Url;

        return null;
    }

    private static string? PickFirstEnclosureAudio(XElement element)
    {
        foreach (var enc in element.Descendants("enclosure"))
        {
            var type = enc.Attribute("type")?.Value ?? "";
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                var url = System.Net.WebUtility.HtmlDecode(enc.Attribute("url")?.Value?.Trim() ?? "");
                if (IsAbsoluteUrl(url)) return url;
            }
        }
        return null;
    }

    private static string? PickMediaAudio(XElement element)
    {
        foreach (var media in element.Descendants(MediaNs + "content"))
        {
            var medium = media.Attribute("medium")?.Value ?? "";
            var type = media.Attribute("type")?.Value ?? "";
            if (medium.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                var url = System.Net.WebUtility.HtmlDecode(media.Attribute("url")?.Value?.Trim() ?? "");
                if (IsAbsoluteUrl(url)) return url;
            }
        }
        return null;
    }

    private static string? PickFirstElementValue(XElement parent, XName name)
    {
        var el = parent.Descendants(name).FirstOrDefault();
        return el?.Value?.Trim();
    }

    private static string? PickFirstImageHref(XElement parent, XName name)
    {
        var el = parent.Descendants(name).FirstOrDefault();
        var href = el?.Attribute("href")?.Value?.Trim();
        return !string.IsNullOrWhiteSpace(href) && IsAbsoluteUrl(href) ? href : null;
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