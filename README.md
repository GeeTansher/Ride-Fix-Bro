# 🏍️ RideFix Bro - The Agentic AI Motorcycle Mechanic

> "Kyunki har problem ka solution garage mein nahi milta, kabhi kabhi cloud mein bhi hota hai!"

RideFix Bro is an AI motorcycle assistant that searches technical manuals via RAG
and retrieves current information through web search. It uses a **.NET 10 API**
backend and a **Kotlin Jetpack Compose** Android frontend.

## Demo limits and disabled uploads

The application currently targets a controlled demonstration, not a publicly
authenticated service. Limits are configured in the `Chat` section of
`RideFixBro.API\appsettings.json`. Override these values through Azure App Service
settings using double underscores, for example `Chat__RequestsPerMinute`.
Restart the application after changing the configuration. Zero or negative
limits cause startup validation to fail rather than silently disabling controls.

| Setting | Default |
|---|---:|
| `MaxMessageCharacters` | 2,000 characters |
| `MaxSessionIdCharacters` | 128 characters |
| `MaxImageBytes` | 2,097,152 bytes (2 MiB), one JPEG/PNG |
| `MaxImagePixels` | 16,777,216 pixels, checked before full image decoding |
| `MaxRequestBodyBytes` | 3,145,728 bytes (3 MiB), including Base64/JSON |
| `RequestsPerMinute` | 5 per fixed one-minute window, shared by this backend instance |
| `MaxConcurrentRequests` | 1 active chat operation per instance, no waiting queue |
| `MaxHistoryTurns` | 10 completed turns, including the new successful turn |
| `MaxToolRounds` | 5 tool-calling rounds per user message |
| `MaxToolCallsPerRequest` | 6 individual tool executions across all rounds |

Message and image limits are enforced server-side. JPEG and PNG content is
decoded and validated rather than relying solely on the declared MIME type.
The request-rate and request-body limits apply only to `POST /api/Chat/ask`.
`Program.cs` registers the named rate policy and ASP.NET Core's built-in
`RequestSizeLimitAttribute`; registration does not apply them globally.
The Ask action explicitly selects these controls:

```csharp
[EnableRateLimiting("chat")]
[ServiceFilter(typeof(RequestSizeLimitAttribute))]
```

The size filter uses the configured `MaxRequestBodyBytes` value and the web
server enforces it even when `Content-Length` is absent. No custom body-buffering
filter is required. Other controller actions do not inherit these chat-specific
limits unless they explicitly opt in; normal web-server limits still apply.
Endpoints opting into the same `"chat"` policy would share its rate bucket.

The Ask action also uses a shared `SemaphoreSlim` to admit chat processing only
when a slot is available, releasing the slot in `finally`. Unrelated actions do
not acquire these slots. Ask requests rejected as busy still count toward the
one-minute request allowance. The application-wide 413 handler only formats
size errors; it does not impose a chat-size limit on other APIs.

A round containing both a manual search and an internet search counts as
**one round and two tool calls**. A batch that exceeds the remaining tool budget
is rejected before execution. Once the budget is exhausted, the model may still
produce a final answer, but no additional tools can execute.
Model requests and embedding calls are separate from this tool-execution count;
these limits are not a hard token or billing cap.

History retention removes complete older turns rather than individual tool
messages. Retained tool calls preserve their original Gemini metadata. Failed
turns and their tentative history trimming are not committed. The Android client
may still display older messages, while model context is limited to the
configured retention window.

| HTTP status | Meaning |
|---|---|
| `400` | Invalid/too-long message, invalid session ID, or invalid image |
| `413` | Request body, image bytes, or image dimensions exceed the limit |
| `429` | Request-rate or concurrency limit; no request queue is created |
| `422` | The question exceeded its tool-round or tool-call budget |
| `500` | Unexpected/provider failure; internal details stay in server logs |

The following routes have been removed and return **404**:

```text
POST /api/Chat/upload-dummy-manual
POST /api/Chat/upload-pdf-manual
```

