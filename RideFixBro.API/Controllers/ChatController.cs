using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Models;
using RideFixBro.API.Services;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class ChatController : ControllerBase
	{
		private readonly AiManagerService _aiManager;

		private readonly VectorDbService _vectorDb;

		public ChatController(AiManagerService aiManager, VectorDbService vectorDb)
		{
			_aiManager = aiManager;
			_vectorDb = vectorDb;
		}

		// Manual vector form m Upload ke liye
		[HttpPost("upload-dummy-manual")]
		public async Task<IActionResult> UploadDummyManual()
		{
			// Apni X440 ka ek dummy fix banaya
			var chainFix = "Harley Davidson X440 Chain Slack: The ideal chain slack for Harley Davidson X440 is 25mm to 30mm. You should check the chain tension every 500 km. Lube it properly. If the chain makes a grinding noise, check the front sprocket.";

			var result = await _vectorDb.UploadManualChunkAsync("chain_slack_01", chainFix);

			return Ok(new { Message = result });
		}

		[HttpPost("upload-pdf-manual")]
		public async Task<IActionResult> UploadPdfManual()
		{
			// apni X440 PDF ka exact local path daal de.
			string pdfPath = @"C:\Users\vrgpv\Downloads\hd_x440_apr_2025.pdf";

			var result = await _vectorDb.ProcessAndUploadPdfAsync(pdfPath);

			return Ok(new { Message = result });
		}

		[HttpPost("ask")]
		public async Task<IActionResult> AskBro([FromBody] ChatRequest request)
		{
			// Agar user ne khali message bhej diya
			if (string.IsNullOrWhiteSpace(request.Message))
			{
				return BadRequest(new { Error = "Abe bhai, blank message kyu bhej raha hai? Kuch likh toh de!" });
			}

			try
			{
				// AiManagerService ko message pass kiya
				var response = await _aiManager.AskMechanicBro(request.SessionId, request.Message, request.ImageData);
				return Ok(new { Reply = response });
			}
			catch (Exception ex)
			{
				// Agar kuch phata toh seedha error milega
				return StatusCode(500, new { Error = $"Bhai, server mein aag lag gayi: {ex.Message}" });
			}
		}
	}
}