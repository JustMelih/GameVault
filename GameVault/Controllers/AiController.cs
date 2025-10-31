using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace GameVault.Controllers
{
    [ApiController] // API davranışlarını otomatikleştirir. (Çeşitli hatalarda 400,401,403 gibi kodların dönmesini sağlayarak anlamamıza yardımcı olur.)
    [Route("api/[controller]")] // URL kalıbı. ( "/api/ai" vb.)
    public class AiController : ControllerBase
    {
        private readonly IRawgClient _rawg;
        private readonly IIntentService _intent;            // ⚠ DI Lifetimes altında tekrar açıklanacak. 
        private readonly IMemoryCache _cache;
        private readonly ILogger<AiController> _logger;

        public AiController(IRawgClient rawg, IIntentService intent, IMemoryCache cache, ILogger<AiController> logger)  // CONSTRUCTOR ve "CONSTRUCTOR INJECTION"
        {
            _rawg = rawg;
            _intent = intent;    // " ⚠ DEPENDENCY INJECTION ⚠ " ile dışarıdan gelenleri değiştirilemez (readonly) olarak tanımlıyoruz. Tekrar açıklanacak.
            _cache = cache;
            _logger = logger;
        }

        [HttpPost("search")] // POST /api/ai/search. enpoint
        public async Task<IActionResult> Search([FromBody] SearchRequest req, CancellationToken ct) // ⚠ CancellationToken zinciri, istek iptal edilirse işlemleri durdurabilmek için. Tekrar açıklanacak. 
        {
            if (string.IsNullOrWhiteSpace(req?.Query)) // 'req' null olmasın diye '?.' ile kontrol ediyoruz. ('?.' = eğer soldaki null değilse devam et.')
                return BadRequest(new { error = "Query cannot be empty." }); // Boş arama sorgusuna 400 hata kodu.

            // Basit hız limitleme (throttle)
            var clientKey = $"throttle:{HttpContext.Connection.RemoteIpAddress}";
            if (!TryConsumeToken(clientKey, 5, 10)) // 10 saniyede maksimum 5 istek.
                return StatusCode(429, new { error = "Too many requests. Please wait a few seconds." }); // Aşılırsa '429' kodu.
            // ⚠ Reverse Proxy / X-Forwarded-For. Tekrar açıklanacak.

            var sw = System.Diagnostics.Stopwatch.StartNew(); // Sonuçların kaç ms'de bulunduğunu ölçer.
            var limit = req.Limit <= 0 ? 10 : Math.Min(req.Limit, 20); // Sonuç sayısını limitleyerek API'a gereksiz yük bindirilmemesini sağlar.

            // Intent (Niyet) = Kullanıcının yazdığı cümleden amacı çıkarmak, yani kullanıcnın ne istediğini anlamak.
            ParsedIntent intent; // Kullanıcının 'niyetini' tutan değişken. Henüz içinde veri yok, AI'dan gelen veriyi koyacak.
            var usedFallback = false; // AI cevap vermezse, yedek sisteme geçme durumu. (LLM çözemedi -> klasik arama motoruna dön')
            string? llmError = null; // string? = null olabilir bir string değişkeni. Olası bir AI hatasını yakalamak için.
            var intentKey = "intent:" + req.Query.Trim().ToLowerInvariant(); // Kullanıcının yaptığı aramayı önbelleğe alıp tekrar aynı sorgu kullanılması durumunda AI'a tekrar gitmektense önbellekten 'ucuz' bir şekilde almak. Kullanıcının 'niyet'ini kaydeder.

            if (!_cache.TryGetValue(intentKey, out intent!)) // Cache'te 'intent' var mı yok mu diye kontrol eder. Varsa kullanır, yoksa üretir.
            {
                try
                {
                    intent = await _intent.ParseAsync(req.Query, ct); // 'req.Query' kullanıcının yazdığı metin. 'ct' istek iptal edilirse çağrıyı kesmek için. Çağrı başarılı olursa 'intent' doluyor.
                }
                catch (Exception ex) // Hata ayıklama, LLM patlarsa hatayı yakalıyoruz.
                {
                    // AI hata verse bile program asla ÇÖKMEYECEK. Yedek yönteme kontrollü GERİ ÇEKİLECEK (Fallback)
                    _logger.LogWarning(ex, "LLM failed for '{Query}', using heuristic fallback.", req.Query); // Log'a AI'ın başarısız olduğu query'i kaydeder.
                    llmError = ex.Message; // Hata sebebini UI'da göstermek için kullanılabilir.
                    intent = FallbackIntent(req.Query); // AI dışı, kendimizin manuel olarak yazdığı kurallarla yedek bir 'intent' üretilir.
                    usedFallback = true; // Fallback kullandığımızı belirtiyoruz. (intent AI'dan değil, yedekten geldi.)
                }

                intent.Include ??= new(); intent.Exclude ??= new(); intent.Titles ??= new(); // Üç alandaki intentlerin hiçbir zaman boş olmamasını sağlıyoruz.
                if (!usedFallback) _cache.Set(intentKey, intent, TimeSpan.FromMinutes(30)); // Sadece AI'ın döndürdüğü sonuçları cache'e al, bu sayede cache yedekten gelen kalitesiz sorgu ile dolmasın.
            }

            var include = intent.Include!; // Intent içindeki 'Include' ve 'Exclude'lardan referans alır. '!' = null-forgiving. Bu alanlar null değil uyarı verme.
            var exclude = intent.Exclude!; // Bu 'var' değişkenleri üzerinden yapılan her değişiklik, asıl include ve exclude'ta etkili olur.
            var titles = intent.Titles! // Üretilen ham başlık listelerii, yine '!' null değil anlamına gelir.
                .Where(s => !string.IsNullOrWhiteSpace(s)) // Boş/null/beyaz boşluk olanları at gitsin.
                .Select(s => s.Trim()) // Kenarlardaki boşlukları kırp.
                .Distinct(StringComparer.InvariantCultureIgnoreCase) // Büyük küçük harf ferketmez 'FIFA' ve 'fifa' ikisi aynı şey olur.
                .Take(usedFallback ? 0 : 2) // Fallback kullanıldıysa hiç başlık alma, LLM kullanıldıysa 2 başlık al.
                .ToList(); // 'titles' bir list halini alır.

            // RAWG main query
            var rawgQuery = string.Join(' ', new[] { req.Query, string.Join(' ', include) } // Kullanıcının sorgusu ve AI'ın önerdiği ek anahtar kelimeler birleştirilip,
                .Where(s => !string.IsNullOrWhiteSpace(s)));                                // boşluk ve null'lardan arındırılarak RAWG API'a gidecek nihai arama sorgusu oluşturuluyor.

            List<RawgGame> mainList; // Sonuçların gideceği ana liste, aşağıdan 'try' içinden güvenli bir şekilde doldurulacak.
            string? rawgError = null; // RAWG çağrısı sırasında bir şey ters giderse hata mesajı burada saklanacak.
            try
            {
                mainList = await _rawg.SearchAsync(rawgQuery, limit, ct); // RAWG API'ye asenkron arama çağrısı. rawgQuery temizleyip birleştirdiğimiz sorgu, limit sınırlandırdığımız sorgu sayısı, ct istek iptal edilirse diye.
                // Her şey yolunda giderse 'mainList' burada dolar. 
            }
            catch (Exception ex) // Her türlü beklenmeyen hatayı yakalıyoruz.
            {
                _logger.LogError(ex, "RAWG main query failed for '{RawgQuery}'", rawgQuery); // Hangi sorgunun hangi hatayı verdiğini tespit edip kaydediyoruz.
                rawgError = ex.Message; // Hata sebebini kaydediyoruz.
                mainList = new(); // Sistemin çökmesi yerine, zarifçe boş bir liste gösterilerek 'sonuç yok' moduna geçiyor.
            }

            // RAWG hint queries (bounded, and only if not fallback)
            var hintResults = new List<RawgGame>(); // Titles üzerinden gelecek ek sonuçları burada toplayacağız.
            if (titles.Count > 0) // 'titles' boşsa ipucu aramasına girmiyor, gereksiz API çağrısı yapmıyoruz.
            {
                try
                {
                    var tasks = titles.Select(t => _rawg.SearchAsync(t, 5, ct)).ToList(); // Her başlık(t) için ayrı bir RAWG araması, 'Task' olarak hazırlanıo listeleniyor, pahalı olmasın diye en fazla 5 sonuç.
                    var lists = await Task.WhenAll(tasks); // Tüm aramalar paralel çalışır ve bitince sonuç döner.
                    foreach (var l in lists) hintResults.AddRange(l); // Dönen tüm alt listeler tek bir 'hintResults' listesinde birleşiyor.
                }
                catch (Exception ex) 
                {
                    _logger.LogWarning(ex, "RAWG hint search failed; continuing with main results."); // İpucu aramasına özel hata loglama.
                    rawgError ??= "hint search failed"; // Eğer 'rawgError' boşsa ipucu aramasından gelen hata atanır, doluysa atanmaz.
                }
            }

            // ---- Merge + de-duplicate ----
            var all = mainList.Concat(hintResults) // 'mainList' ile 'hintResults' birleştirir.
                              .GroupBy(g => g.Id) // Aynı oyun birden fazla listeden gelirse, ID üzerinden gruplanır.
                              .Select(g => g.First()) // Her gruptan ilk öğeyi alır.
                              .ToList();
            // collapse near-duplicate names (Dream, Dreams, Dreams.)
            all = all
                .GroupBy(g => g.Name.Trim().ToLowerInvariant().TrimEnd('.', '!', '?')) // Baştaki ve sondaki boşlukları at, büyük küçük farkını yoksay, sondaki noktalama işaretlerini at.
                .Select(g => g.First()) // Grup içinden ilkini alıyoruz, kopyalar tek öğe olmuş oluyor.
                .ToList();

            // ---- Negative filters ----
            var filtered = ApplyNegatives(all, exclude).ToList(); // Intent'ten gelen hariç tut anahtarlarını arayıp eşleşen oyunları eliyor.

            // ---- Ranking ----
            int Rank(RawgGame g) // PUANLAYICI.
            {
                var name = (g?.Name ?? "").ToLowerInvariant(); // 'g' null ise hata vermesin diye boş stringe dönüştürüyor, hepsini küçük harfe çekiyoruz.

                // exact blockbuster matches
                foreach (var t in titles) // LLM'den gelen 'titles' içindekileri puanlıyoruz.
                {
                    var tt = t.ToLowerInvariant(); // Büyük/küçük harfi düzeltiyoruz.
                    if (name == tt) return 0; // Tam eşleşirse 0 dönüyor. (1. öncelik (zirve))
                    if (name.StartsWith(tt)) return 1; // Başlangıçta eşleşme varsa 1 dönüyor. (2. öncelik)
                    if (name.Contains(tt)) return 2; // Sadece barındırıyorsa 2 döndürüyor. (3. öncelik)
                }

                // include keyword presence
                var hay = ((g?.Name ?? "") + " " + string.Join(' ', g?.Genres ?? new List<string>())).ToLowerInvariant(); // Oyun türlerini birleştirip, düzenliyoruz aynı zamanda null olursa diye koruyoruz.
                if (include.Any(k => !string.IsNullOrWhiteSpace(k) && hay.Contains(k.ToLowerInvariant()))) // AI'ın önerdiği anahtar kelimeleri (include listesi) oyun ismi veya türleri arasında geçip geçmediğini kontrol eder.
                    return 3; // Eğer geçiyorsa, oyunun tema ile alakalı olduğu varsayılır ve az miktarda öncelik veriyor.

                // ↓↓↓ add platform penalty here (before fallback)
                var goodPlatform = (g?.Platforms ?? new List<string>()).Any(p => // Oyun alttaki platformların en az birinde yayınlanmış mı?
                    p.Contains("PC", StringComparison.OrdinalIgnoreCase) || // Eğer oyun bunların birinde varsa 'goodPlatform' true olur, yoksa false olur.
                    p.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Nintendo", StringComparison.OrdinalIgnoreCase)
                );
                if (!goodPlatform)
                    return 8; // goodPlatform false olursa (yani mobil ve webteki çöp oyunlarda yayınlanmışsa), en dip puanı ver.

                // penalty for generic single-word names (cheap / vague)
                if (name.Length < 6 && !name.Contains("3") && !name.Contains("ii") && !name.Contains("iv")) // İsmi çok kısa tek kelimeden oluşan oyunları hedef alır, roma rakamlarıyla yazılan IV, V gibi olanları hariç tutar.
                    return 7; // En düşüğün bir üstü puan verir.

                // fallback
                return 6; // Ortalama oyun kategorisi.
            }

            var ranked = filtered.OrderBy(g => Rank(g)) // Negatif filtrelerden geçmiş her oyuna 'Rank' fonksiyonunu uygulayıp sıralıyor.
                                 .ThenBy(g => g.Name ?? "") // Aynı Rank değerine sahip olanları ikinci kez sıralıyor.
                                 .Take(limit); // Daha önce ayarladığımız limit kadar oyun sıralanır.

            var items = ranked.Select(g => new // Response için şekillendiriliyor.
            {
                title = g.Name, // Kullanıcının göreceği başlık.
                why = BuildWhy(g, intent) // Neden bu sonucu gösterdiğimizi açıklayan (title eşleşmesi, platform, include match gibi) bir string üretir.
            });

            return Ok(new // 'Ok', 'Status: 200' (Başarılı çalışma) kodu üretir.
            {
                items, // Az önce şekillendirdiğimiz liste.
                tookMs = (int)sw.ElapsedMilliseconds, // Stopwatch ile ölçtüğümüz süre.
                debug = new // DEBUGGING.
                {
                    rawgQuery, // RAWG'a gönderdiğimiz nihai sorgu. (kullanıcı + include birleşimi.)
                    include, // Intent'ten gelen 'include' keywordleri.
                    exclude, // Intent'ten gelen 'exclude' keywordleri.
                    titles, // Intent'ten gelen başlıklar. 
                    llmFallback = usedFallback, // LLM patlayıp, standart aramaya dönüldü mü?
                    llmError, // LLM'den dönen hata mesajı.
                    rawgError // RAWG aramasında ata oldu mu?
                }
            });
        }

        // FALLBACK DURUMUNDA KULLANILACAK KISIM. YANİ YAPAY ZEKA DESTEKSİZ ARAMA MOTORU, TÜM OLAY YAPAY ZEKADA BİTTİĞİ İÇİN BURASI ASLINDA GEREKSİZ.
        // TEK AMACI EĞER OLUR DA LLM ÇÖKER VEYA HATA VERİR BİR ŞEY OLURSA KULLANICIYA BOŞ DÖNMEMEK İÇİN YAPILMIŞ.
        // AMA OLMASA DA OLUR ÇÜNKÜ KULLANICIYA ÇÖP VERMEKTEN BAŞKA BİR ŞEY DEĞİL, ONURLUCA HATA MESAJI VERSEK DAHA İYİ AMA KOYMUŞUZ YİNE DE ŞİMDİLİK KALSIN BAKALIM.
        private static ParsedIntent FallbackIntent(string q)
        {
            var text = (q ?? "").ToLowerInvariant();
            var inc = new List<string>();
            var exc = new List<string>();

            if (text.Contains("ortaçağ") || text.Contains("orta cag") || text.Contains("medieval")) inc.Add("medieval");
            if (text.Contains("ejder") || text.Contains("ejderha") || text.Contains("dragon")) inc.Add("dragon");
            if (text.Contains("uzay") || text.Contains("space") || text.Contains("sci-fi")) { inc.Add("space"); inc.Add("sci-fi"); }
            if (text.Contains("korku") || text.Contains("horror")) inc.Add("horror");
            if (text.Contains("rpg")) inc.Add("rpg");
            if (text.Contains("açık dünya") || text.Contains("acik dunya") || text.Contains("open world")) inc.Add("open world");
            if (text.Contains("polis") || text.Contains("police")) inc.Add("police");
            if (text.Contains("koval") || text.Contains("chase")) inc.Add("chase");
            if (text.Contains("yarış") || text.Contains("yaris") || text.Contains("race") || text.Contains("racing")) inc.Add("racing");

            if (text.Contains("büyü olmasın") || text.Contains("buyu olmasin") || text.Contains("büyüsüz") || text.Contains("no magic"))
                exc.Add("magic");
            if (text.Contains("sihir") || text.Contains("büyü")) exc.Add("magic");
            if (text.Contains("sihirbaz") || text.Contains("wizard")) exc.Add("wizard");

            return new ParsedIntent
            {
                Include = inc.Distinct().ToList(),
                Exclude = exc.Distinct().ToList(),
                Titles = new List<string>()
            };
        }

        private IEnumerable<RawgGame> ApplyNegatives(IEnumerable<RawgGame> list, List<string> excludes)
        {
            if (list == null) yield break; // Liste yoksa boş geç.

            var excludesNorm = (excludes ?? new()).Where(s => !string.IsNullOrWhiteSpace(s))
                                                  .Select(s => s.ToLowerInvariant())
                                                  .ToList(); // Excludes null ise boş liste yap, ıvır zıvır klasik düzenleri yap.

            if (excludesNorm.Count == 0)
            {
                foreach (var g in list) yield return g;
                yield break; // Negatif bir şey yoksa boş geç.
            }

            var forbidMap = new Dictionary<string, string[]> // Eş anlamlıların aynı hale getirildiği yer.
            {
                // Daha sonra burası JSON'a aktarılıp oradan çağırılacak, genişletilecek gerekirse AI destekli olacak.
                ["magic"] = new[] { "magic", "mage", "wizard", "sorcerer" },
                ["wizard"] = new[] { "wizard", "mage", "sorcerer", "magic" }
            };

            foreach (var g in list)
            {
                var haystack = $"{g?.Name ?? ""} {string.Join(' ', g?.Genres ?? new())} {string.Join(' ', g?.Platforms ?? new())}"
                    .ToLowerInvariant(); // Aranacak metin bir araya getirilip ayar çekiliyor.

                var blocked = excludesNorm.Any(ex =>
                    (forbidMap.TryGetValue(ex, out var words) && words.Any(w => haystack.Contains(w)))
                    || haystack.Contains(ex)); // forbidMap'te o kelime geçiyorsa exclude et (blocked = true)

                if (!blocked) yield return g; // Engellenenleri sik geç.
            }
        }

        private string[] BuildWhy(RawgGame g, ParsedIntent intent) // Kullanıcıya neden bu sonuçları gösterdiğimiz kısım.
        {
            var hints = new List<string>(); // Dönecek olan kısa açıklamaları bir listeye topluyoruz.

            var inc = intent?.Include ?? new(); // Intent'ten gelen 'include' kelimeleri.
            if (inc.Count > 0)
                hints.Add("Matched: " + string.Join(", ", inc.Take(2))); // Varsa 'Matched: ' diyerek ilk iki tanesi yazdırılıyor.

            var genres = g?.Genres ?? new List<string>();
            if (genres.Count > 0)
                hints.Add("Genres: " + string.Join(", ", genres.Take(2))); // Oyun türleri içinde aynı şey.

            var plats = g?.Platforms ?? new List<string>();
            if (plats.Count > 0)
                hints.Add("Platforms: " + string.Join(", ", plats.Take(2))); // Platformlar içinde aynı şey.

            var rel = g?.Released;
            if (!string.IsNullOrWhiteSpace(rel))
                hints.Add("Release: " + rel); // Oyun çıkış tarihi boş değilse onu da ekle.

            return hints.Take(3).ToArray(); // Toplanan ipuçlarından en fazla 3 adet döndür.
        }

        public class SearchRequest // JSON'dan gelen request body bu sınıf ile eşleşiyor. (req)
        {
            public string? Query { get; set; } // Query cümlesi, null olabilir patlamasın diye.
            public int Limit { get; set; } = 10; // Default limit değeri yani en fazla 10 oyun listeleniyor.
        }

        private bool TryConsumeToken(string key, int tokensPer10s, int windowSeconds) // Rate limiter. Kullanıcının kaç saniyede kaç istek hakkı olduğunu ayarlıyoruz.
        {
            var now = DateTimeOffset.UtcNow; // Şu anki UTC zamanı.
            var windowKey = $"{key}:{now.ToUnixTimeSeconds() / windowSeconds}"; // Her 10 saniyede bir sıfırlanır.

            if (!_cache.TryGetValue(windowKey, out int count))
                count = 0; // Cache'ta yoksa, sayacı 0'dan başlat.

            if (count >= tokensPer10s) return false; // Kullanıcı limiti aştıysa, engelle.

            _cache.Set(windowKey, count + 1, new MemoryCacheEntryOptions // Aşmadıysa sayacı +1 yap.
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(windowSeconds) // Kısacası her 10 saniyede 5 istek olayını yapar.
            });
            return true;
        }
    }
}
