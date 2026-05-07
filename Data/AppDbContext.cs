using Microsoft.EntityFrameworkCore;

namespace QUEUEDUP.Data;

public class SpotifyUser
{
    public string   Id            { get; set; } = "";
    public string   DisplayName   { get; set; } = "";
    public string   AccessToken   { get; set; } = "";
    public string   RefreshToken  { get; set; } = "";
    public DateTime TokenExpiry   { get; set; }
}

public class RankSnapshot
{
    public int      Id            { get; set; }
    public string   SpotifyUserId { get; set; } = "";
    public string   Type          { get; set; } = "";
    public string   TimeRange     { get; set; } = "";
    public DateTime Date          { get; set; }
    public string   ItemsJson     { get; set; } = "";
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SpotifyUser>  SpotifyUsers  => Set<SpotifyUser>();
    public DbSet<RankSnapshot> RankSnapshots => Set<RankSnapshot>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SpotifyUser>().HasKey(u => u.Id);
        mb.Entity<RankSnapshot>()
          .HasIndex(s => new { s.SpotifyUserId, s.Type, s.TimeRange, s.Date })
          .IsUnique();
    }
}
