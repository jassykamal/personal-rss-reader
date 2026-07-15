using System.Text.RegularExpressions;
using CodeHollow.FeedReader;

namespace RssReader;

// ─────────────────────────────────────────────────────────────
// FeedService is responsible for talking to the internet:
// - Checking whether a URL is a valid RSS/Atom feed
// - Downloading articles from a feed and saving them
//
// It uses the CodeHollow.FeedReader package to do the heavy
// lifting — that package understands all RSS/Atom versions so
// we don't have to parse XML manually.
// ─────────────────────────────────────────────────────────────
public class FeedService
{
    private readonly StorageService _storage;

    // ASP.NET gives us StorageService automatically (dependency injection)
    public FeedService(StorageService storage)
    {
        _storage = storage;
    }

    // ── VALIDATE ──────────────────────────────────────────────
    // Tries to read the feed at the given URL.
    // Returns the feed's title if it's valid, or null if it fails.
    // We use this before adding a feed to give the user a clear error
    // instead of silently saving a broken URL.
    public async Task<string?> ValidateFeedAsync(string url)
    {
        try
        {
            var feed = await FeedReader.ReadAsync(url);
            // A valid feed must have at least a title
            return string.IsNullOrWhiteSpace(feed?.Title) ? null : feed.Title;
        }
        catch
        {
            // Any network error, parsing error, or non-feed URL returns null
            return null;
        }
    }

    // ── REFRESH ───────────────────────────────────────────────
    // Downloads the latest articles for a given feed and saves them.
    // We replace all old articles for this feed with fresh ones —
    // this keeps the data up to date and avoids duplicates.
    public async Task RefreshFeedAsync(Feed feed)
    {
        CodeHollow.FeedReader.Feed parsedFeed;

        try
        {
            parsedFeed = await FeedReader.ReadAsync(feed.Url);
        }
        catch
        {
            // If the feed is temporarily unreachable, do nothing
            // (don't delete existing articles)
            return;
        }

        var data = _storage.Load();

        // Remove old articles for this feed before adding new ones
        data.Articles.RemoveAll(a => a.FeedId == feed.Id);

        foreach (var item in parsedFeed.Items)
        {
            // Skip items with no title and no link — they're useless
            if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Link))
                continue;

            var rawContent = item.Description ?? item.Content ?? string.Empty;

            var article = new Article
            {
                FeedId    = feed.Id,
                FeedTitle = feed.Title,
                Title     = item.Title ?? "(No title)",
                Url       = item.Link  ?? string.Empty,

                // Strip HTML tags from the description (XSS prevention +
                // cleaner reading experience as requested)
                Description = StripHtml(rawContent),

                // Prefer images already embedded in the feed item content.
                ImageUrl = ExtractImageUrl(rawContent),

                // Use the article's publish date, or now if it's missing
                PublishedAt = item.PublishingDate?.ToUniversalTime() ?? DateTime.UtcNow
            };

            data.Articles.Add(article);
        }

        // Record when we last successfully refreshed this feed
        var storedFeed = data.Feeds.FirstOrDefault(f => f.Id == feed.Id);
        if (storedFeed != null)
            storedFeed.LastRefreshed = DateTime.UtcNow;

        _storage.Save(data);
    }

    // ── STRIP HTML ────────────────────────────────────────────
    // Removes all HTML tags from a string using a regular expression.
    // Example: "<p>Hello <b>world</b></p>" → "Hello world"
    //
    // This is important for security: RSS feeds can contain HTML,
    // and if we display it directly in the browser without stripping,
    // malicious feeds could inject scripts (XSS attack).
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // Remove all HTML tags
        var noTags = Regex.Replace(html, "<[^>]*>", " ");

        // Decode HTML entities like &amp; → &, &lt; → <, &nbsp; → space
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);

        // Collapse multiple spaces/newlines into single spaces
        var clean = Regex.Replace(decoded, @"\s+", " ").Trim();

        return clean;
    }

    private static string? ExtractImageUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = Regex.Match(
            html,
            "<img[^>]+src=[\"'](?<src>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        var src = System.Net.WebUtility.HtmlDecode(match.Groups["src"].Value.Trim());
        return Uri.TryCreate(src, UriKind.Absolute, out _) ? src : null;
    }
}
