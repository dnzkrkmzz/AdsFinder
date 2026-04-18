using System.ComponentModel.DataAnnotations;

namespace AdsFinder.Models
{
    public class CheckResult
    {
        [Key]
        public int Id { get; set; }
        public string Domain { get; set; }
        public string SelectedPartner { get; set; } // Hangi partner aranıyor?
        
        // İşlem durumu
        public bool IsProcessed { get; set; } = false; // Başlangıçta hepsi "Bekliyor"
        
        // Sonuçlar
        public bool HasAdsTxt { get; set; }
        public string StatusMessage { get; set; }
    }
}