namespace RssReader;

// ─────────────────────────────────────────────────────────────
// A "Feed" is one RSS subscription the user has added.
// For example: BBC News at https://feeds.bbci.co.uk/news/rss.xml
// ─────────────────────────────────────────────────────────────
public class Feed
{
    // A unique ID we generate automatically so we can identify each feed
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // The RSS URL the user pasted in
    public string Url { get; set; } = string.Empty;

    // The feed's name, e.g. "BBC News" — we read this from the feed itself
    public string Title { get; set; } = string.Empty;

    // When the user subscribed
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // When we last fetched new articles (null = never refreshed yet)
    public DateTime? LastRefreshed { get; set; }
}

// ─────────────────────────────────────────────────────────────
// An "Article" is one news item/post inside a feed.
// ─────────────────────────────────────────────────────────────
public class Article
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Which feed this article came from
    public string FeedId { get; set; } = string.Empty;

    // The feed's display name (so we can show "BBC News" next to the article)
    public string FeedTitle { get; set; } = string.Empty;

    // The article headline
    public string Title { get; set; } = string.Empty;

    // The link to read the full article on the original website
    public string Url { get; set; } = string.Empty;

    // A short description / excerpt (HTML tags stripped out)
    public string Description { get; set; } = string.Empty;

    // When the article was published
    public DateTime PublishedAt { get; set; }
}

// ─────────────────────────────────────────────────────────────
// This is the shape of the entire subscriptions.json file.
// It holds all feeds and all cached articles together.
// ─────────────────────────────────────────────────────────────
public class AppData
{
    public List<Feed> Feeds { get; set; } = new();
    public List<Article> Articles { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────
// What the frontend sends when adding a new feed
// ─────────────────────────────────────────────────────────────
public class AddFeedRequest
{
    public string Url { get; set; } = string.Empty;
}
