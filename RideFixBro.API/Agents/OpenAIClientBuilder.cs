using AutoGen.Core;
using AutoGen.OpenAI;
using OpenAI;
using System.ClientModel;

namespace RideFixBro.API.Agents
{
	public class OpenAIClientBuilder
	{
		// Yeh function tere open ai client ko initialize karke wapas dega
		public static OpenAIClient Create(string apiKey)
		{
			// OpenAI client banaya par URL Gemini ka daal diya!
			var openAIClient = new OpenAIClient(
				new ApiKeyCredential(apiKey),
				new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") }
			);
			
			return openAIClient;
		}
	}
}
