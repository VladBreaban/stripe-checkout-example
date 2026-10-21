using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using Stripe.Checkout;
using StripeCheckout.Api;
using Xunit;

namespace StripeCheckout.Tests;

public sealed class PaymentsTests
{
    private static Event Paid(string id = "evt_example", string status = "paid") => new() {
        Id = id, Type = "checkout.session.completed",
        Data = new EventData { Object = new Session {
            Id = "cs_example", Mode = "payment", PaymentStatus = status,
            AmountTotal = 1999, Currency = "eur",
        } },
    };

    [Fact]
    public void RejectsLiveKeysAndUntrustedRedirects()
    {
        var config = Settings();
        config.SecretKey = "sk_live_fixture";
        Assert.Throws<InvalidOperationException>(config.Validate);
        config = Settings();
        config.AppUrl = "http://example.com";
        Assert.Throws<InvalidOperationException>(config.Validate);
        config = Settings();
        config.Validate();
    }

    [Fact]
    public void CheckoutUsesServerControlledPriceAndRedirect()
    {
        var options = CheckoutGateway.BuildOptions(Settings());
        Assert.Equal("price_fixture", options.LineItems.Single().Price);
        Assert.Equal(1L, options.LineItems.Single().Quantity);
        Assert.Equal("payment", options.Mode);
        Assert.Equal("http://localhost:3000/success", options.SuccessUrl);
    }

    [Fact]
    public void LedgerIgnoresUnpaidAndDeduplicatesAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = Path.Combine(directory, "payments.db");
            var ledger = new ReceiptLedger(path);
            Assert.False(ledger.Record(Paid(status: "unpaid")));
            Assert.Equal(0, ledger.CountReceipts());
            Assert.True(ledger.Record(Paid()));
            Assert.False(ledger.Record(Paid()));
            var delayed = Paid("evt_delayed");
            delayed.Type = "checkout.session.async_payment_succeeded";
            Assert.True(ledger.Record(delayed));
            Assert.Equal(1, new ReceiptLedger(path).CountReceipts());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HttpCheckoutValidatesIdempotencyKey()
    {
        using var factory = new TestApp();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/checkout", null)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/checkout");
        request.Headers.Add("Idempotency-Key", "purchase_1234567890");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("checkout.stripe.com", await response.Content.ReadAsStringAsync());
        Assert.Equal("purchase_1234567890", factory.Gateway.LastKey);
    }

    [Fact]
    public async Task WebhookVerifiesSignatureAndRedirectCannotConfirmPayment()
    {
        using var factory = new TestApp();
        using var client = factory.CreateClient();
        var ledger = factory.Services.GetRequiredService<ReceiptLedger>();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/success")).StatusCode);
        Assert.Equal(0, ledger.CountReceipts());
        var payload = JsonSerializer.Serialize(new {
            id = "evt_http", @object = "event", api_version = StripeConfiguration.ApiVersion,
            type = "checkout.session.completed", livemode = false,
            data = new { @object = new {
                id = "cs_http", @object = "checkout.session", mode = "payment",
                payment_status = "paid", amount_total = 1999, currency = "eur",
            } },
        });
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/webhook", new StringContent(payload))).StatusCode);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec_fixture"),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        var signature = $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
        async Task<HttpStatusCode> Send(string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("Stripe-Signature", signature);
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Send(payload + " "));
        Assert.Equal(HttpStatusCode.OK, await Send(payload));
        Assert.Equal(HttpStatusCode.OK, await Send(payload));
        Assert.Equal(1, ledger.CountReceipts());
    }

    private static PaymentSettings Settings() => new() {
        SecretKey = "sk_test_fixture", WebhookSecret = "whsec_fixture",
        PriceId = "price_fixture", AppUrl = "http://localhost:3000",
    };
}

internal sealed class StubGateway : ICheckoutGateway
{
    public string? LastKey { get; private set; }
    public Task<string> CreateAsync(string key, CancellationToken ct)
    {
        LastKey = key;
        return Task.FromResult("https://checkout.stripe.com/test-fixture");
    }
}

internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public StubGateway Gateway { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Stripe:SecretKey", "sk_test_fixture");
        builder.UseSetting("Stripe:WebhookSecret", "whsec_fixture");
        builder.UseSetting("Stripe:PriceId", "price_fixture");
        builder.UseSetting("Stripe:AppUrl", "http://localhost:3000");
        builder.UseSetting("DatabasePath", Path.Combine(directory, "payments.db"));
        builder.ConfigureServices(services => {
            services.RemoveAll<ICheckoutGateway>();
            services.AddSingleton<ICheckoutGateway>(Gateway);
        });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
