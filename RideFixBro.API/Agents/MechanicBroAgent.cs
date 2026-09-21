using AutoGen.Core;
using AutoGen.OpenAI;
using AutoGen.OpenAI.Extension;
using RideFixBro.API.Services;

namespace RideFixBro.API.Agents
{
	public static class MechanicBroAgent
	{
		// Yeh function tere agent ko initialize karke wapas dega
		public static IAgent Create(string apiKey, string tavilyApiKey, VectorDbService vectorDbService)
		{
			try
			{
				// 1. Tavily Service ko zinda kar
				var tavilyService = new TavilySearchService(tavilyApiKey);

				// 2. Middleware bana (Ye auto-generated property 'SearchInternetAsyncFunctionContract' use karega)
				var toolMiddleware = new FunctionCallMiddleware(
					functions: [
						tavilyService.SearchInternetAsyncFunctionContract, // Internet wala tool
						vectorDbService.SearchManualAsyncFunctionContract  // Manual wala tool
					]
				);

				// OpenAI client banaya
				var openAIClient = OpenAIClientBuilder.Create(apiKey);

				// Ab hum GeminiChatAgent ki jagah OpenAIChatAgent use kar rahe hain
				var openAIAgent = new OpenAIChatAgent(
					chatClient: openAIClient.GetChatClient("gemini-3.1-flash-lite"),
					name: "MechanicBro",
					systemMessage: @"Tu ek expert motorcycle mechanic hai. Tera naam 'Ride Fix Bro' hai. 
					Tera tone ekdum casual, hinglish aur friendly 'bro' wala hona chahiye, jaise ek dost dusre dost ko garage mein bike theek karte time advice deta hai. Formal hindi ya english bilkul mat use karna. 
					Agar user kuch galat bol raha hai toh direct point out kar aur sahi kar, haan mein haan nahi milani. Faltu ki bakwaas nahi, kam shabdon mein solid knowledge de.

					TOOLS USE KARNE KE STRICT RULES: 
					1. INTERNET SEARCH: Agar tujhe kisi gear, tyre, helmet ya parts ka LATEST price, reviews ya current info chahiye, toh chup-chaap 'SearchInternetAsync' tool call kar lena. Hawa me teer mat marna aur fake price mat batana.
					2. BIKE MANUAL & TECHNICALS: Agar bike ki exact repair steps, torque specs, ya technical details (jaise spark plug badalna) chahiye toh 'SearchManualAsync' tool call kar. 

					STRICT INSTRUCTIONS:
					1. Agar user ne koi photo bheji hai, toh usko dhyan se dekh aur diagnose kar.
					2. Agar tool/manual ki info mein answer nahi hai, toh apni general motorcycle knowledge use kar, par user ko bata dena ki 'Bhai, manual mein exact likha nahi hai, par experience se bata raha hoon...'
					3. Faltu me zabardasti bike ka technical gyaan mat pelna agar poocha na jaye. Exact point pe baat kar.
					4. Agar user sirf 'Hi', 'Hello', 'Kaise ho' bol raha hai, ya kisi general topic (jaise trips ya riding jackets ki casual baat) pe baat kar raha hai, toh kisi tool ko call mat karna. Bas ek normal cool dost ki tarah apni general knowledge se direct reply karna."
				);

				// 3. Pehle message connector lagaya, fir apna web search wala tool pehna diya
				return openAIAgent.RegisterMessageConnector().RegisterMiddleware(toolMiddleware);
			}
			catch(Exception ex)
			{
				Console.WriteLine("Error in Mechanic Bro Service: " + ex.ToString());
				throw new Exception($"Error Message: {ex.Message} \n Error Inner Exception: {ex.InnerException}");
			}
		}
	}
}