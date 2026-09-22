using AutoGen.Core;
using RideFixBro.API.Agents;
using RideFixBro.API.DataStore.Interfaces;

namespace RideFixBro.API.Services
{
	public class AiManagerService
	{
		private readonly IAgent _agent;
		private readonly IChatHistoryStore _chatHistoryStore;
		private readonly ILogger<AiManagerService> _logger;
		private readonly int _maxToolRounds;

		public AiManagerService(IAgent agent, IChatHistoryStore chatStore,
			IConfiguration config, ILogger<AiManagerService> logger)
		{
			_agent = agent;
			_chatHistoryStore = chatStore;
			_logger = logger;
			_maxToolRounds = config.GetValue("Chat:MaxToolRounds", 5);
			if (_maxToolRounds <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(config), "Chat:MaxToolRounds must be positive.");
			}
		}

		public async Task<string> AskMechanicBro(string sessionId, string userMessage,
			string? base64Image = null, CancellationToken cancellationToken = default)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
			ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

			IMessage messageToSend = new TextMessage(Role.User, userMessage);
			if (!string.IsNullOrEmpty(base64Image))
			{
				var dataUri = base64Image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
					? base64Image
					: $"data:image/jpeg;base64,{base64Image}";
				messageToSend = new MultiModalMessage(Role.User,
				[
					new TextMessage(Role.User, userMessage),
					new ImageMessage(Role.User, dataUri)
				]);
			}

			try
			{
				// Working history pe turn chala; final answer mila tabhi store isse save karega.
				return await _chatHistoryStore.UpdateHistoryAsync(sessionId, async history =>
				{
					history.Add(messageToSend);
					for (var round = 0; ; round++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var reply = await _agent.GenerateReplyAsync(history,
							new MechanicReplyOptions { ExecuteTools = round < _maxToolRounds },
							cancellationToken);

						// Tool ka result user ka final answer nahi hai; woh Gemini se alag se aayega.
						if (reply is TextMessage text && text.Role == Role.Assistant &&
							!string.IsNullOrWhiteSpace(text.Content))
						{
							history.Add(reply);
							return text.Content;
						}

						if (round >= _maxToolRounds)
						{
							throw new InvalidOperationException(
								$"Tool-calling limit of {_maxToolRounds} rounds reached. Try a narrower question.");
						}

						if (reply is not AggregateMessage<ToolCallMessage, ToolCallResultMessage> toolReply)
						{
							throw new InvalidOperationException(
								$"Expected a completed tool exchange or an assistant answer, but received {reply.GetType().Name}.");
						}

						ValidateToolExchange(toolReply);
						// Request + results saath rakh; agli iteration mein Gemini inhe padhke aage bolega.
						history.Add(toolReply);
					}
				}, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Mechanic chat failed; the incomplete turn was not saved.");
				throw;
			}
		}

		// Har call ID ka exactly ek matching result chahiye; adhura bundle Gemini ko mat bhej.
		private static void ValidateToolExchange(AggregateMessage<ToolCallMessage, ToolCallResultMessage> exchange)
		{
			var calls = exchange.Message1.ToolCalls.ToList();
			var results = exchange.Message2.ToolCalls.ToList();
			if (calls.Count == 0 || calls.Count != results.Count ||
				calls.Any(call => string.IsNullOrWhiteSpace(call.ToolCallId)) ||
				calls.Select(call => call.ToolCallId).Distinct(StringComparer.Ordinal).Count() != calls.Count)
			{
				throw new InvalidOperationException("The tool exchange has missing or duplicate call IDs or results.");
			}

			foreach (var call in calls)
			{
				var matches = results.Where(result => result.ToolCallId == call.ToolCallId).ToList();
				if (matches.Count != 1 || matches[0].FunctionName != call.FunctionName ||
					matches[0].FunctionArguments != call.FunctionArguments ||
					string.IsNullOrWhiteSpace(matches[0].Result))
				{
					throw new InvalidOperationException($"Tool '{call.FunctionName}' did not return a matching, nonempty result.");
				}
			}
		}
	}
}
