using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SignDesk.Api;
using SignDesk.Infrastructure;
using SignDesk.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("SignDesk")
    ?? throw new InvalidOperationException("ConnectionStrings:SignDesk is not configured.");
var webhookSecret = builder.Configuration["Webhooks:SigningProviderSecret"]
    ?? throw new InvalidOperationException("Webhooks:SigningProviderSecret is not configured.");

builder.Services.AddSignDeskInfrastructure(
    connectionString,
    webhookSecret,
    builder.Configuration.GetValue("BackgroundWorkers:Enabled", true));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandling>();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<SignDeskDbContext>("database");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();
app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapDocuments();
app.MapWebhooks();

await app.RunAsync();

public partial class Program;
