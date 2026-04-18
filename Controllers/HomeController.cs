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
            // 10 yerine 100'erli paketler halinde alıyoruz
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

            // 100 siteyi Render'ı şişirmeden, 20'şerli koldan aynı anda tarıyoruz (Hız aşırtma)
            await Parallel.ForEachAsync(pendingSites, new ParallelOptions { MaxDegreeOfParallelism = 20 }, async (item, ct) =>
            {
                var url = $"https://{item.Domain}/ads.txt";

                try
                {
                    var response = await client.GetAsync(url, ct);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(ct);
                        
                        if (content.Contains(item.SelectedPartner ?? "", StringComparison.OrdinalIgnoreCase))
                        {
                            item.HasAdsTxt = true;
                            item.StatusMessage = "Partner Bulundu ✅";
                        }
                        else
                        {
                            item.HasAdsTxt = false;
                            item.StatusMessage = "Partner Eksik ❌";
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
            // 1. Veritabanındaki tüm sonuçları al
            var results = await _context.CheckResults.ToListAsync();

            if (!results.Any())
            {
                return RedirectToAction("Index"); // Veri yoksa ana sayfaya dön
            }

            // 2. Excel dosyasını hafızada oluştur
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Tarama Sonuçları");
                var currentRow = 1;

                // Başlıklar
                worksheet.Cell(currentRow, 1).Value = "Domain";
                worksheet.Cell(currentRow, 2).Value = "Durum";
                worksheet.Cell(currentRow, 3).Value = "Aranan Partner";

                // Verileri Doldur
                foreach (var item in results)
                {
                    currentRow++;
                    worksheet.Cell(currentRow, 1).Value = item.Domain;
                    worksheet.Cell(currentRow, 2).Value = item.StatusMessage;
                    worksheet.Cell(currentRow, 3).Value = item.SelectedPartner;
                }

                // Sütun genişliklerini otomatik ayarla
                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();

                    // 3. BOMBA KISIM: Excel oluşturulduktan sonra veritabanını tamamen temizle!
                    _context.CheckResults.RemoveRange(results);
                    await _context.SaveChangesAsync();

                    // 4. Excel dosyasını kullanıcıya indir
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "AdsFinder_Sonuclar.xlsx");
                }
            }
        }
    }
}