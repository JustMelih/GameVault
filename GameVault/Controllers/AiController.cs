using GameVault.Integrations.Rawg;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;


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
                                                                                            // --- Stop kelime filtresi (gereksiz kelimeleri ayıkla) ---
                                                                                            // --- Genel alias genişletme (yamadan ziyade seri eşlemesi) ---
                var qLower = (req.Query ?? "").ToLowerInvariant();
                bool mentionsWar = qLower.Contains("war") || qLower.Contains("military") || qLower.Contains("soldier") || qLower.Contains("battle");

                if (mentionsWar)
                {
                    void AddInc(string t)
                    {
                        if (!intent.Include.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)))
                            intent.Include.Add(t);
                    }
                    // Broaden search surface (generic, not hard-coded results)
                    AddInc("battlefield");
                    AddInc("crysis");
                    AddInc("arma");
                    AddInc("medal of honor");
                    AddInc("company of heroes");
                }

                void AddIfMissing(List<string> list, string token)
                {
                    if (!list.Any(x => x.Equals(token, StringComparison.OrdinalIgnoreCase)))
                        list.Add(token);
                }

                // sports yearly franchises
                if (intent.Include.Any(t => t.Contains("fifa", StringComparison.OrdinalIgnoreCase)))
                {
                    AddIfMissing(intent.Include, "ea sports fc");
                    // titles varsa ekstra arama sinyali için:
                    if (!intent.Titles.Any(t => t.Contains("EA SPORTS FC", StringComparison.OrdinalIgnoreCase)))
                        intent.Titles.Add("EA SPORTS FC 25"); // RAWG'de mevcutsa ipucu olur; yoksa zarar vermez
                }

                // city builder sinyali güçlendir (genel)
                if (intent.Include.Any(t => t.Contains("city", StringComparison.OrdinalIgnoreCase)) &&
                    intent.Include.Any(t => t.Contains("build", StringComparison.OrdinalIgnoreCase)))
                {
                    AddIfMissing(intent.Include, "city builder");
                    // titles varsa “II” varyantlarına ipucu verir
                    if (!intent.Titles.Any(t => t.Contains("Cities: Skylines II", StringComparison.OrdinalIgnoreCase)))
                        intent.Titles.Add("Cities: Skylines II");
                }

                var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "game", "games", "video", "videogame", "the", "a", "an", "play", "player", "we"
                };

                intent.Include = intent.Include
                    .Where(t => !stop.Contains(t) && t.Length >= 3)
                    .Distinct()
                    .Take(6)
                    .ToList();

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
            // Prefer clean, concept-only search — remove "game where we ..." junk
            var cleanUserText = Regex.Replace(req.Query, @"\b(game|where|we|play|in|as|the|a|an)\b", "", RegexOptions.IgnoreCase);
            var rawgQuery = string.Join(' ', new[] { cleanUserText, string.Join(' ', include) })
                .Trim();
            // boşluk ve null'lardan arındırılarak RAWG API'a gidecek nihai arama sorgusu oluşturuluyor.

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



            bool LooksLikeSpam(RawgGame g)
            {
                var name = (g?.Name ?? "").Trim();
                if (name.Length < 3) return true;

                var lower = name.ToLowerInvariant();

                // Aynı kelime 3+ tekrar (COIN COIN COIN gibi)
                var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0 && words.GroupBy(w => w).Max(gr => gr.Count()) >= 3) return true;

                // Aşırı noktalama/emoji vs. (oyun adı değil, mobil clicker kokusu)
                var letters = words.Sum(w => w.Count(char.IsLetter));
                var nonLetters = lower.Count(ch => !char.IsLetter(ch) && !char.IsWhiteSpace(ch));
                if (letters > 0 && nonLetters > letters) return true;

                return false;
            }

            all = all.Where(g => !LooksLikeSpam(g)).ToList();

            // ---- Ranking ----
            int Rank(RawgGame g)
            {
                if (g == null) return int.MaxValue;

                var name = (g.Name ?? "").Trim();
                var nameL = name.ToLowerInvariant();

                // 1) Titles eşleşme (ikonik isimler)
                double titleHit = 0;
                foreach (var t in titles)
                {
                    var tt = t.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(tt)) continue;

                    if (nameL == tt) { titleHit = 1.00; break; }
                    else if (nameL.StartsWith(tt)) { titleHit = Math.Max(titleHit, 0.85); }
                    else if (nameL.Contains(tt)) { titleHit = Math.Max(titleHit, 0.70); }
                }

                // 2) Include örtüşmesi (genel)
                var hay = (name + " " + string.Join(' ', g.Genres ?? new()) + " " + string.Join(' ', g.Platforms ?? new()))
                            .ToLowerInvariant();

                var inc = include.Where(s => s.Length >= 3).ToList();
                int incHit = inc.Count(k => hay.Contains(k));
                double incScore = inc.Count == 0 ? 0 : (double)incHit / inc.Count; // 0..1

                // 3) Query bigram örtüşmesi (genel)
                double phraseScore = 0;
                {
                    var qTokens = (req.Query ?? "").ToLowerInvariant()
                        .Split(new[] { ' ', ',', '.', ':', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => w.Length >= 3 && w != "game" && w != "play" && w != "where")
                        .ToArray();

                    for (int i = 0; i < qTokens.Length - 1; i++)
                    {
                        var bigram = qTokens[i] + " " + qTokens[i + 1];
                        if (nameL.Contains(bigram)) phraseScore += 0.5;
                    }
                    phraseScore = Math.Min(1.0, phraseScore);
                }

                // 4) Platform sinyali (genel)
                // 4) Platform sinyali (güncelle)
                var plats = g.Platforms ?? new();
                bool hasConsolePc = plats.Any(p =>
                    p.Contains("PC", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Nintendo", StringComparison.OrdinalIgnoreCase));

                bool isBrowser = plats.Any(p =>
                    p.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Browser", StringComparison.OrdinalIgnoreCase));

                bool isMobileOnly = !hasConsolePc && plats.Any(p =>
                    p.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("iOS", StringComparison.OrdinalIgnoreCase));

                double platScore =
                    isBrowser ? -0.9 :
                    isMobileOnly ? -0.5 :
                    hasConsolePc ? 0.2 : -0.2;

                // ---- Franchise key çıkar (numerik/roman/edition at) ----
                string FranchiseKey(string name)
                {
                    var n = (name ?? "").ToLowerInvariant();
                    n = System.Text.RegularExpressions.Regex.Replace(n, @"\b(remastered|definitive|ultimate|deluxe|goty|edition|unlimited|complete)\b", "");
                    n = System.Text.RegularExpressions.Regex.Replace(n, @"\b(ii|iii|iv|v|vi|vii|viii|ix|x)\b", ""); // roman
                    n = System.Text.RegularExpressions.Regex.Replace(n, @"\b\d{2,4}\b", ""); // yıllar/sayılar
                    n = n.Replace(":", "").Replace("-", " ");
                    n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
                    return n;
                }

                // Tüm adaylardan franchise → en yeni yıl tablosu
                int YearOf(string? s) => DateTime.TryParse(s, out var d) ? d.Year : 0;

                var latestYearByFranchise = all
                    .GroupBy(g => FranchiseKey(g.Name))
                    .ToDictionary(gr => gr.Key, gr => gr.Max(x => YearOf(x.Released)));

                // Rank() içinde (en altta score hesaplanırken) şu ek alanı kullan:
                double franchiseBoost = 0;
                {
                    var key = FranchiseKey(g.Name!);
                    if (latestYearByFranchise.TryGetValue(key, out var latest))
                    {
                        var y = YearOf(g.Released);
                        if (latest > 0 && y > 0)
                        {
                            var diff = latest - y; // 0 = en yeni
                            if (diff == 0) franchiseBoost = 0.35;          // en yeni olanlar öne
                            else if (diff == 1) franchiseBoost = 0.15;     // bir önceki
                            else if (diff == 2) franchiseBoost = 0.05;     // iki önceki
                            else franchiseBoost = 0;                        // daha eski: boost yok
                        }
                    }
                }

                // 5) İsim aşırı kısa/jenerik cezası (genel)
                double genericPenalty = 0;
                if (name.Length <= 3) genericPenalty -= 0.6;
                if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1 && name.Length < 6)
                    genericPenalty -= 0.3;

                // 6) Recency (varsa)
                // 6) Recency (güncelle — yumuşak eğri)
                double recency = 0;
                if (DateTime.TryParse(g.Released, out var dt))
                {
                    var ageYears = Math.Max(0, (DateTime.UtcNow - dt).TotalDays / 365.0);
                    // 0 yaş ≈ +0.35, 3 yaş ≈ +0.20, 6 yaş ≈ +0.10, 10+ ≈ +0.03
                    recency = 0.35 * Math.Exp(-ageYears / 6.0);
                }


                // 7) Popülerlik/kalite (RAWG alanları — RawgClient’te mapledik)
                double popScore = 0;
                if (g.RatingsCount > 0)
                    popScore += Math.Min(0.4, Math.Log10(g.RatingsCount + 1) * 0.2); // 0..~0.4
                if (g.Metacritic is int mc)
                    popScore += Math.Clamp((mc - 60) / 100.0, 0, 0.3); // 60 üstüne hafif boost

                // 8) Konu-uyumsuzluk cezası (genel)
                double mismatch = TopicMismatchPenalty(g, include);

                // ---- toplam skor (yüksek iyi) ----
                double score =
                      1.30 * titleHit
                    + 1.05 * incScore
                    + 0.70 * phraseScore
                    + 0.30 * recency
                    + 0.25 * popScore
                    + franchiseBoost      // <— eklendi
                    + platScore
                    + genericPenalty
                    + mismatch;

                // Rank'ta küçük daha iyi; tersle
                return (int)(1000 - score * 100.0);
            }


            bool MatchesTitle(RawgGame g, IEnumerable<string> ts)
            {
                var name = (g?.Name ?? "").Trim().ToLowerInvariant();
                foreach (var t in ts)
                {
                    var tt = t.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(tt)) continue;
                    if (name == tt || name.StartsWith(tt) || name.Contains(tt))
                        return true;
                }
                return false;
            }

            double TopicMismatchPenalty(RawgGame g, List<string> include)
            {
                var hay = ((g?.Name ?? "") + " " + string.Join(' ', g?.Genres ?? new()))
                          .ToLowerInvariant();

                var niche = new[] { "dating sim", "idle", "clicker", "gacha" };
                bool nichePresent = niche.Any(n => hay.Contains(n));

                bool userAskedNiche = include.Any(inc =>
                    niche.Any(n => n.Contains(inc, StringComparison.OrdinalIgnoreCase)));

                if (nichePresent && !userAskedNiche) return -0.4;  // ufak ceza
                return 0;
            }

            // --- helper to normalize franchise keys (local function is fine here)
            string FranchiseKey(string name)
            {
                var n = (name ?? "").ToLowerInvariant().Trim();

                // Normalize separators
                n = n.Replace('–', '-').Replace('—', '-');

                // Hard root mapping for mega-franchises (prevents CoD sublines bypassing cap)
                if (n.StartsWith("call of duty")) return "call of duty";
                if (n.StartsWith("battlefield")) return "battlefield";
                if (n.StartsWith("medal of honor")) return "medal of honor";
                if (n.StartsWith("crysis")) return "crysis";
                if (n.StartsWith("arma")) return "arma";
                if (n.StartsWith("company of heroes")) return "company of heroes";
                if (n.StartsWith("total war")) return "total war";

                // Generic fallback: strip subtitles after ":" or " - "
                var cut = n.IndexOf(':');
                if (cut > 0) n = n[..cut];
                cut = n.IndexOf(" - ");
                if (cut > 0) n = n[..cut];

                // Remove common suffix noise
                n = System.Text.RegularExpressions.Regex.Replace(n,
                    @"\b(remastered|definitive|ultimate|deluxe|goty|edition|unlimited|complete)\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Drop roman numerals & plain years/numbers
                n = System.Text.RegularExpressions.Regex.Replace(n, @"\b(ii|iii|iv|v|vi|vii|viii|ix|x)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                n = System.Text.RegularExpressions.Regex.Replace(n, @"\b\d{2,4}\b", "");

                // Normalize whitespace
                n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
                return n;
            }

            // Keep your buckets
            var bucketA = filtered.Where(g => MatchesTitle(g, titles)).ToList(); // titles-aligned
            var bucketB = filtered.Where(g => !MatchesTitle(g, titles)).ToList();

            // Order inside each bucket with your existing Rank()
            var orderedA = bucketA.OrderBy(g => Rank(g)).ThenBy(g => g.Name ?? "").ToList();
            var orderedB = bucketB.OrderBy(g => Rank(g)).ThenBy(g => g.Name ?? "").ToList();

            // Global per-franchise cap
            const int franchiseCap = 1; // try 1 first; raise to 2 if too strict

            var pickA = orderedA.GetEnumerator();
            var pickB = orderedB.GetEnumerator();

            bool nextA = true; // alternate A/B
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rankedList = new List<RawgGame>(limit);

            // Helper to try taking next from an enumerator with cap
            bool TryTake(IEnumerator<RawgGame> it)
            {
                if (!it.MoveNext()) return false;
                var g = it.Current;
                var key = FranchiseKey(g.Name);
                seen.TryGetValue(key, out var cnt);
                if (cnt >= franchiseCap) return false; // skip; caller will try again on next loop

                rankedList.Add(g);
                seen[key] = cnt + 1;
                return true;
            }

            // Round-robin until filled or both exhaust
            while (rankedList.Count < limit && (orderedA.Count > 0 || orderedB.Count > 0))
            {
                var took = false;

                if (nextA && orderedA.Count > 0)
                    took = TryTake(pickA);
                else if (!nextA && orderedB.Count > 0)
                    took = TryTake(pickB);

                // If chosen bucket couldn't provide (cap/exhaustion), try the other
                if (!took)
                {
                    if (nextA && orderedB.Count > 0) took = TryTake(pickB);
                    else if (!nextA && orderedA.Count > 0) took = TryTake(pickA);
                }

                // If neither provided, break
                if (!took) break;

                // Flip for next round
                nextA = !nextA;

                // Early exit
                if (rankedList.Count >= limit) break;
            }

            // If still short (due to caps), append remaining in overall order ignoring caps
            if (rankedList.Count < limit)
            {
                var chosenIds = new HashSet<int>(rankedList.Select(x => x.Id));
                foreach (var g in orderedA.Concat(orderedB))
                {
                    if (chosenIds.Contains(g.Id)) continue;
                    rankedList.Add(g);
                    if (rankedList.Count >= limit) break;
                }
            }

            var ranked = rankedList.Take(limit).ToList();

            // Your items projection stays the same
            var items = ranked.Select(g => new
            {
                title = g.Name,
                why = BuildWhy(g, intent)
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
