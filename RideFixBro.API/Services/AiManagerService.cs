using AutoGen.Core;
using RideFixBro.API.Agents;
using RideFixBro.API.Configuration;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Models;
using RideFixBro.API.DataStore;

namespace RideFixBro.API.Services
{
	public class AiManagerService(IAgent agent, IChatHistoryStore chatStore,
        ChatLimitsOptions limits, ILogger<AiManagerService> logger)
    {
		private readonly IAgent _agent = agent;
		private readonly IChatHistoryStore _chatHistoryStore = chatStore;
		private readonly ILogger<AiManagerService> _logger = logger;
		private readonly ChatLimitsOptions _limits = limits;

        public async Task<string> AskMechanicBro(ChatContext chat, ChatInput input, CancellationToken cancellationToken = default)
		{
			try
			{
				if (chat.Id <= 0) throw new ChatInputException("A persisted chat ID is required.");
				var previous = await _chatHistoryStore.LoadRecentAsync(chat.Id, _limits.MaxHistoryTurns - 1, cancellationToken);
				var history = previous.Messages.ToList();
				var turn = new List<IMessage> { CreateUserMessage(chat, input) };
				history.AddRange(turn);
				var toolCallsExecuted = 0;
				for (var round = 0; ; round++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var reply = await _agent.GenerateReplyAsync(history,
						new MechanicReplyOptions
						{
							ExecuteTools = round < _limits.MaxToolRounds,
							RemainingToolCalls = _limits.MaxToolCallsPerRequest - toolCallsExecuted,
							ManualKey = chat.Bike?.ManualKey
						},
						cancellationToken);

					if (reply is TextMessage text && text.Role == Role.Assistant &&
						!string.IsNullOrWhiteSpace(text.Content))
					{
						turn.Add(reply);
						// Save only this complete turn; old SQL history is never trimmed or rewritten.
						await _chatHistoryStore.AppendTurnAsync(chat.Id, previous, turn, cancellationToken);
						return text.Content;
					}

					if (round >= _limits.MaxToolRounds)
						throw new ChatLimitExceededException(
							$"Bhai, {_limits.MaxToolRounds} rounds ki tool limit aa gayi. Sawal thoda chhota kar.");
					if (reply is not AggregateMessage<ToolCallMessage, ToolCallResultMessage> toolReply)
						throw new InvalidOperationException(
							$"Expected a completed tool exchange or an assistant answer, but received {reply.GetType().Name}.");

					ValidateToolExchange(toolReply);
					toolCallsExecuted += toolReply.Message1.ToolCalls.Count();
					turn.Add(toolReply);
					// Tool result final answer nahi; next model round ko complete exchange dikhao.
					history.Add(toolReply);
				}
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
				// Provider cancellation ko generic server error mat banao.
				cancellationToken.ThrowIfCancellationRequested();
				_logger.LogError(ex, "Mechanic chat failed; the incomplete turn was not saved.");
				throw;
			}
		}

		private static IMessage CreateUserMessage(ChatContext chat, ChatInput input)
		{
			var context = chat.Bike is { } bike
				? $"[App-selected motorcycle: {bike.Make} {bike.Model}, year {bike.Year}. Do not substitute another bike's specifications. Retrieved manual passages must confirm applicability before presenting model-year-specific specifications.]"
				: "[General chat: no motorcycle is selected. Manual-backed or bike-specific details are not guaranteed. Do not claim to have consulted a bike manual. Ask the user to start a bike-specific chat for applicable manual information.]";
			var text = new UserChatMessage($"{input.Message}\n\n{context}", input.Message);
			return input.ImageDataUri is null ? text :
				new MultiModalMessage(Role.User, [text, new ImageMessage(Role.User, input.ImageDataUri)]);
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
