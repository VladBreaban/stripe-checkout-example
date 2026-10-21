using Stripe;
using Stripe.Checkout;

namespace StripeCheckout.Api;

public sealed class PaymentSettings
{
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string PriceId { get; set; } = "";
    public string AppUrl { get; set; } = "http://localhost:3000";

    public void Validate()
    {
        if (!SecretKey.StartsWith("sk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("A test-mode Stripe secret key is required; live keys are rejected.");
        if (!WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal) ||
            !PriceId.StartsWith("price_", StringComparison.Ordinal))
            throw new InvalidOperationException("Configure Stripe:WebhookSecret and Stripe:PriceId.");
        if (!Uri.TryCreate(AppUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme == "http" && !uri.IsLoopback))
            throw new InvalidOperationException("AppUrl must be an HTTPS origin, or HTTP on localhost.");
        AppUrl = uri.GetLeftPart(UriPartial.Authority);
    }
}

public interface ICheckoutGateway
{
    Task<string> CreateAsync(string idempotencyKey, CancellationToken ct);
}

public sealed class CheckoutGateway(IStripeClient client, PaymentSettings settings) : ICheckoutGateway
{
    public async Task<string> CreateAsync(string idempotencyKey, CancellationToken ct)
    {
        var session = await new SessionService(client).CreateAsync(
            BuildOptions(settings),
            new RequestOptions { IdempotencyKey = $"checkout:{idempotencyKey}" }, ct);
        return session.Url ?? throw new InvalidOperationException("Stripe did not return a checkout URL.");
    }

    public static SessionCreateOptions BuildOptions(PaymentSettings settings) => new()
    {
        Mode = "payment",
        LineItems = [new SessionLineItemOptions { Price = settings.PriceId, Quantity = 1 }],
        SuccessUrl = $"{settings.AppUrl}/success",
        CancelUrl = $"{settings.AppUrl}/cancel",
    };
}
