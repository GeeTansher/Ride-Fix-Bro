# 🏍️ RideFix Bro - The Agentic AI Motorcycle Mechanic

> "Kyunki har problem ka solution garage mein nahi milta, kabhi kabhi cloud mein bhi hota hai!"

RideFix Bro is a high-performance, multi-agent AI assistant designed to diagnose motorcycle issues, search technical manuals via RAG, and fetch live market data. Built with a robust **.NET 10 API** backend and a sleek **Kotlin Jetpack Compose** Android frontend.

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

RideFix Bro isn't just a simple chatbot. It uses a **Multi-Agent Orchestration Loop**:
1. **Dynamic Tool Routing:** The LLM decides whether to search the internet (for latest gear prices/reviews) or query the Vector DB (for specific bike torque specs and error codes).
2. **Custom Message Translation:** A custom `GeminiMessageConnector` middleware intercepts and translates AutoGen SDK messages into Gemini-compatible structures, preventing `400 Bad Request` proxy sequence errors.
3. **Thread-Safe Memory:** Uses a `ConcurrentDictionary` and `SemaphoreSlim` to maintain perfect chat history sequences per user session without race conditions.

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
