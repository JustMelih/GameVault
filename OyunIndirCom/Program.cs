using Microsoft.AspNetCore.Identity; //Imports the identity types (UserManager, SignInManager etc.) 
using OyunIndirCom.Models; 
using Microsoft.EntityFrameworkCore;
using OyunIndirCom;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options => // Hazýr gelen kullanýcý sistemini etkinleþtirir. (Kullanýcýlar + Roller)
{
    options.Password.RequireDigit = true; // Þifre en az bir rakam içermeli.
    options.Password.RequiredLength = 6; // Þifre minimum uzunluðu 6 karakter olmalý
    options.Password.RequireNonAlphanumeric = true; // Þifre bir sembol içermeli.
    options.User.RequireUniqueEmail = true; // Her kullanýcý unique bir e-mail'e sahip olmalý.
})
.AddEntityFrameworkStores<ApplicationDbContext>() // Identity sisteminin, kullanýcý ve rollerini EFC üzerinden veritabanýna kaydetmesini saðlar.
.AddDefaultTokenProviders(); // Þifre sýfýrlama ve e-mail doðrulama tokenlerini otomatik olarak oluþturmamýzý saðlar.

builder.Services.ConfigureApplicationCookie(options => // Login cookie'nin nasýl çalýþmasý ve uygulamanýn giriþ yapan kullanýcýlarý nereye yönlendireceðini ayarlamamýzý saðlar.
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Access/Denied";
});

var connectionString = builder.Configuration 
    .GetConnectionString("DefaultConnection") // Veritabaný baðlantý string'ini okur.
    ?? throw new InvalidOperationException( // Connection string bulunmadýðýnda hata verir.
        "Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));   // DI container'a 'ApplicationDbContext'i ekler ve EFC'a SQL serveri kullanmasýný söyler.

var app = builder.Build(); // Uygulama kurulumunu tamamlar.
// "MIDDLEWARES"
app.UseHttpsRedirection();// Kullanýcýlarý daha güvenlikli olan sitenin "https" versiyonunu kullandýrýr. 
app.UseStaticFiles(); // wwwroot dosyasýndakileri kullanmamýzý saðlar.
app.UseRouting(); // URL matching'i etkinleþtirir ve uygulamanýn hangi controller'ýn veya action'un hangi URL'yi karþýlayacaðýný belirler.
app.UseAuthentication(); // Login cookie'yi okuyarak isteðin kim tarafýndan gönderildiðini görür.
app.UseAuthorization(); // Kullanýcýnýn ulaþmaya çalýþtýðý kaynaða ulaþma izni olup olmadýðýný kontrol eder.
app.MapControllerRoute( // Default rotayý oluþturur.
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope()) // Geçici olarak bir servis oluþturur bu sayede baþlangýç öncesinde veritabanýnda gerekli rollerin oluþmasýný saðlar.
{
    var services = scope.ServiceProvider;
    RoleSeeder.SeedAsync(services).GetAwaiter().GetResult();
}

app.Run();
