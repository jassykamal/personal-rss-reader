using System.Security.Claims;
using System.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RssReader;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=rssreader.db"));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.SignIn.RequireConfirmedEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<IEmailService, EmailService>();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

// ── Existing endpoints (no auth required) ───────────────────────

app.MapGet("/api/feeds", (StorageService storage) =>
{
    var data = storage.Load();
    return Results.Ok(data.Feeds);
});

app.MapPost("/api/feeds", async (AddFeedRequest request, StorageService storage, FeedService feedService) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "Please enter a feed URL." });

    var url = request.Url.Trim();

    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "URL must start with http:// or https://" });

    var title = await feedService.ValidateFeedAsync(url);
    if (title == null)
        return Results.BadRequest(new { error = "Could not read this URL as an RSS/Atom feed." });

    var data = storage.Load();

    if (data.Feeds.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = "You are already subscribed to this feed." });

    var newFeed = new Feed { Url = url, Title = title };
    data.Feeds.Add(newFeed);
    storage.Save(data);

    await feedService.RefreshFeedAsync(newFeed);

    return Results.Created($"/api/feeds/{newFeed.Id}", newFeed);
}).DisableAntiforgery();

app.MapDelete("/api/feeds/{id}", (string id, StorageService storage) =>
{
    var data = storage.Load();
    var feed = data.Feeds.FirstOrDefault(f => f.Id == id);
    if (feed == null)
        return Results.NotFound(new { error = "Feed not found." });

    data.Feeds.Remove(feed);
    data.Articles.RemoveAll(a => a.FeedId == id);
    storage.Save(data);

    return Results.Ok(new { message = $"'{feed.Title}' and its articles have been removed." });
}).DisableAntiforgery();

app.MapPost("/api/feeds/{id}/refresh", async (string id, StorageService storage, FeedService feedService) =>
{
    var data = storage.Load();
    var feed = data.Feeds.FirstOrDefault(f => f.Id == id);
    if (feed == null)
        return Results.NotFound(new { error = "Feed not found." });

    await feedService.RefreshFeedAsync(feed);

    return Results.Ok(new { message = $"'{feed.Title}' has been refreshed." });
}).DisableAntiforgery();

app.MapGet("/api/articles", (StorageService storage) =>
{
    var data = storage.Load();
    var articles = data.Articles
        .OrderByDescending(a => a.PublishedAt)
        .ToList();
    return Results.Ok(articles);
});

// ── Auth endpoints ──────────────────────────────────────────────

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
        return Results.Ok(new { authenticated = false });

    return Results.Ok(new
    {
        authenticated = true,
        email = user.FindFirstValue(ClaimTypes.Email),
        username = user.FindFirstValue(ClaimTypes.Name)
    });
});