Existing Qdrant manuals remain searchable. Ingestion service methods are retained
for future administrative functionality, but HTTP upload routes, administrator
authentication, administrator roles, and an upload interface are not currently
enabled. Reintroducing uploads requires server-side authentication and
authorization; client-side visibility or an `isAdmin` flag does not grant access.

Limits are process-local: they reset on restart and are not shared across
instances. Sessions are stored in memory, and the turn limit applies per
conversation rather than to the total number of sessions. These controls do
**not** make the chat API private. Configure network access restrictions
separately; this implementation does not modify Azure access restrictions,
Key Vault configuration, or authentication settings.

## 🚀 The Tech Stack

**Backend (The Brains):**
* **Framework:** .NET 10 Web API
* **AI Orchestration:** Microsoft AutoGen (C#)
* **LLM Engine:** Gemini 3.1 Flash Lite (Routed via OpenAI Proxy for protobuf compatibility)
* **Vector Database:** Qdrant Cloud (3072-dimension embeddings for Gemini)
* **Live Search Agent:** Tavily Search API
* **Memory:** Thread-safe In-Memory Session Store (SemaphoreSlim managed)
* **Deployment:** Azure App Service (F1 Tier)
* **API Documentation:** Scalar UI (`Scalar.AspNetCore`)

**Frontend (The Face):**
* **Framework:** Kotlin + Jetpack Compose (Modern Android UI)
* **Architecture:** MVVM (Model-View-ViewModel)
* **Networking:** Retrofit with OkHttp (Configured with 60s timeouts for Azure cold-starts)
* **Features:** Multimodal Vision (Camera/Gallery uploads), Markdown rendering.

## 🧠 How It Works (The Architecture)

The current implementation uses **one tool-enabled agent**, not a multi-agent
swarm. Responsibilities follow the request flow:

1. **HTTP setup (`Program.cs`):** Registers services and defines request-size and request-rate controls without applying them globally.
2. **API entry (`ChatController.AskBro`):** Opts into the chat limits, rejects requests when chat capacity is busy, calls the chat service, and returns HTTP responses.
3. **Input validation (`ChatInputValidator`):** Checks message/session lengths and validates an optional JPEG or PNG image.
4. **Conversation flow (`AiManagerService`):** Retains complete conversation turns, tracks tool budgets, and saves successful answers.
5. **Tools and provider messages:** `MechanicBroAgent` obtains a model reply, checks requested tools, and then executes them. `GeminiMessageConnector` preserves provider metadata when converting messages. `InMemoryChatStore` serializes updates within each session.

## 🛠️ Quick Setup (Local Development)

### Prerequisites
* .NET 10 SDK
* Qdrant Cloud Account
* Gemini API Key & Tavily API Key
* Android Studio

### Backend Setup
1. Clone the repo.
2. Add your secrets using .NET User Secrets:
   ```bash
   dotnet user-secrets set "API_Keys:Gemini_Api_key" "YOUR_KEY"
   dotnet user-secrets set "API_Keys:Tavily_Api_key" "YOUR_KEY"
   dotnet user-secrets set "Qdrant_Vector_DB:Cluster_Endpoint" "YOUR_URL"
   dotnet user-secrets set "Qdrant_Vector_DB:API_Key" "YOUR_KEY"

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


1.  Run the API: dotnet run. Visit /scalar for the UI.
    

### Frontend Setup

1.  Open the Android project in Android Studio.
    
2.  Update the RetrofitClient base URL to point to your local machine (e.g., http://10.0.2.2:5000 for emulator).
    
3.  Build and run!
    

🛣️ Future Roadmap
------------------

*   \[ \] Migrate InMemoryChatStore to Entity Framework (SQL Server) for persistent user garages.
    
*   \[ \] Cloud-based PDF dynamic chunking and upload endpoints for multiple bike manuals.
    
*   \[ \] Voice input/output integration.
    

_Built with ❤️ and a lot of caffeine by \[Geetansh Verma / Your GitHub Username\]_
