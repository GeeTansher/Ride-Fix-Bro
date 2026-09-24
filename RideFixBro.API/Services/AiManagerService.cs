using AutoGen.Core;
using RideFixBro.API.Agents;
using RideFixBro.API.Configuration;
using RideFixBro.API.DataStore.Interfaces;

namespace RideFixBro.API.Services
{
	public class AiManagerService(IAgent agent, IChatHistoryStore chatStore,
        ChatLimitsOptions limits, ChatInputValidator inputValidator, ILogger<AiManagerService> logger)
    {
		private readonly IAgent _agent = agent;
		private readonly IChatHistoryStore _chatHistoryStore = chatStore;
		private readonly ILogger<AiManagerService> _logger = logger;
		private readonly ChatLimitsOptions _limits = limits;
		private readonly ChatInputValidator _inputValidator = inputValidator;

        public async Task<string> AskMechanicBro(string sessionId, string userMessage,
			string? base64Image = null, CancellationToken cancellationToken = default)
		{
			try
			{
				var dataUri = _inputValidator.Validate(sessionId, userMessage, base64Image);
				IMessage messageToSend = new TextMessage(Role.User, userMessage);
				if (dataUri is not null)
				{
					messageToSend = new MultiModalMessage(Role.User,
					[
						new TextMessage(Role.User, userMessage),
						new ImageMessage(Role.User, dataUri)
					]);
				}

				// Working history pe turn chala; final answer mila tabhi store isse save karega.
				return await _chatHistoryStore.UpdateHistoryAsync(sessionId, async history =>
				{
					TrimHistory(history, _limits.MaxHistoryTurns - 1);
					history.Add(messageToSend);
					var toolCallsExecuted = 0;
					for (var round = 0; ; round++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var reply = await _agent.GenerateReplyAsync(history,
							new MechanicReplyOptions
							{
								ExecuteTools = round < _limits.MaxToolRounds,
								RemainingToolCalls = _limits.MaxToolCallsPerRequest - toolCallsExecuted
							},
							cancellationToken);

						// Tool ka result user ka final answer nahi hai; woh Gemini se alag se aayega.
						if (reply is TextMessage text && text.Role == Role.Assistant &&
							!string.IsNullOrWhiteSpace(text.Content))
						{
							history.Add(reply);
							return text.Content;
						}

						if (round >= _limits.MaxToolRounds)
						{
							throw new ChatLimitExceededException(
								$"Bhai, {_limits.MaxToolRounds} rounds ki tool limit aa gayi. Sawal thoda chhota kar.");
						}

						if (reply is not AggregateMessage<ToolCallMessage, ToolCallResultMessage> toolReply)
						{
							throw new InvalidOperationException(
								$"Expected a completed tool exchange or an assistant answer, but received {reply.GetType().Name}.");
						}

						ValidateToolExchange(toolReply);
						toolCallsExecuted += toolReply.Message1.ToolCalls.Count();
						// Request + results saath rakh; agli iteration mein Gemini inhe padhke aage bolega.
						history.Add(toolReply);
					}
				}, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (ChatInputException)
			{
				throw;
			}
			catch (ChatLimitExceededException ex)
			{
				_logger.LogWarning("Chat budget exceeded; incomplete turn not saved: {Reason}", ex.Message);
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Mechanic chat failed; the incomplete turn was not saved.");
				throw;
			}
		}

		private static void TrimHistory(List<IMessage> history, int turnsToKeep)
		{
			if (turnsToKeep == 0)
			{
				history.Clear();
				return;
			}

			var turnsFound = 0;
			for (var index = history.Count - 1; index >= 0; index--)
			{
				var isUserMessage = false;
				if (history[index] is TextMessage text)
				{
					isUserMessage = text.Role == Role.User;
				}
				else if (history[index] is MultiModalMessage image)
				{
					isUserMessage = image.Role == Role.User;
				}

				if (!isUserMessage)
				{
					continue;
				}
				turnsFound++;
				if (turnsFound == turnsToKeep)
				{
					// Latest allowed turns mil gaye; unse pehle ka poora context hata do.
					history.RemoveRange(0, index);
					return;
				}
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
