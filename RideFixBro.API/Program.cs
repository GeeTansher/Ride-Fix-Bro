using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// services
builder.Services.AddScoped<RideFixBro.API.Services.AiManagerService>();
builder.Services.AddSingleton<RideFixBro.API.Services.VectorDbService>();
builder.Services.AddSingleton<IChatHistoryStore, InMemoryChatStore>();

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
