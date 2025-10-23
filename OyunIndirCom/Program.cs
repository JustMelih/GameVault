using Microsoft.AspNetCore.Identity; //Imports the identity types (UserManager, SignInManager etc.) 
using OyunIndirCom.Models; 
using Microsoft.EntityFrameworkCore;
using OyunIndirCom;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options => // Haz�r gelen kullan�c� sistemini etkinle�tirir. (Kullan�c�lar + Roller)
{
    options.Password.RequireDigit = true; // �ifre en az bir rakam i�ermeli.
    options.Password.RequiredLength = 6; // �ifre minimum uzunlu�u 6 karakter olmal�
    options.Password.RequireNonAlphanumeric = true; // �ifre bir sembol i�ermeli.
    options.User.RequireUniqueEmail = true; // Her kullan�c� unique bir e-mail'e sahip olmal�.
})
.AddEntityFrameworkStores<ApplicationDbContext>() // Identity sisteminin, kullan�c� ve rollerini EFC �zerinden veritaban�na kaydetmesini sa�lar.
.AddDefaultTokenProviders(); // �ifre s�f�rlama ve e-mail do�rulama tokenlerini otomatik olarak olu�turmam�z� sa�lar.

builder.Services.ConfigureApplicationCookie(options => // Login cookie'nin nas�l �al��mas� ve uygulaman�n giri� yapan kullan�c�lar� nereye y�nlendirece�ini ayarlamam�z� sa�lar.
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Access/Denied";
});

var connectionString = builder.Configuration 
    .GetConnectionString("DefaultConnection") // Veritaban� ba�lant� string'ini okur.
    ?? throw new InvalidOperationException( // Connection string bulunmad���nda hata verir.
        "Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));   // DI container'a 'ApplicationDbContext'i ekler ve EFC'a SQL serveri kullanmas�n� s�yler.

var app = builder.Build(); // Uygulama kurulumunu tamamlar.
// "MIDDLEWARES"
app.UseHttpsRedirection();// Kullan�c�lar� daha g�venlikli olan sitenin "https" versiyonunu kulland�r�r. 
app.UseStaticFiles(); // wwwroot dosyas�ndakileri kullanmam�z� sa�lar.
app.UseRouting(); // URL matching'i etkinle�tirir ve uygulaman�n hangi controller'�n veya action'un hangi URL'yi kar��layaca��n� belirler.
app.UseAuthentication(); // Login cookie'yi okuyarak iste�in kim taraf�ndan g�nderildi�ini g�r�r.
app.UseAuthorization(); // Kullan�c�n�n ula�maya �al��t��� kayna�a ula�ma izni olup olmad���n� kontrol eder.
app.MapControllerRoute( // Default rotay� olu�turur.
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope()) // Ge�ici olarak bir servis olu�turur bu sayede ba�lang�� �ncesinde veritaban�nda gerekli rollerin olu�mas�n� sa�lar.
{
    var services = scope.ServiceProvider;
    RoleSeeder.SeedAsync(services).GetAwaiter().GetResult();
}

app.Run();
