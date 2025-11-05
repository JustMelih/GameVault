using Microsoft.AspNetCore.Identity;
using GameVault.Models;
using Microsoft.EntityFrameworkCore;
using GameVault;
using GameVault.Integrations.Rawg;


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

// RAWG client
builder.Services.AddHttpClient<IRawgClient, RawgClient>(c =>
{
    var cfg = builder.Configuration.GetSection("Rawg");
    var baseUrl = cfg["BaseUrl"] ?? "https://api.rawg.io/api/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(6);
});

//  Always register the OpenAI `IntentService`. No config gating here.
// LLM client — ALWAYS register, don't gate on config checks
builder.Services.AddHttpClient<IIntentService, IntentService>(c =>
{
    var baseUrl = builder.Configuration["LLM:BaseUrl"] ?? "https://api.openai.com/v1/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(12);
});


// DB
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
