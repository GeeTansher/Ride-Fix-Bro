using System.ComponentModel.DataAnnotations;

namespace RideFixBro.API.Models
{
    public sealed class ManualUploadRequest
    {
        [Required]
        public IFormFile File { get; set; } = null!;
        [Range(0, 999)]
        // 0 = page 1 se; 7 = pehle 8 physical PDF pages skip. Printed page numbers nahi.
        public int SkipPages { get; set; }
    }
}
