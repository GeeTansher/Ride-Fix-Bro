using AutoGen.Core;
using Microsoft.AspNetCore.RateLimiting;
using OpenAI.Chat;
using RideFixBro.API.Agents;
using RideFixBro.API.Configuration;
using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Filters;
using RideFixBro.API.Services;
using Scalar.AspNetCore;
using System.Globalization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var chatLimits = new ChatLimitsOptions();
builder.Configuration.GetSection(ChatLimitsOptions.SectionName).Bind(chatLimits);
chatLimits.Validate();

builder.Services.AddControllers();
// Sirf register kiya hai; jis action par ServiceFilter lagega, wahi ye limit use karega.
builder.Services.AddSingleton(new ChatBodyLimit(chatLimits.MaxRequestBodyBytes));
builder.Services.AddSingleton(chatLimits);
builder.Services.AddSingleton<ChatInputValidator>();
// free tier h to concurrent requests limit lagana padega; nahi toh Gemini ke free tier me 429 aa jayega, else anyone sponser!!
builder.Services.AddSingleton<SemaphoreSlim>(_ =>
	new SemaphoreSlim(chatLimits.MaxConcurrentRequests, chatLimits.MaxConcurrentRequests));

builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.AddFixedWindowLimiter(ChatLimitsOptions.SectionName, limiter =>
	{
		limiter.PermitLimit = chatLimits.RequestsPerMinute;
		limiter.Window = TimeSpan.FromMinutes(1);
		limiter.QueueLimit = 0;
		limiter.AutoReplenishment = true;
	});
	options.OnRejected = async (context, cancellationToken) =>
	{
		if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
		{
			context.HttpContext.Response.Headers.RetryAfter =
				Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
		}
		await context.HttpContext.Response.WriteAsJsonAsync(new
		{
			Error = "Bhai, abhi chat requests ki limit aa gayi. Thoda ruk ke dobara try kar."
		}, cancellationToken);
	};
});

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

// Server body reject kare toh client ko bhi wahi clear 413 error mile.
app.Use(async (context, next) =>
{
	try
	{
		await next(context);
	}
	catch (BadHttpRequestException ex) when (
		ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
	{
		context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
		await context.Response.WriteAsJsonAsync(new
		{
			Error = "Bhai, request allowed size se badi hai. Data/file chhoti karke dobara try kar."
		}, context.RequestAborted);
	}
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
	app.MapScalarApiReference();

	app.MapGet("/", () => Results.Redirect("/scalar"))
		.ExcludeFromDescription();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
