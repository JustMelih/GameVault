using GameVault;
using GameVault.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net;

// >>> Yeni using'ler (resolver ve title-intent service kendi namespace'in neyse onu ekle)
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = true;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Access/Denied";
});

builder.Services.AddMemoryCache();

// --- RAWG CLIENT (ayný kalabilir) ---
builder.Services.AddHttpClient<IRawgClient, RawgClient>(client =>
{
    client.BaseAddress = new Uri("https://api.rawg.io/api/");
    client.Timeout = TimeSpan.FromSeconds(8); // biraz sýký
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GameVault/1.0 (+https://gamevault.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
});

// --- YENÝ: AI sadece baþlýk listesi servisimiz ---
builder.Services.AddHttpClient<ITitleIntentService, TitleIntentService>(client =>
{
    var baseUrl = builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(99);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
});

// --- YENÝ: RAWG baþlýk çözümleyici (ilk kabul edilebilir sonucu seçer) ---
builder.Services.AddSingleton<IRawgResolver, RawgResolver>();

// --- DB ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    RoleSeeder.SeedAsync(services).GetAwaiter().GetResult();
}

app.Run();
