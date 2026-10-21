using System.Net.Http.Json;

namespace StripeCheckout.Client;

// C# HTTP consumer of the existing API. No Stripe credentials live in the client.
public sealed class CheckoutApiClient(HttpClient http)
{
    public async Task<Uri> CreateAsync(string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/checkout");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CheckoutResponse>(cancellationToken);
        if (!Uri.TryCreate(result?.Url, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals("checkout.stripe.com", StringComparison.OrdinalIgnoreCase) ||
            !url.IsDefaultPort || url.UserInfo.Length != 0)
            throw new InvalidDataException("The API returned an invalid checkout destination.");
        return url;
    }

    private sealed record CheckoutResponse(string? Url);
}
