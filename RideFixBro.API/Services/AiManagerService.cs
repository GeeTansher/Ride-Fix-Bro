using AutoGen.Core;
using Microsoft.SemanticKernel.ChatCompletion;
using RideFixBro.API.Agents;
using RideFixBro.API.DataStore.Interfaces;

namespace RideFixBro.API.Services
{
	public class AiManagerService
	{
		private readonly string _geminiApiKey;
		private readonly string _tavilyApiKey;
		private readonly VectorDbService _vectorDb; // Vector DB ka reference

		private readonly IChatHistoryStore chatHistoryStore;

		// Constructor mein VectorDbService inject ki
		public AiManagerService(IConfiguration config, VectorDbService vectorDb, IChatHistoryStore chatStore)
		{
			_geminiApiKey = config["API_Keys:Gemini_Api_key"] ?? throw new ArgumentNullException("Bhai, appsettings mein Gemini API Key nahi mil rahi!");
			_tavilyApiKey = config["API_Keys:Tavily_Api_key"] ?? throw new ArgumentNullException("Bhai, appsettings mein Tavily API Key nahi mil rahi!");
			_vectorDb = vectorDb;
			chatHistoryStore = chatStore;
		}

		public async Task<string> AskMechanicBro(string sessionId, string userMessage, string? base64Image = null)
		{
			try
			{
				// Agent ko bula aur prompt de de
				var broAgent = MechanicBroAgent.Create(_geminiApiKey, _tavilyApiKey, _vectorDb);
				IMessage messageToSend;

				// Agar photo aayi hai, toh MultiModal Message banayenge
				if (!string.IsNullOrEmpty(base64Image))
				{
					// Format fix kar rahe hain incase app se galat aaye
					string cleanBase64 = base64Image.Replace("data:image/jpeg;base64,", "").Replace("data:image/png;base64,", "");
					string dataUri = $"data:image/jpeg;base64,{cleanBase64}";

					// Image aur Text dono ek saath bhej rahe hain
					messageToSend = new MultiModalMessage(Role.User,
					[
						new TextMessage(Role.User, userMessage),
						new ImageMessage(Role.User, dataUri)
					]);
				}
				else
				{
					// Warna normal text message
					messageToSend = new TextMessage(Role.User, userMessage);
				}

				// 2. Memory se history nikali
				var chatHistory = chatHistoryStore.GetHistory(sessionId);
				chatHistory.Add(messageToSend);

				// 3. Seedha SendAsync maro! Middleware khud sabhi tools (Tavily & Manual) 
				// ko execute karke final Hinglish answer laake dega. No manual loops needed!
				var reply = await broAgent.GenerateReplyAsync(chatHistory);

				chatHistory.Add(reply);
				//chatHistoryStore.SaveHistory(sessionId, chatHistory);
				// History save karne se pehle ye check laga de bhai:
				var cleanHistory = chatHistory.Where(m =>
				{
					// Agar ToolCallMessage hai aur uska result null hai, toh usko uda do
					if (m is ToolCallResultMessage toolMsg)
					{
						// Check if any tool result is null or empty
						return true;
					}
					return true;
				}).ToList();

				chatHistoryStore.SaveHistory(sessionId, cleanHistory);

				return reply.GetContent();
				//// 1. Apni "Tool Registry" bana le (Yahan tu 100 tools bhi add kar sakta hai bina if-else ke)
				//var toolRegistry = new Dictionary<string, Func<string, Task<string>>>
				//{
				//	{
				//		"SearchInternetAsync", async (jsonArgs) =>
				//		{
				//			var args = System.Text.Json.JsonDocument.Parse(jsonArgs);
				//			var query = args.RootElement.GetProperty("query").GetString() ?? "";
				//			var tavilyService = new TavilySearchService(_tavilyApiKey);
				//			return await tavilyService.SearchInternetAsync(query);
				//		}
				//	},
				//	{
				//		"SearchManualAsync", async (jsonArgs) =>
				//		{
				//			var args = System.Text.Json.JsonDocument.Parse(jsonArgs);
				//			var query = args.RootElement.GetProperty("userQuery").GetString();
				//			// LLM ne jo smart query banayi hai, usse DB search maar!
				//			return await _vectorDb.SearchManualAsync(query);
				//		}
				//	}
				//};

				//// 2. Chat history maintain kar
				//// Database (Memory) se pichli baatein nikal!
				//var chatHistory = chatHistoryStore.GetHistory(sessionId);

				//// user ka message add kar
				//chatHistory.Add(messageToSend);

				//var reply = await broAgent.GenerateReplyAsync(chatHistory);
				//chatHistory.Add(reply);

				//// 3. Agar Gemini ne koi Tool maanga hai (kitne bhi tools ho sakte hain)
				//if (reply is ToolCallMessage toolCallMsg)
				//{
				//	// Hum history mein purana message replace karke naya daalenge jisme Content = "" (khali string) ho.
				//	var safeToolCallMsg = new ToolCallMessage(toolCallMsg.ToolCalls, toolCallMsg.From) { Content = "" };
				//	chatHistory[chatHistory.Count - 1] = safeToolCallMsg; // Last message replace kar diya

				//	var toolResultsList = new List<ToolCall>();       // Naya list banayenge saare results ikkathe karne ke liye

				//	// AutoGen ek saath multiple tools bhi maang sakta hai, isliye hum loop lagayenge
				//	foreach (var toolCall in toolCallMsg.ToolCalls)
				//	{
				//		string toolResultText = "";

				//		// Check kar ki Gemini ne jo tool maanga, wo humari Registry mein hai ya nahi
				//		if (toolRegistry.TryGetValue(toolCall.FunctionName, out var executeToolLogic))
				//		{
				//			// Tool execute kar bina kisi if-else ke!
				//			toolResultText = await executeToolLogic(toolCall.FunctionArguments);
				//		}
				//		else
				//		{
				//			toolResultText = "Error: Bhai ye tool toh mere paas hai hi nahi!";
				//		}

				//		// AAG KA GOLA: Purane toolCall ko mutate nahi karna hai!
				//		// Ek naya ToolCall object bana sirf result ke liye taaki purana message corrupt na ho.

				//		//// Gemini strict hai, usko plain text nahi, JSON chahiye tool result mein!
				//		//var safeJsonResult = System.Text.Json.JsonSerializer.Serialize(new { result = toolResultText });

				//		var completedToolCall = new ToolCall(toolCall.FunctionName, toolCall.FunctionArguments)
				//		{
				//			ToolCallId = toolCall.ToolCallId, // ID match karna bohot zaroori hai
				//			Result = toolResultText
				//		};

				//		toolResultsList.Add(completedToolCall);
				//	}

				//	// Saare tools ke results ko EK SAATH ek single message mein pack kar (Loop ke bahar)
				//	var resultMsg = new ToolCallResultMessage(toolResultsList);
				//	chatHistory.Add(resultMsg);

				//	// 4. Sab tools chalne ke baad LLM ko bol "Bhai ab tu padh aur final answer de"
				//	reply = await broAgent.GenerateReplyAsync(chatHistory);
				//	chatHistory.Add(reply);
				//}
				//chatHistoryStore.SaveHistory(sessionId, chatHistory);
				//return reply.GetContent();
			}
			catch(Exception ex)
			{
				Console.WriteLine("Error in Ai Manager Service: " + ex.ToString());
				throw new Exception($"Error Message: {ex.Message} \n Error Inner Exception: {ex.InnerException}");
			}
		}
	}
}