using AutoGen.Core;
using OpenAI.Chat;
using RideFixBro.API.Agents.Helper;
using System.ClientModel.Primitives;
using System.Text.Json;
using DbMessage = RideFixBro.Data.Entities.Message;

namespace RideFixBro.API.DataStore;

// Display text alag hai; model context ko sidebar/message UI mein expose nahi karna.
internal sealed class UserChatMessage(string content, string displayText) : TextMessage(Role.User, content)
{
    public string DisplayText { get; } = displayText;
}

internal static class ChatMessageCodec
{
    private const string MissingPhoto = "\n[The photo from this earlier turn was not stored and is no longer available. Ask for a new photo if needed; do not claim to see it.]";
    private sealed record Call(string Id, string Name, string Arguments, string? Result);
    private sealed record Payload(int Version, string Kind, string? Text = null, string? From = null,
        bool PhotoNotStored = false, string? AssistantJson = null, List<Call>? Calls = null);

    public static DbMessage Encode(IMessage message)
    {
        if (message is TextMessage text)
            return Row(text.Role == Role.User ? "User" : text.Role == Role.Assistant ? "Assistant" :
                text.Role == Role.System ? "System" : throw new InvalidOperationException("Invalid text role."),
                text is UserChatMessage user ? user.DisplayText : text.Content,
                new(1, "text", text.Content, text.From));
        if (message is MultiModalMessage photo)
        {
            // Photo current request tak hai; SQL mein bytes nahi, text aur missing-photo disclosure save hoti hai.
            var textPart = photo.Content.OfType<TextMessage>().Single();
            return Row("User", textPart is UserChatMessage user ? user.DisplayText : textPart.Content,
                new(1, "text", textPart.Content + MissingPhoto, photo.From, PhotoNotStored: true));
        }
        if (message is AggregateMessage<ToolCallMessage, ToolCallResultMessage> exchange)
        {
            // Original SDK JSON Gemini thought signatures preserve karta hai; sirf tool name/args se woh kho jaayengi.
            var assistant = exchange.Message1 is GeminiMessageConnector.GeminiToolCallMessage gemini
                ? ModelReaderWriter.Write(gemini.AssistantMessage).ToString()
                : null;
            var results = exchange.Message2.ToolCalls.ToDictionary(call =>
                call.ToolCallId ?? throw new InvalidOperationException("Tool result ID missing."));
            return Row("Tool", null, new(1, "tools", exchange.Message1.Content, exchange.From,
                AssistantJson: assistant, Calls: exchange.Message1.ToolCalls.Select(call =>
                    new Call(call.ToolCallId ?? throw new InvalidOperationException("Tool call ID missing."),
                        call.FunctionName, call.FunctionArguments, results[call.ToolCallId!].Result)).ToList()));
        }
        throw new InvalidOperationException($"Cannot persist message type {message.GetType().Name}.");
    }

    public static IMessage Decode(DbMessage row)
    {
        if (row.PayloadJson is null)
        {
            if (row.Content is null || row.Role is not ("User" or "Assistant" or "System"))
                throw new InvalidOperationException("Stored message is missing its payload.");
            return new TextMessage(ParseRole(row.Role), row.Content);
        }
        var payload = Read(row);
        if (payload.Kind == "text" && payload.Text is not null)
            return new TextMessage(ParseRole(row.Role), payload.Text, payload.From);
        if (payload.Kind == "tools" && payload.Calls is { Count: > 0 } calls)
        {
            ToolCallMessage request = new(calls.Select(call =>
                new ToolCall(call.Name, call.Arguments) { ToolCallId = call.Id }), payload.From);
            request.Content = payload.Text;
            if (payload.AssistantJson is not null)
                request = new GeminiMessageConnector.GeminiToolCallMessage(request,
                    ModelReaderWriter.Read<AssistantChatMessage>(BinaryData.FromString(payload.AssistantJson))
                        ?? throw new InvalidOperationException("Stored assistant tool request is missing."));
            var results = new ToolCallResultMessage(calls.Select(call =>
                new ToolCall(call.Name, call.Arguments, call.Result ?? throw new InvalidOperationException("Stored tool result missing."))
                    { ToolCallId = call.Id }), payload.From);
            return new ToolCallAggregateMessage(request, results, payload.From);
        }
        throw new InvalidOperationException("Stored message payload is invalid.");
    }

    public static bool PhotoNotStored(DbMessage row) =>
        row.PayloadJson is not null && Read(row).PhotoNotStored;

    private static Payload Read(DbMessage row)
    {
        var payload = JsonSerializer.Deserialize<Payload>(row.PayloadJson!);
        return payload is { Version: 1 } ? payload : throw new InvalidOperationException("Unknown stored chat payload version.");
    }

    private static Role ParseRole(string role) => role switch
    {
        "User" => Role.User, "Assistant" => Role.Assistant, "System" => Role.System,
        _ => throw new InvalidOperationException("Invalid stored text role.")
    };

    private static DbMessage Row(string role, string? content, Payload payload) =>
        new() { Role = role, Content = content, PayloadJson = JsonSerializer.Serialize(payload), Timestamp = DateTime.UtcNow };
}
