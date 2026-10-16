using System.Text.Json;
using SignDesk.Infrastructure.Documents;
using SignDesk.Infrastructure.Webhooks;

namespace SignDesk.Api;

public static class WebhookEndpoints
{
    public const string SignatureHeader = "SignDesk-Signature";
    public const int MaxPayloadBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/signing-provider", async (
            HttpRequest request,
            WebhookSignatureVerifier verifier,
            DocumentService service,
            CancellationToken ct) =>
        {
            if (request.ContentLength > MaxPayloadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            var check = verifier.Verify(request.Headers[SignatureHeader], payload);
            if (check != SignatureCheck.Valid)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: $"Invalid signature: {check}");
            }

            WebhookEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<WebhookEnvelope>(payload, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                envelope = null;
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Id) || envelope.Data is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed event payload.");
            }

            var result = await service.ApplyWebhookAsync(
                envelope.Id, envelope.Type, envelope.Data.DocumentId, envelope.Data.SignerId, envelope.Data.Reason, ct);
            return Results.Ok(new { received = envelope.Id, result = result.ToString().ToLowerInvariant() });
        })
        .WithTags("Webhooks");

        return app;
    }
}
