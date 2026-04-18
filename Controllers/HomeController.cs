using Microsoft.AspNetCore.Mvc;
using AdsFinder.Models;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;

namespace AdsFinder.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppDbContext _context;

        public HomeController(ILogger<HomeController> logger, IHttpClientFactory httpClientFactory, AppDbContext context)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _context = context;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> StartScan(IFormFile file, string selectedPartner)
        {
            if (file == null || string.IsNullOrEmpty(selectedPartner))
            {
                return RedirectToAction("Index");
            }

            // 1. Yeni tarama için eski verileri temizle
            var allOldRecords = _context.CheckResults.ToList();
            _context.CheckResults.RemoveRange(allOldRecords);
            await _context.SaveChangesAsync();

            // 2. Dosyayı oku ve veritabanına "Bekliyor" olarak kaydet
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    var domain = line?.Trim();

                    if (!string.IsNullOrWhiteSpace(domain))
                    {
                        // Alan adının başındaki http/https kısmını temizleyip saf domaini alalım (İsteğe bağlı)
                        domain = domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');

                        _context.CheckResults.Add(new CheckResult
                        {
                            Domain = domain,
                            SelectedPartner = selectedPartner,
                            IsProcessed = false,
                            StatusMessage = "Bekliyor..."
                        });
                    }
                }
            }
            await _context.SaveChangesAsync();

            // 3. Toplam sayıyı View'a gönder ve Results sayfasını aç
            ViewBag.TotalSites = await _context.CheckResults.CountAsync();
            return View("Results");
        }

        [HttpPost]
        public async Task<IActionResult> ProcessBatch()
        {
            var pendingSites = await _context.CheckResults
                .Where(x => !x.IsProcessed)
                .Take(100)
                .ToListAsync();

            if (!pendingSites.Any())
            {
                return Json(new { finished = true });
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8); 

            // Uygulamaya "Kılık Değiştirme" (Google Chrome Gibi Davranma) Özelliği Ekliyoruz:
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7"); 

            await Parallel.ForEachAsync(pendingSites, new ParallelOptions { MaxDegreeOfParallelism = 20 }, async (item, ct) =>
            {
                var url = $"https://{item.Domain}/ads.txt";

                try
                {
                    var response = await client.GetAsync(url, ct);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(ct);
                        
                        // YENİ MANTIK: Virgülle ayrılmış partnerleri listeye çevir ve tek tek kontrol et
                        var partnersToSearch = item.SelectedPartner?
                            .Split(',')
                            .Select(p => p.Trim())
                            .Where(p => !string.IsNullOrEmpty(p))
                            .ToList() ?? new List<string>();

                        var foundList = new List<string>();
                        var missingList = new List<string>();

                        foreach (var partner in partnersToSearch)
                        {
                            if (content.Contains(partner, StringComparison.OrdinalIgnoreCase))
                            {
                                foundList.Add(partner);
                            }
                            else
                            {
                                missingList.Add(partner);
                            }
                        }

                        // Sonuçları Kararlaştır
                        if (foundList.Any() && !missingList.Any())
                        {
                            item.HasAdsTxt = true;
                            item.StatusMessage = $"Hepsi Bulundu ✅ ({string.Join(", ", foundList)})";
                        }
                        else if (foundList.Any() && missingList.Any())
                        {
                            item.HasAdsTxt = true; // En az 1 tane bulunduğu için yeşil yapalım
                            item.StatusMessage = $"Kısmen ✅ ({string.Join(", ", foundList)}) | Eksik ❌ ({string.Join(", ", missingList)})";
                        }
                        else
                        {
                            item.HasAdsTxt = false;
                            item.StatusMessage = $"Hiçbiri Yok ❌";
                        }
                    }
                    else
                    {
                        item.HasAdsTxt = false;
                        item.StatusMessage = $"Hata: {response.StatusCode}";
                    }
                }
                catch
                {
                    item.HasAdsTxt = false;
                    item.StatusMessage = "Bağlantı Kurulamadı";
                }

                item.IsProcessed = true;
            });

            await _context.SaveChangesAsync();

            return Json(new { finished = false, results = pendingSites });
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
        [HttpGet]
        public async Task<IActionResult> ExportToExcel()
        {
            var results = await _context.CheckResults.ToListAsync();

            if (!results.Any())
            {
                return RedirectToAction("Index"); // Veri yoksa ana sayfaya dön
            }

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Tarama Sonuçları");
                
                // 1. Aranılan partnerleri bul (Kullanıcının girdiği virgüllü listeyi ayırıyoruz)
                var firstRecord = results.First();
                var partners = firstRecord.SelectedPartner?
                    .Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList() ?? new List<string>();

                // 2. BAŞLIKLARI DİNAMİK OLUŞTUR
                worksheet.Cell(1, 1).Value = "Domain";
                
                int colIndex = 2; // 2. sütundan itibaren partnerleri diz
                foreach (var partner in partners)
                {
                    worksheet.Cell(1, colIndex).Value = partner;
                    colIndex++;
                }

                // 3. VERİLERİ DOLDUR
                var currentRow = 1;
                foreach (var item in results)
                {
                    currentRow++;
                    worksheet.Cell(currentRow, 1).Value = item.Domain;

                    int pColIndex = 2;
                    foreach (var partner in partners)
                    {
                        string cellValue = "";

                        // Durum mesajına göre hücreye yazılacak değeri belirliyoruz
                        if (item.StatusMessage == null)
                        {
                            cellValue = "Bilinmiyor";
                        }
                        else if (item.StatusMessage.StartsWith("Hata") || item.StatusMessage.StartsWith("Bağlantı"))
                        {
                            cellValue = item.StatusMessage; // Hata varsa direkt yaz (Örn: Hata: Forbidden)
                        }
                        else if (item.StatusMessage.Contains("Hepsi Bulundu"))
                        {
                            cellValue = "Bulundu ✅";
                        }
                        else if (item.StatusMessage.Contains("Hiçbiri Yok"))
                        {
                            cellValue = "Eksik ❌";
                        }
                        else if (item.StatusMessage.Contains("Kısmen"))
                        {
                            // Metni ikiye böl: "Kısmen (bulunanlar) | Eksik (bulunamayanlar)"
                            var parts = item.StatusMessage.Split('|');
                            
                            // Eğer partner ilk kısımda (bulunanlar arasında) geçiyorsa
                            if (parts.Length > 0 && parts[0].Contains(partner, StringComparison.OrdinalIgnoreCase))
                            {
                                cellValue = "Bulundu ✅";
                            }
                            else
                            {
                                cellValue = "Eksik ❌";
                            }
                        }
                        else
                        {
                            cellValue = item.StatusMessage;
                        }

                        worksheet.Cell(currentRow, pColIndex).Value = cellValue;
                        pColIndex++;
                    }
                }

                // 4. GÖRSEL DÜZENLEMELER (Tabloyu Şıklaştıralım)
                worksheet.Columns().AdjustToContents(); // Sütun genişliklerini otomatik ayarla
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true; // Başlıkları kalın yap
                headerRow.Style.Fill.BackgroundColor = XLColor.LightGray; // Başlıklara gri arka plan ver
                worksheet.SheetView.FreezeRows(1); // İlk satırı (başlıkları) dondur

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();

                    // 5. Excel oluşturulduktan sonra veritabanını tamamen temizle
                    _context.CheckResults.RemoveRange(results);
                    await _context.SaveChangesAsync();

                    // 6. Dosyayı İndir
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "AdsFinder_Detayli_Sonuclar.xlsx");
                }
            }
        }
    }
}