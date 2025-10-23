using Microsoft.AspNetCore.Mvc;
using OyunIndirCom.Models;
using Microsoft.EntityFrameworkCore;

namespace OyunIndirCom.Controllers
{
    public class HomeController : Controller // "Controller"dan kalıtım alıyoruz, bu sayede bir çok yardımcı methodlara erişim sağlayabiliyoruz.
    {
        private readonly ApplicationDbContext _context;
        public HomeController (ApplicationDbContext context)
        {
            _context = context;
            // Basitçe: "_context" bizim DbContext'imiz. 
        }
        public IActionResult Index()
        {
            var videoGames = _context.VideoGames.ToList(); // Veritabanında ki tüm oyunları getirir ve hafızaya bir liste olarak atar.
            return View(videoGames); // Listeyi index.cshtml'e gönderir.
        }
        public IActionResult GameGallery(string sort) // URL'den bir sorgu çeker ((Home/GameGallery?sort=newest)
        {
            IQueryable<VideoGames> videoGames = _context.VideoGames; // Henüz çalışmamış bir sorguyu temsil eder. EFC sadece .ToList() veya benzer bir şey tetiklendiğinde sorgular.
                                                                     // IQueryable kullanmak ilk önce her şeyi getirmekten daha verimlidir çünkü sıralama ve filtreleme veritabanında yapılır.
            videoGames = sort switch
            {
                "newest" => videoGames.OrderByDescending(g => g.ReleaseDate),
                "toprated" => videoGames.OrderByDescending(g => g.Rating),
                "mostdownloaded" => videoGames.OrderByDescending(g => g.Downloads),
                "editorschoice" => videoGames.OrderByDescending(g => g.EditorsChoice),
                _ => videoGames.OrderBy(g => g.Id)
            };

            ViewData["CurrentSort"] = sort ?? ""; // ViewData'yı controller'dan view'a ekstra bir bilgi göndermek istediğimizde kullanırız. Burada sort'u gönderiyoruz ki view'de seçili sort'u belirleyebilelim.
            return View(videoGames.ToList());
        }

    }
}
