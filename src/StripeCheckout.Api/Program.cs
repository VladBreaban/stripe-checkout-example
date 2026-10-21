using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Stripe;
using Stripe.Checkout;
using StripeCheckout.Api;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Stripe").Get<PaymentSettings>()
    ?? throw new InvalidOperationException("Configure the Stripe section using user-secrets or environment variables.");
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IStripeClient>(new StripeClient(settings.SecretKey));
builder.Services.AddSingleton<ICheckoutGateway, CheckoutGateway>();
builder.Services.AddSingleton(new ReceiptLedger(
    builder.Configuration["DatabasePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "payments.db")));

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (Exception)
    {
        // Never log raw payloads, credentials or personal payment details.
        app.Logger.LogError("Payment request failed; check Stripe events and database availability.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Request could not be processed." });
        }
    }
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapPost("/checkout", async (HttpRequest request, ICheckoutGateway gateway, CancellationToken ct) =>
{
    var key = request.Headers["Idempotency-Key"].ToString();
    if (!Regex.IsMatch(key, @"\A[a-zA-Z0-9_-]{16,100}\z"))
        return Results.BadRequest(new { error = "Provide an Idempotency-Key of 16–100 letters, digits, underscores or hyphens." });
    var url = await gateway.CreateAsync(key, ct);
    return Results.Ok(new { url });
});

app.MapPost("/webhook", async (HttpContext context, ReceiptLedger ledger, PaymentSettings config) =>
{
    var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = 1_048_576;
    if (context.Request.ContentLength > 1_048_576)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    Event stripeEvent;
    try
    {
        // Read original bytes BEFORE any JSON deserialization.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + count > 1_048_576)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, count);
        }
        var payload = Encoding.UTF8.GetString(buffer.ToArray());
        stripeEvent = EventUtility.ConstructEvent(
            payload, context.Request.Headers["Stripe-Signature"].ToString(), config.WebhookSecret);
    }
    catch (Exception ex) when (ex is StripeException or ArgumentException or Newtonsoft.Json.JsonException)
    {
        return Results.BadRequest(new { error = "Invalid webhook signature or payload." });
    }
    if (stripeEvent.Livemode)
        return Results.BadRequest(new { error = "Only test-mode events are accepted." });
    // A storage error must return 500, so Stripe can retry.
    ledger.Record(stripeEvent);
    return Results.Ok(new { received = true });
});

app.MapGet("/success", () => Results.Ok(new {
    message = "Returned from Checkout. Only a signed paid webhook confirms payment."
}));
app.MapGet("/cancel", () => Results.Ok(new { message = "Checkout cancelled. You can try again." }));
app.Run();

public partial class Program { }
