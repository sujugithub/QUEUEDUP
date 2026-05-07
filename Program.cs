using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using QUEUEDUP.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// Configurable DB path — set DbPath env var in Azure to "Data Source=/home/data/queuedup.db"
var dbPath = builder.Configuration["DbPath"] ?? "Data Source=queuedup.db";
var dbFile = dbPath.Replace("Data Source=", "").Trim();
var dbDir  = Path.GetDirectoryName(Path.GetFullPath(dbFile));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(dbPath));
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromDays(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

// Trust Azure's reverse proxy so HTTPS cookies work correctly
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""EmailSignups"" (
            ""Id""         INTEGER NOT NULL CONSTRAINT ""PK_EmailSignups"" PRIMARY KEY AUTOINCREMENT,
            ""Email""      TEXT    NOT NULL,
            ""SignedUpAt"" TEXT    NOT NULL
        )");
    db.Database.ExecuteSqlRaw(@"
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_EmailSignups_Email"" ON ""EmailSignups"" (""Email"")");
}

app.Run();
