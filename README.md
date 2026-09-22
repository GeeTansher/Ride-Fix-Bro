# Ride-Fix-Bro

## Chat tool execution

The API uses AutoGen.OpenAI with Google's OpenAI-compatible endpoint. Both search
tools register their generated contracts **and** execution wrappers. Successful
results are sent as JSON strings with a `result` property.

The Gemini message connector retains the SDK's original tool-call objects,
including `extra_content.google.thought_signature`. Do not replace them with
new tool calls containing only the name, arguments, and ID: that loses provider
metadata needed by subsequent requests.

Each turn runs until an assistant answer is received. `Chat:MaxToolRounds` in
`RideFixBro.API\appsettings.json` defaults to **5**; override it with the
`Chat__MaxToolRounds` environment variable if needed. Multiple tools requested
in one assistant message count as one round. After the fifth round, the model
can produce a final answer, but no sixth batch of tools is executed.

History is committed only after a nonempty final answer. Tool errors, provider
errors, cancellations, and exhausted budgets leave the saved history unchanged.
Requests for the same nonempty `SessionId` are serialized; different sessions
can proceed independently. Tool failures are logged and surfaced as errors,
not saved as empty results. A successful manual lookup with no matches returns
an explicit no-match message.

The Android chat ViewModel creates one session ID and sends it with every
message in that conversation. Scalar/API callers must also supply a nonempty
`sessionId` and reuse it for follow-up messages.

History is still in-memory and is lost on restart. Restart the existing API
process once when applying this fix to discard any previously corrupted sessions.

## Regression tests

From the repository root:

```powershell
dotnet test .\RideFixBro.API.Tests\RideFixBro.API.Tests.csproj
```

These tests use mocked HTTP responses with the actual AutoGen/OpenAI conversion
and serialization pipeline. They do not call live Gemini, Tavily, or Qdrant APIs
or consume provider quota.

The Android request-serialization test requires the project's Java and Android
SDK setup:

```powershell
Set-Location .\RideFixBro
.\gradlew.bat :app:testDebugUnitTest --tests com.example.ridefixbro.ChatRequestTest
```