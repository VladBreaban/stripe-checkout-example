using StripeCheckout.Client;

public class ClientEntryPoint
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var address = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:3000";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            (uri.Scheme == "http" && !uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new InvalidOperationException("ApiBaseUrl must be an HTTPS origin, or HTTP on localhost.");

        builder.Services.AddRazorPages();
        builder.Services.AddHttpClient<CheckoutApiClient>(client =>
        {
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        var app = builder.Build();
        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/error");
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
        app.UseStaticFiles();
        app.MapGet("/error", () => Results.Problem("The client could not process the request."));
        app.MapRazorPages();
        app.Run();
    }
}
