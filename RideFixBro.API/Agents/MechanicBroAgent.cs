using AutoGen.Core;
using AutoGen.OpenAI;
using OpenAI.Chat;
using RideFixBro.API.Services;
using System.Text.Json;
using IMiddleware = AutoGen.Core.IMiddleware;

namespace RideFixBro.API.Agents
{
	public static class MechanicBroAgent
	{
		public static IAgent Create(ChatClient chatClient, TavilySearchService tavilyService, VectorDbService vectorDbService)
		{
			// Contract Gemini ko batata hai tool KYA karta hai; map batata hai C# mein KISE chalana hai.
			// Generated wrapper JSON arguments ko method ke parameters mein badalta hai.
			return Create(chatClient,
				[tavilyService.SearchInternetAsyncFunctionContract, vectorDbService.SearchManualAsyncFunctionContract],
				new Dictionary<string, Func<string, Task<string>>>
				{
					[nameof(TavilySearchService.SearchInternetAsync)] = tavilyService.SearchInternetAsyncWrapper,
					[nameof(VectorDbService.SearchManualAsync)] = vectorDbService.SearchManualAsyncWrapper
				});
		}

		internal static IAgent Create(ChatClient chatClient, IEnumerable<FunctionContract> functions,
			IDictionary<string, Func<string, Task<string>>> functionMap)
		{
			// Setup ek hi hai; tests bhi isi pipeline mein apne fake tools laga sakte hain.
			var safeFunctionMap = functionMap.ToDictionary(entry => entry.Key,
				entry => WrapToolResult(entry.Key, entry.Value), StringComparer.Ordinal);
			var toolMiddleware = new MechanicToolMiddleware(functions, safeFunctionMap);
			var openAIAgent = new OpenAIChatAgent(
				chatClient: chatClient,
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

			// Bahar tool middleware, andar message connector, uske andar Gemini ko call karne wala agent.
			return openAIAgent.RegisterMiddleware(new GeminiMessageConnector()).RegisterMiddleware(toolMiddleware);
		}

		private static Func<string, Task<string>> WrapToolResult(string name, Func<string, Task<string>> execute)
		{
			return async arguments =>
			{
				using var document = JsonDocument.Parse(arguments);
				if (document.RootElement.ValueKind != JsonValueKind.Object)
				{
					throw new JsonException($"Tool '{name}' arguments must be a JSON object.");
				}
				var result = await execute(arguments);
				if (string.IsNullOrWhiteSpace(result))
				{
					throw new InvalidOperationException($"Tool '{name}' returned an empty result.");
				}
				// Result ka JSON haath se mat jod, bhai; serializer quotes aur newlines sahi escape karega.
				return JsonSerializer.Serialize(new { result });
			};
		}
	}

	internal sealed class MechanicReplyOptions : GenerateReplyOptions
	{
		// Ye apne middleware ka switch hai, Gemini API ka parameter nahi.
		public bool ExecuteTools { get; init; } = true;
		public int RemainingToolCalls { get; init; } = int.MaxValue;
	}

	internal sealed class MechanicToolMiddleware : IMiddleware
	{
		private readonly FunctionCallMiddleware _execute;
		private readonly FunctionCallMiddleware _describe;
		private readonly IDictionary<string, Func<string, Task<string>>> _tools;

		public MechanicToolMiddleware(IEnumerable<FunctionContract> functions,
			IDictionary<string, Func<string, Task<string>>> functionMap)
		{
			var contracts = functions.ToArray();
			_execute = new FunctionCallMiddleware(contracts, functionMap);
			_describe = new FunctionCallMiddleware(contracts);
			_tools = functionMap;
		}

		public string Name => nameof(MechanicToolMiddleware);

		public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent,
			CancellationToken cancellationToken = default)
		{
			var executeTools = true;
			var remaining = int.MaxValue;
			if (context.Options is MechanicReplyOptions options)
			{
				executeTools = options.ExecuteTools;
				remaining = options.RemainingToolCalls;
			}

			// Pehle se tool request di ho toh model ko dobara bulane ki zarurat nahi.
			if (context.Messages.LastOrDefault() is ToolCallMessage pendingCall)
			{
				if (!executeTools)
				{
					return pendingCall;
				}
				ValidateToolRequest(pendingCall, remaining);
				return await _execute.InvokeAsync(context, agent, cancellationToken);
			}

			// 1. Gemini ka reply lo. Abhi tools execute nahi hue hain.
			var reply = await _describe.InvokeAsync(context, agent, cancellationToken);
			if (reply is not ToolCallMessage call || !executeTools)
			{
				return reply;
			}

			// 2. Poora batch check karo, phir 3. tools chalao.
			ValidateToolRequest(call, remaining);
			var executionContext = new MiddlewareContext(new IMessage[] { call }, context.Options);
			var result = await _execute.InvokeAsync(executionContext, agent, cancellationToken);
			if (result is not ToolCallResultMessage toolResult)
			{
				throw new InvalidOperationException("Tool execution did not return a result message.");
			}
			return new ToolCallAggregateMessage(call, toolResult, from: agent.Name);
		}

		private void ValidateToolRequest(ToolCallMessage call, int remaining)
		{
			if (call.ToolCalls.Count() > remaining)
			{
				throw new ChatLimitExceededException("Bhai, is request ka tool-call budget khatam ho raha hai. Sawal thoda chhota kar.");
			}
			foreach (var tool in call.ToolCalls)
			{
				if (!_tools.ContainsKey(tool.FunctionName))
				{
					throw new InvalidOperationException($"Tool '{tool.FunctionName}' is not registered.");
				}
			}
		}
	}
}