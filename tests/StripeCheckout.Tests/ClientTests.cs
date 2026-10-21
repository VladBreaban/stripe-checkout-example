using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StripeCheckout.Client;
using Xunit;

namespace StripeCheckout.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task ClientCallsApiAndRedirectsToHostedCheckout()
    {
        using var factory = new ClientApp();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetStringAsync("/");
        Assert.Contains("text/html", (await client.GetAsync("/")).Content.Headers.ContentType!.ToString());
        var form = Form(page);
        var response = await client.PostAsync("/", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://checkout.stripe.com/test-session", response.Headers.Location!.AbsoluteUri);
        Assert.Equal("/checkout", factory.Handler.LastPath);
        Assert.Equal(form["CheckoutKey"], factory.Handler.LastKey);
    }

    [Fact]
    public async Task ApiFailureShowsErrorAndKeepsRetryKey()
    {
        using var factory = new ClientApp();
        factory.Handler.Status = HttpStatusCode.ServiceUnavailable;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = Form(await client.GetStringAsync("/"));
        var failed = await client.PostAsync("/", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var html = await failed.Content.ReadAsStringAsync();
        Assert.Contains("couldn", html);
        var retry = Form(html);
        Assert.Equal(form["CheckoutKey"], retry["CheckoutKey"]);
        factory.Handler.Status = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.Redirect,
            (await client.PostAsync("/", new FormUrlEncodedContent(retry))).StatusCode);
        Assert.Equal(form["CheckoutKey"], factory.Handler.LastKey);
    }

    [Fact]
    public async Task FormRequiresAntiforgeryToken()
    {
        using var factory = new ClientApp();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/", new FormUrlEncodedContent(
            new Dictionary<string,string> { ["CheckoutKey"] = Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Handler.LastKey);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/success")]
    [InlineData("/cancel")]
    [InlineData("/css/site.css")]
    public async Task ClientPagesAndStylesLoad(string path)
    {
        using var factory = new ClientApp();
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Handler.LastKey); // Return pages must never create a purchase.
    }

    [Theory]
    [InlineData("https://example.com/not-stripe")]
    [InlineData("http://checkout.stripe.com/insecure")]
    [InlineData("https://checkout.stripe.com.evil.example/")]
    public async Task RejectsUnexpectedRedirectDestination(string url)
    {
        using var factory = new ClientApp();
        factory.Handler.Url = url;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = Form(await client.GetStringAsync("/"));
        var response = await client.PostAsync("/", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    private static Dictionary<string, string> Form(string html)
    {
        string Value(string name)
        {
            var input = Regex.Matches(html, "<input[^>]*>").Select(m => m.Value)
                .Single(x => x.Contains($"name=\"{name}\""));
            return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]*)\"").Groups[1].Value);
        }
        return new() {
            ["CheckoutKey"] = Value("CheckoutKey"),
            ["__RequestVerificationToken"] = Value("__RequestVerificationToken"),
        };
    }
}

internal sealed class CheckoutStubHandler : HttpMessageHandler
{
    public string? LastPath { get; private set; }
    public string? LastKey { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Url { get; set; } = "https://checkout.stripe.com/test-session";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastPath = request.RequestUri!.AbsolutePath;
        LastKey = request.Headers.GetValues("Idempotency-Key").Single();
        return Task.FromResult(new HttpResponseMessage(Status) {
            Content = System.Net.Http.Json.JsonContent.Create(new { url = Url }),
        });
    }
}

internal sealed class ClientApp : WebApplicationFactory<ClientEntryPoint>
{
    public CheckoutStubHandler Handler { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services => services.AddHttpClient<CheckoutApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => Handler));
    }
}
