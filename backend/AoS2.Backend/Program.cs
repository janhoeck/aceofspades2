using AoS2.Backend;
using AoS2.Contracts;
using AoS2.JoinTickets;

switch (args)
{
    // Neuen Signaturschlüssel für Join-Tickets ausgeben (einmalig beim Einrichten, siehe docs/backend.md).
    case ["generate-signing-key"]:
    {
        using var key = JoinTicketSigningKey.Generate();
        Console.WriteLine(key.ExportPrivateKey());
        return 0;
    }

    // Für den Docker-HEALTHCHECK: Im Container gibt es weder curl noch wget.
    case ["healthcheck"]:
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(',', ';')[0] ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}{ApiRoutes.Health}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.AddBackend();

var app = builder.Build();
await app.UseBackendAsync();
await app.RunAsync();
return 0;

/// <summary>Für die Tests (WebApplicationFactory).</summary>
public partial class Program;