app.MapPost("/api/auth/register", async (
    RegisterRequest req,
    UserManager<ApplicationUser> userManager,
    IEmailService emailService,
    IConfiguration config,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Email and password are required." });

    if (req.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    var existing = await userManager.FindByEmailAsync(req.Email);
    if (existing != null)
        return Results.BadRequest(new { error = "An account with this email already exists." });

    var user = new ApplicationUser
    {
        UserName = req.Email,
        Email = req.Email
    };

    var result = await userManager.CreateAsync(user, req.Password);
    if (!result.Succeeded)
    {
        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
        return Results.BadRequest(new { error = errors });
    }

    var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
    var encodedToken = HttpUtility.UrlEncode(token);
    var baseUrl = GetBaseUrl(httpContext, config);
    var callbackUrl = $"{baseUrl}/verify-email.html?userId={user.Id}&token={encodedToken}";

    var body = $@"<div style='font-family:sans-serif;max-width:480px;margin:0 auto;padding:32px'>
      <h2>Verify your email</h2>
      <p>Click the button below to verify your email address and activate your RSS Reader account.</p>
      <a href='{callbackUrl}' style='display:inline-block;padding:12px 24px;background:#0d9488;color:#fff;border-radius:6px;text-decoration:none;font-weight:700'>Verify Email</a>
      <p style='color:#888;font-size:0.85rem;margin-top:24px'>If you did not create this account, you can ignore this email.</p>
    </div>";

    var sent = await emailService.SendEmailAsync(req.Email, "Verify your RSS Reader account", body);

    if (sent)
        return Results.Ok(new { message = "Account created. Please check your email to verify your account." });

    return Results.Ok(new
    {
        message = "Account created. SMTP not configured — use the link below to verify.",
        verificationUrl = callbackUrl
    });
}).DisableAntiforgery();

app.MapPost("/api/auth/login", async (
    LoginRequest req,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Email and password are required." });

    var user = await userManager.FindByEmailAsync(req.Email);
    if (user == null)
        return Results.BadRequest(new { error = "Invalid email or password." });

    if (!user.EmailConfirmed)
        return Results.BadRequest(new { error = "Please verify your email before signing in. Check your inbox for a verification link." });

    var result = await signInManager.PasswordSignInAsync(
        user, req.Password, req.RememberMe, lockoutOnFailure: false);

    if (!result.Succeeded)
        return Results.BadRequest(new { error = "Invalid email or password." });

    return Results.Ok(new
    {
        message = "Logged in successfully.",
        email = user.Email,
        username = user.UserName
    });
}).DisableAntiforgery();

app.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok(new { message = "Logged out." });
});

// ── CSRF token endpoint ─────────────────────────────────────

app.MapGet("/api/antiforgery/token", (IAntiforgery antiforgery, HttpContext context) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
});

// ── Email verification ───────────────────────────────────────

app.MapGet("/api/auth/confirm-email", async (
    string userId,
    string token,
    UserManager<ApplicationUser> userManager) =>
{
    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        return Results.BadRequest(new { error = "Invalid verification link." });

    var user = await userManager.FindByIdAsync(userId);
    if (user == null)
        return Results.BadRequest(new { error = "Invalid verification link." });

    var decodedToken = HttpUtility.UrlDecode(token);

    var result = await userManager.ConfirmEmailAsync(user, decodedToken);
    if (!result.Succeeded)
        return Results.BadRequest(new { error = "Verification link is invalid or has expired." });

    return Results.Ok(new { message = "Email verified successfully." });
});

app.MapPost("/api/auth/resend-confirmation", async (
    LoginRequest req,
    UserManager<ApplicationUser> userManager,
    IEmailService emailService,
    IConfiguration config,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest(new { error = "Email is required." });

    var user = await userManager.FindByEmailAsync(req.Email);
    if (user == null || user.EmailConfirmed)
        return Results.Ok(new { message = "If your email is registered and not yet verified, a new verification email has been sent." });

    var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
    var encodedToken = HttpUtility.UrlEncode(token);
    var baseUrl = GetBaseUrl(httpContext, config);
    var callbackUrl = $"{baseUrl}/verify-email.html?userId={user.Id}&token={encodedToken}";

    var body = $@"<div style='font-family:sans-serif;max-width:480px;margin:0 auto;padding:32px'>
      <h2>Verify your email</h2>
      <p>Click the button below to verify your email address and activate your RSS Reader account.</p>
      <a href='{callbackUrl}' style='display:inline-block;padding:12px 24px;background:#0d9488;color:#fff;border-radius:6px;text-decoration:none;font-weight:700'>Verify Email</a>
      <p style='color:#888;font-size:0.85rem;margin-top:24px'>If you did not request this, you can ignore this email.</p>
    </div>";

    var sent = await emailService.SendEmailAsync(req.Email, "Verify your RSS Reader account", body);

    if (sent)
        return Results.Ok(new { message = "If your email is registered and not yet verified, a new verification email has been sent." });

    return Results.Ok(new
    {
        message = "SMTP not configured — use the link below.",
        verificationUrl = callbackUrl
    });
}).DisableAntiforgery();

// ── Favorites endpoints ─────────────────────────────────────────

