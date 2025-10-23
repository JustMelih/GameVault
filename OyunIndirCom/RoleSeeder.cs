using Microsoft.AspNetCore.Identity; // Identity'i ekler ve ondan kalıtım alan kendi ApplicationUser'ı ekler.
using OyunIndirCom.Models;

public static class RoleSeeder // Static bir sınıf oluşturur. Obje oluşturmamıza izin vermez, sadece method çağırabiliriz.
{
    public static async Task SeedAsync(IServiceProvider services) // UserManager ve RoleManager gibi servisleri çağırmamızı sağlar. async Task olduğu için uyg'ı bloklamadan çalışabilir.
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>(); // Rol oluşturur, siler, varlığını kontrol eder. (Admin, user, moderator vs.)
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>(); // Kullanıcı oluşturur, bulur, şifre kontrol eder, kullanıcıya rol verir veya kaldırır.

        string[] roles = new[] { "User", "Moderator", "Admin" }; // Rol isimleri için basit bir array.

        foreach (var role in roles) 
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
            // Eğer rol yoksa, oluştur.
        }

        var adminEmail = "admin@localhost";
        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = "admin",
                Email = adminEmail,
                EmailConfirmed = true
            };

            // Default admin rolü ve kullanıcı oluşturuyoruz çünkü uygulama ilk çalıştığında hiçbir rol olmadığı için.

            var result = await userManager.CreateAsync(adminUser, "Admin123!"); // change this
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(adminUser, "Admin");
            }
        }
    }
}
