using System.ComponentModel.DataAnnotations;

namespace RideFixBro.API.Models.ManualPublishModels
{
    public class ManualUploadRequest
    {
        [Required]
        public IFormFile File { get; set; } = null!;
        [Range(0, 999)]
        // 0 = page 1 se; 7 = pehle 7 physical pages skip, page 8 se extraction.
        public int SkipPages { get; set; }
    }

    public sealed class PublishBikeRequest : ManualUploadRequest
    {
        [Required, StringLength(100)]
        public string Make { get; set; } = string.Empty;
        [Required, StringLength(100)]
        public string Model { get; set; } = string.Empty;
        [Range(1900, 2100)]
        public int Year { get; set; }
        [Required, StringLength(128)]
        public string ManualKey { get; set; } = string.Empty;
    }

}
