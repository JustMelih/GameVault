using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameVault.Models
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        // DbContext = Ana EFC sınıfı. Veritabanı ile bağlanıt seansı oluşturmamızı sağlar.
        // IdentityDbContext = DbContext'in özel bir versiyonu, tüm Identity (Users, Roles, Claims vb.) tabloları ile birlikte hazır halde gelir.
        // ApplicationUser = Default olan "IdentityUser" kullanmak yerine kendimizin oluşturduğu "ApplicationUser" sınıfını kullanır.
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }
        // DbContext'in constructor'u. Veritabanına nasıl bağlanacağımız hakkında ki ayarları içerir. Bu ayarları : base(options) ile DbContext sınıfına aktarır.

        public DbSet<VideoGames> VideoGames { get; set; }
        // DbSet veritabanında ki bir tabloyu temsil eder. Burada VideoGames modelinden baz alınarak veritabanında bir VideoGames tablosu oluşturulur.
        // Her bir 'DbSet' veritabanında ki bir tablodur.

    }
}
