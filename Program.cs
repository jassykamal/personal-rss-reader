// ─────────────────────────────────────────────────────────────
// Program.cs — The entry point and heart of the application.
//
// In ASP.NET Core Minimal API, this one file:
//   1. Configures the app (services, middleware)
//   2. Defines all the API routes (endpoints)
//   3. Starts the web server
//
// An "endpoint" is a URL the browser can call to do something.
// Think of it like a menu of actions the server can perform.
// ─────────────────────────────────────────────────────────────

using RssReader;

var builder = WebApplication.CreateBuilder(args);

// ── REGISTER SERVICES ─────────────────────────────────────────
// "Singleton" means: create one instance and reuse it everywhere.
// ASP.NET will automatically pass these into our endpoints.
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<FeedService>();

// Allow the browser (frontend) to talk to this backend.
// Without this, the browser blocks requests for security reasons (CORS policy).
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Railway (and most cloud platforms) assign a random port via the PORT
// environment variable. We read it here so the app works both locally
// (defaults to 5000) and in the cloud (uses whatever port Railway assigns).
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseCors();

// Serve static files (index.html, CSS, JS) from the wwwroot/ folder.
// This is what the user sees when they open the browser.
app.UseStaticFiles();

// ─────────────────────────────────────────────────────────────
// ENDPOINT 1: GET /api/feeds
// Returns the list of all subscribed feeds.
// The browser calls this when the page loads to show the sidebar.
// ─────────────────────────────────────────────────────────────
app.MapGet("/api/feeds", (StorageService storage) =>
{
    var data = storage.Load();
    return Results.Ok(data.Feeds);
});

// ─────────────────────────────────────────────────────────────
// ENDPOINT 2: POST /api/feeds
// Adds a new feed subscription.
// The browser sends the URL the user typed, we validate it,
// save it, then immediately fetch its articles.
// ─────────────────────────────────────────────────────────────
app.MapPost("/api/feeds", async (AddFeedRequest request, StorageService storage, FeedService feedService) =>
{
    // Basic check: did the user actually type something?
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "Please enter a feed URL." });

    // Normalize the URL (trim spaces, lowercase)
    var url = request.Url.Trim();

    // Make sure the URL starts with http:// or https://
    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "URL must start with http:// or https://" });

    // Try to read the feed — if it fails, it's not a valid RSS/Atom feed
    var title = await feedService.ValidateFeedAsync(url);
    if (title == null)
        return Results.BadRequest(new { error = "Could not read this URL as an RSS/Atom feed. Make sure it's a feed URL, not a regular website URL." });

    var data = storage.Load();

    // Don't allow duplicate subscriptions
    if (data.Feeds.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = "You are already subscribed to this feed." });

    // Create and save the new feed
    var newFeed = new Feed { Url = url, Title = title };
    data.Feeds.Add(newFeed);
    storage.Save(data);

    // Immediately fetch articles so the user sees content right away
    await feedService.RefreshFeedAsync(newFeed);

    return Results.Created($"/api/feeds/{newFeed.Id}", newFeed);
});

// ─────────────────────────────────────────────────────────────
// ENDPOINT 3: DELETE /api/feeds/{id}
// Removes a feed and all its articles.
// The {id} part is a "route parameter" — it changes per request.
// Example: DELETE /api/feeds/abc-123 removes the feed with id "abc-123"
// ─────────────────────────────────────────────────────────────
app.MapDelete("/api/feeds/{id}", (string id, StorageService storage) =>
{
    var data = storage.Load();

    var feed = data.Feeds.FirstOrDefault(f => f.Id == id);
    if (feed == null)
        return Results.NotFound(new { error = "Feed not found." });

    // Remove the feed itself
    data.Feeds.Remove(feed);

    // Remove ALL articles that came from this feed.
    // This keeps the data clean — no orphaned articles from deleted feeds.
    data.Articles.RemoveAll(a => a.FeedId == id);

    storage.Save(data);

    return Results.Ok(new { message = $"'{feed.Title}' and its articles have been removed." });
});

// ─────────────────────────────────────────────────────────────
// ENDPOINT 4: POST /api/feeds/{id}/refresh
// Fetches the latest articles for a specific feed.
// The user clicks a "Refresh" button in the sidebar to trigger this.
// ─────────────────────────────────────────────────────────────
app.MapPost("/api/feeds/{id}/refresh", async (string id, StorageService storage, FeedService feedService) =>
{
    var data = storage.Load();

    var feed = data.Feeds.FirstOrDefault(f => f.Id == id);
    if (feed == null)
        return Results.NotFound(new { error = "Feed not found." });

    await feedService.RefreshFeedAsync(feed);

    return Results.Ok(new { message = $"'{feed.Title}' has been refreshed." });
});

// ─────────────────────────────────────────────────────────────
// ENDPOINT 5: GET /api/articles
// Returns ALL articles from ALL feeds, sorted newest first.
// This powers the main "river of news" reading view.
// ─────────────────────────────────────────────────────────────
app.MapGet("/api/articles", (StorageService storage) =>
{
    var data = storage.Load();

    var articles = data.Articles
        .OrderByDescending(a => a.PublishedAt) // Newest first
        .ToList();

    return Results.Ok(articles);
});

// ─────────────────────────────────────────────────────────────
// Default route: serve index.html for the root URL "/"
// When the user opens http://localhost:5000, they get the app.
// ─────────────────────────────────────────────────────────────
app.MapFallbackToFile("index.html");

app.Run();