app.MapGet("/api/favorites", async (
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (userId == null) return Results.Unauthorized();

    var favorites = await db.FavoriteArticles
        .Where(f => f.UserId == userId)
        .OrderByDescending(f => f.SavedAt)
        .ToListAsync();

    return Results.Ok(favorites);
}).RequireAuthorization();

app.MapPost("/api/favorites", async (
    FavoriteRequest req,
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var existing = await db.FavoriteArticles
        .FirstOrDefaultAsync(f => f.UserId == userId && f.ArticleUrl == req.ArticleUrl);

    if (existing != null)
    {
        db.FavoriteArticles.Remove(existing);
        await db.SaveChangesAsync();
        return Results.Ok(new { favorited = false });
    }

    db.FavoriteArticles.Add(new FavoriteArticle
    {
        UserId = userId,
        ArticleUrl = req.ArticleUrl,
        ArticleTitle = req.ArticleTitle,
        FeedTitle = req.FeedTitle,
        ImageUrl = req.ImageUrl,
        SavedAt = DateTime.UtcNow
    });

    await db.SaveChangesAsync();
    return Results.Ok(new { favorited = true });
}).RequireAuthorization();

app.MapGet("/api/favorites/check", async (
    string url,
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Ok(new { favorited = false });

    var favorited = await db.FavoriteArticles
        .AnyAsync(f => f.UserId == userId && f.ArticleUrl == url);

    return Results.Ok(new { favorited });
}).RequireAuthorization();

// ── Recently Viewed ─────────────────────────────────────────────

app.MapPost("/api/recently-viewed", async (
    ViewArticleRequest req,
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Ok(new { recorded = false });

    var existing = await db.RecentlyViewedArticles
        .FirstOrDefaultAsync(r => r.UserId == userId && r.ArticleUrl == req.ArticleUrl);

    if (existing != null)
    {
        existing.ViewedAt = DateTime.UtcNow;
        existing.ArticleTitle = req.ArticleTitle;
        existing.FeedTitle = req.FeedTitle;
        existing.ImageUrl = req.ImageUrl;
    }
    else
    {
        db.RecentlyViewedArticles.Add(new RecentlyViewedArticle
        {
            UserId = userId,
            ArticleUrl = req.ArticleUrl,
            ArticleTitle = req.ArticleTitle,
            FeedTitle = req.FeedTitle,
            ImageUrl = req.ImageUrl,
            ViewedAt = DateTime.UtcNow
        });
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { recorded = true });
}).DisableAntiforgery();

app.MapGet("/api/recently-viewed", async (
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var recent = await db.RecentlyViewedArticles
        .Where(r => r.UserId == userId)
        .OrderByDescending(r => r.ViewedAt)
        .Take(50)
        .ToListAsync();

    return Results.Ok(recent);
}).RequireAuthorization();

// ── Reading History ─────────────────────────────────────────────

app.MapPost("/api/history", async (
    ViewArticleRequest req,
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Ok(new { recorded = false });

    db.ReadingHistories.Add(new ReadingHistory
    {
        UserId = userId,
        ArticleUrl = req.ArticleUrl,
        ArticleTitle = req.ArticleTitle,
        FeedTitle = req.FeedTitle,
        ReadAt = DateTime.UtcNow
    });

    await db.SaveChangesAsync();
    return Results.Ok(new { recorded = true });
}).DisableAntiforgery();

app.MapGet("/api/history", async (
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var history = await db.ReadingHistories
        .Where(r => r.UserId == userId)
        .OrderByDescending(r => r.ReadAt)
        .Take(100)
        .ToListAsync();

    return Results.Ok(history);
}).RequireAuthorization();

app.MapFallbackToFile("index.html");

app.Run();

static string GetBaseUrl(HttpContext httpContext, IConfiguration config)
{
    if (httpContext.Request.Host.HasValue)
    {
        var scheme = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? httpContext.Request.Scheme;
        return $"{scheme}://{httpContext.Request.Host}";
    }
    return config["AppBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5000";
}

// Helper for consistent userId access
static partial class Program
{
    public static string? GetUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier);
}
