using Microsoft.AspNetCore.Mvc;
using AdsFinder.Models;
using System.Net.Http;
using ClosedXML.Excel;

public class HomeController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HomeController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpPost]
    public async Task<IActionResult> CheckAds(IFormFile file, string selectedPartner)
    {
        var results = new List<CheckResult>();
        if (file == null || string.IsNullOrEmpty(selectedPartner)) return RedirectToAction("Index");

        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                var domain = line?.Trim();
                if (string.IsNullOrWhiteSpace(domain)) continue;

                var url = domain.StartsWith("http") ? domain : $"https://{domain}";
                var adsTxtUrl = $"{url.TrimEnd('/')}/ads.txt";

                try
                {
                    var response = await client.GetAsync(adsTxtUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        
                        // Seçilen partner (örn: admatic.com.tr) içeriğin içinde var mı?
                        if (content.Contains(selectedPartner, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new CheckResult { Domain = domain, HasAdsTxt = true, StatusMessage = "Partner Bulundu" });
                        }
                        else
                        {
                            results.Add(new CheckResult { Domain = domain, HasAdsTxt = false, StatusMessage = "Partner Eksik" });
                        }
                    }
                    else
                    {
                        results.Add(new CheckResult { Domain = domain, HasAdsTxt = false, StatusMessage = "ads.txt bulunamadı" });
                    }
                }
                catch
                {
                    results.Add(new CheckResult { Domain = domain, HasAdsTxt = false, StatusMessage = "Bağlantı Hatası" });
                }
            }
        }

        ViewBag.SelectedPartner = selectedPartner;
        return View("Results", results);
    }

    [HttpPost]
    public async Task<IActionResult> ExportToExcel(string partner, string resultsJson)
    {
        // Sonuçları JSON'dan geri alıyoruz (veya TempData kullanabilirsiniz)
        var results = System.Text.Json.JsonSerializer.Deserialize<List<CheckResult>>(resultsJson);

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("AdsTxt Kontrol");
            var currentRow = 1;

            // Başlıklar
            worksheet.Cell(currentRow, 1).Value = "Domain";
            worksheet.Cell(currentRow, 2).Value = "Durum";
            worksheet.Cell(currentRow, 3).Value = "Hata Mesajı";
            worksheet.Cell(currentRow, 4).Value = "Kontrol Edilen Partner";

            // Stil (Opsiyonel)
            worksheet.Row(1).Style.Font.Bold = true;
            worksheet.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;

            // Veriler
            foreach (var item in results)
            {
                currentRow++;
                worksheet.Cell(currentRow, 1).Value = item.Domain;
                worksheet.Cell(currentRow, 2).Value = item.HasAdsTxt ? "VAR" : "YOK";
                worksheet.Cell(currentRow, 3).Value = item.StatusMessage;
                worksheet.Cell(currentRow, 4).Value = partner;
            }

            worksheet.Columns().AdjustToContents();

            using (var stream = new MemoryStream())
            {
                workbook.SaveAs(stream);
                var content = stream.ToArray();
                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AdsCheck_{partner}.xlsx");
            }
        }
    }
}