using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameVault.Models;

namespace GameVault.Controllers
{
    public class VideoGamesController : Controller
    {
        private readonly ApplicationDbContext _ctx;
        public VideoGamesController(ApplicationDbContext ctx)
            => _ctx = ctx;

        public async Task<IActionResult> Index()
            => View(await _ctx.VideoGames.AsNoTracking().ToListAsync());

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();
            var game = await _ctx.VideoGames
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id);
            if (game == null) return NotFound();
            return View(game);
        }

        public IActionResult Create() => View();

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(VideoGames game)
        {
            Console.WriteLine("POST Create triggered");
            if (!ModelState.IsValid)
            {
                Console.WriteLine("Model invalid");
                return View(game);
            }

            _ctx.Add(game);
            await _ctx.SaveChangesAsync();
            Console.WriteLine("Saved successfully");

            return RedirectToAction(nameof(Index));
        }


        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var game = await _ctx.VideoGames.FindAsync(id);
            if (game == null) return NotFound();
            return View(game);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, VideoGames game)
        {
            if (id != game.Id) return BadRequest();
            if (!ModelState.IsValid) return View(game);
            _ctx.Update(game);
            await _ctx.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();
            var game = await _ctx.VideoGames
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id);
            if (game == null) return NotFound();
            return View(game);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var game = await _ctx.VideoGames.FindAsync(id);
            if (game != null) _ctx.VideoGames.Remove(game);
            await _ctx.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}