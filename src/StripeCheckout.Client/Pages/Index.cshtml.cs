using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StripeCheckout.Client.Pages;

public sealed class IndexModel(CheckoutApiClient api) : PageModel
{
    [BindProperty]
    [Required, RegularExpression(@"[a-zA-Z0-9_-]{16,100}")]
    public string CheckoutKey { get; set; } = "";
    public string? ErrorMessage { get; private set; }

    public void OnGet() => CheckoutKey = Guid.NewGuid().ToString("N");

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "This checkout attempt is invalid. Refresh the page to start again.";
            Response.StatusCode = 400;
            return Page();
        }
        try
        {
            var url = await api.CreateAsync(CheckoutKey, cancellationToken);
            return Redirect(url.AbsoluteUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   InvalidDataException or JsonException or NotSupportedException)
        {
            // Keep the same key when retrying an uncertain request; don't create a second session.
            ErrorMessage = "We couldn't open checkout. Make sure the API is running and configured, then try again.";
            Response.StatusCode = 502;
            return Page();
        }
    }
}
