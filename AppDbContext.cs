using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RssReader;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<FavoriteArticle> FavoriteArticles => Set<FavoriteArticle>();
    public DbSet<RecentlyViewedArticle> RecentlyViewedArticles => Set<RecentlyViewedArticle>();
    public DbSet<ReadingHistory> ReadingHistories => Set<ReadingHistory>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<FavoriteArticle>(e =>
        {
            e.HasIndex(f => new { f.UserId, f.ArticleUrl }).IsUnique();
        });

        builder.Entity<RecentlyViewedArticle>(e =>
        {
            e.HasIndex(r => r.UserId);
        });

        builder.Entity<ReadingHistory>(e =>
        {
            e.HasIndex(r => r.UserId);
        });
    }
}
