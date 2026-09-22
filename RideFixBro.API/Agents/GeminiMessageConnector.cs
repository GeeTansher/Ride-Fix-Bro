using AutoGen.Core;
using AutoGen.OpenAI;
using OpenAI.Chat;
using IMiddleware = AutoGen.Core.IMiddleware;

namespace RideFixBro.API.Agents
{
	internal sealed class GeminiMessageConnector : IMiddleware
	{
		private readonly OpenAIChatRequestMessageConnector _connector = new(strictMode: true);

		public string Name => nameof(GeminiMessageConnector);

		public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent,
			CancellationToken cancellationToken = default)
		{
			var messages = context.Messages.SelectMany(ExpandToolExchange);
			var request = _connector.ProcessIncomingMessages(agent, messages);
			var reply = await agent.GenerateReplyAsync(request, context.Options, cancellationToken);
			var converted = _connector.PostProcessMessage(reply);

			if (reply is IMessage<ChatCompletion> completion && converted is ToolCallMessage toolCall)
			{
				// Standard connector calls dobara banata hai, toh Google's extra metadata chhoot jata hai.
				// Asli SDK tool calls sambhal: thought_signature jaisa mila, waisa hi wapas jaana chahiye.
				var assistantMessage = new AssistantChatMessage(completion.Content);
				if (assistantMessage.Content.Count == 0)
				{
					// Tool-only reply mein text na ho toh explicit empty content bhejenge.
					assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(string.Empty));
				}
				return new GeminiToolCallMessage(toolCall, assistantMessage);
			}

			return converted;
		}

		private static IEnumerable<IMessage> ExpandToolExchange(IMessage message)
		{
			if (message is AggregateMessage<ToolCallMessage, ToolCallResultMessage> aggregate &&
				aggregate.Message1 is GeminiToolCallMessage call)
			{
				// Bundle khol: pehle assistant ki tool request, phir uske results. Order mat ulatna.
				// Envelope asli SDK message ko dobara convert hone se bachata hai.
				yield return new MessageEnvelope<ChatMessage>(call.AssistantMessage, from: call.From);
				yield return aggregate.Message2;
			}
			else if (message is GeminiToolCallMessage toolCall)
			{
				yield return new MessageEnvelope<ChatMessage>(toolCall.AssistantMessage, from: toolCall.From);
			}
			else
			{
				yield return message;
			}
		}

		private sealed class GeminiToolCallMessage : ToolCallMessage
		{
			// AutoGen ko apna ToolCallMessage chahiye; agle Gemini request ko asli SDK message bhi chahiye.
			public GeminiToolCallMessage(ToolCallMessage call, AssistantChatMessage assistantMessage)
				: base(call.ToolCalls, call.From)
			{
				Content = call.Content ?? string.Empty;
				AssistantMessage = assistantMessage;
			}

			public AssistantChatMessage AssistantMessage { get; }
		}
	}
}
