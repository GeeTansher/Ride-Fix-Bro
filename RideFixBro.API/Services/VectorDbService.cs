using AutoGen.Core;
using OpenAI.Embeddings;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RideFixBro.API.Agents;
using System.ComponentModel;
using System.Text;
using UglyToad.PdfPig;

namespace RideFixBro.API.Services
{
	public partial class VectorDbService
	{
		private readonly QdrantClient _qdrantClient;
		private readonly EmbeddingClient _embeddingClient;
		private readonly string _collectionName = "X440_Manual"; // DB ka naam

		internal VectorDbService(QdrantClient qdrantClient, EmbeddingClient embeddingClient)
		{
			_qdrantClient = qdrantClient;
			_embeddingClient = embeddingClient;
		}

		public VectorDbService(IConfiguration config)
		{
			var qdrantEndpoint = config["Qdrant_Vector_DB:Cluster_Endpoint"];
			var qdrantApiKey = config["Qdrant_Vector_DB:API_Key"];
			var geminiApiKey = config["API_Keys:Gemini_Api_key"];

			if (string.IsNullOrEmpty(qdrantEndpoint) || string.IsNullOrEmpty(qdrantApiKey))
			{
				throw new Exception("Bhai, Qdrant ki keys check kar appsettings mein, missing hain!");
			}
			if (string.IsNullOrEmpty(geminiApiKey))
			{
				throw new Exception("Bhai, Gemini ki keys check kar appsettings mein, missing hain!");
			}

			// Qdrant Cloud se connection establish kar raha hai
			_qdrantClient = new QdrantClient(host: qdrantEndpoint, https: true, apiKey: qdrantApiKey);

			// Gemini Embedding Client Setup (OpenAI proxy)
			var openAIClient = OpenAIClientBuilder.Create(geminiApiKey);

			// Gemini ka text-embedding model (Dhyan rakhna, chat model alag hota hai, embedding model alag)
			_embeddingClient = openAIClient.GetEmbeddingClient("gemini-embedding-2-preview");
		}

		// Yeh function manual ke text ko DB mein daalega
		public async Task<string> UploadManualChunkAsync(string sectionId, string text)
		{
			try
			{
				// 1. Pehle check karo DB (Collection) exist karti hai ya nahi?
				var collections = await _qdrantClient.ListCollectionsAsync();
				if (!collections.Contains(_collectionName))
				{
					// gemini-embedding-2-preview ka dimension 3072 hota hai
					await _qdrantClient.CreateCollectionAsync(_collectionName, new VectorParams { Size = 3072, Distance = Distance.Cosine });
				}

				// 2. Gemini se Text ka Vector (Embedding) nikaalo
				var embeddingResponse = await _embeddingClient.GenerateEmbeddingAsync(text);
				var vectorArray = embeddingResponse.Value.ToFloats().ToArray();

				// 3. Qdrant mein Data aur Text dono save karo (Payload matlab Metadata)
				var point = new PointStruct
				{
					Id = (ulong)sectionId.GetHashCode(), // Unique ID banayi
					Vectors = vectorArray,
					Payload = { ["text"] = text, ["bike"] = "Harley_X440" } // Yeh text wapas aayega jab hum search marenge
				};

				await _qdrantClient.UpsertAsync(_collectionName, [point]);

				return $"Bhai, '{sectionId}' wala chunk successfully Qdrant mein upload ho gaya!";
			}
			catch (Exception ex)
			{
				throw new Exception($"Panga ho gaya upload mein: {ex.Message}");
			}
		}

		// PDF Process karne ka Main Function
		public async Task<string> ProcessAndUploadPdfAsync(string filePath)
		{
			if (!File.Exists(filePath)) return "Bhai, file hi nahi mili is path pe!";

			StringBuilder fullText = new StringBuilder();

			try
			{
				// 1. PDF Read kar rahe hain
				using (PdfDocument document = PdfDocument.Open(filePath))
				{
					// Shuru ke 7 pages index maan ke skip kar rahe hain (Apne hisaab se change kar lena)
					for (int i = 8; i <= document.NumberOfPages; i++)
					{
						var page = document.GetPage(i);
						fullText.Append(page.Text).Append(" ");
					}
				}

				var allText = fullText.ToString();

				// 2. Text ko 300 words ke chunks (tukdon) mein tod rahe hain
				var chunks = GetWordChunks(allText, 300);
				int successCount = 0;

				// 3. Loop chala ke Qdrant mein upload maar rahe hain
				for (int i = 0; i < chunks.Count; i++)
				{
					string chunkId = $"manual_page_{i + 1}"; // Unique ID banayi

					// Apna purana upload wala function call kiya
					await UploadManualChunkAsync(chunkId, chunks[i]);
					successCount++;

					// PRO TIP: Google se ban hone se bachne ke liye 1.5 seconds ka delay
					await Task.Delay(1500);
				}

				return $"Aag laga di bhai! PDF se {successCount} chunks DB mein daal diye.";
			}
			catch (Exception ex)
			{
				throw new Exception ($"Bhai PDF parse karne mein aag lag gayi: {ex.Message}");
			}
		}

		// User ki query ko Qdrant mein search karne ke liye
		[Function]
		[Description("Motorcycle ki manual, technical repair steps, torque specs, ya error codes search karne ke liye is tool ka use karein. (e.g. 'X440 spark plug replacement')")]
		public async Task<string> SearchManualAsync(
			[Description("Search query jo DB mein dhoondhni hai")] string userQuery)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(userQuery);
			var embeddingResponse = await _embeddingClient.GenerateEmbeddingAsync(userQuery);
			var queryVector = embeddingResponse.Value.ToFloats().ToArray();
			var searchResults = await _qdrantClient.QueryAsync(
				collectionName: _collectionName,
				query: queryVector,
				limit: 3
			);

			var contextText = new StringBuilder();
			foreach (var result in searchResults)
			{
				if (result.Payload.TryGetValue("text", out var textValue) &&
					!string.IsNullOrWhiteSpace(textValue.StringValue))
				{
					contextText.AppendLine(textValue.StringValue);
					contextText.AppendLine("---");
				}
			}

			return contextText.Length > 0
				? contextText.ToString()
				: "No relevant manual passages were found. Do not present general advice as a verified manual specification.";
		}

		// Helper Function: Text ko words ke hisaab se todne ke liye
		private List<string> GetWordChunks(string text, int wordsPerChunk)
		{
			var words = text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			var chunks = new List<string>();

			for (int i = 0; i < words.Length; i += wordsPerChunk)
			{
				// 300 words ka ek block bana ke list mein daal rahe hain
				chunks.Add(string.Join(" ", words.Skip(i).Take(wordsPerChunk)));
			}
			return chunks;
		}
	}
}