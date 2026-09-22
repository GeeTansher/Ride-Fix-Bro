using AutoGen.Core;
using OpenAI.Chat;
using RideFixBro.API.Agents;
using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// services
builder.Services.AddScoped<RideFixBro.API.Services.AiManagerService>();
builder.Services.AddSingleton<RideFixBro.API.Services.VectorDbService>();
builder.Services.AddSingleton<IChatHistoryStore, InMemoryChatStore>();
builder.Services.AddSingleton<ChatClient>(services =>
{
	var config = services.GetRequiredService<IConfiguration>();
	var apiKey = config["API_Keys:Gemini_Api_key"]
		?? throw new InvalidOperationException("Gemini API key is missing.");
	return OpenAIClientBuilder.Create(apiKey).GetChatClient("gemini-3.1-flash-lite");
});
builder.Services.AddHttpClient<TavilySearchService>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<IAgent>(services => MechanicBroAgent.Create(
	services.GetRequiredService<ChatClient>(),
	services.GetRequiredService<TavilySearchService>(),
	services.GetRequiredService<VectorDbService>()));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
	app.MapScalarApiReference();

	app.MapGet("/", () => Results.Redirect("/scalar"))
		.ExcludeFromDescription();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
