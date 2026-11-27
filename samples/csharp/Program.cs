// RelayStack C# samples (Phase 0). Two commands:
//
//   Upload a CSV and wait for processing:
//     RELAYSTACK_API_KEY=rs_dev_... dotnet run --project samples/csharp -- upload <inbox-id> "$PWD/samples/data/timesheets_with_errors.csv"
//     (dotnet run uses the project directory as the working directory, so pass an absolute path.)
//
//   Run a minimal webhook receiver that verifies signatures and de-duplicates by webhook-id:
//     dotnet run --project samples/csharp -- receive whsec_... 5055
//
// Production receivers should persist processed webhook-ids (delivery is at-least-once) and respond 2xx quickly.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var command = args.FirstOrDefault();
return command switch
{
    "upload" when args.Length == 3 => await Upload(Guid.Parse(args[1]), args[2]),
    "receive" when args.Length >= 2 => await Receive(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 5055),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: upload <inbox-id> <file.csv> | receive <whsec_secret> [port]");
    return 2;
}

static async Task<int> Upload(Guid inboxId, string path)
{
    var apiKey = Environment.GetEnvironmentVariable("RELAYSTACK_API_KEY") ?? throw new InvalidOperationException("Set RELAYSTACK_API_KEY.");
    using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("RELAYSTACK_API_URL") ?? "http://localhost:8080") };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    await using var file = File.OpenRead(path);
    using var content = new StreamContent(file);
    content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/inboxes/{inboxId}/files?filename={Uri.EscapeDataString(Path.GetFileName(path))}")
    {
        Content = content,
    };
    // Reusing the same key on retry returns the original upload instead of creating a duplicate.
    request.Headers.Add("Idempotency-Key", $"{Path.GetFileName(path)}-{File.GetLastWriteTimeUtc(path):yyyyMMddHHmmss}");

    using var response = await http.SendAsync(request);
    var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>();
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Upload failed ({(int)response.StatusCode}): {uploaded}");
        return 1;
    }

    var fileId = uploaded.GetProperty("id").GetString();
    Console.WriteLine($"Uploaded {fileId} ({uploaded.GetProperty("size_bytes").GetInt64()} bytes)");

    for (var i = 0; i < 60; i++)
    {
        var status = await http.GetFromJsonAsync<JsonElement>($"/v1/files/{fileId}");
        var state = status.GetProperty("status").GetString();
        if (state is "completed" or "failed")
        {
            Console.WriteLine($"Status {state}: {status.GetProperty("valid_rows")} valid, {status.GetProperty("invalid_rows")} invalid");
            var errors = await http.GetFromJsonAsync<JsonElement>($"/v1/files/{fileId}/validation-errors");
            foreach (var e in errors.GetProperty("data").EnumerateArray())
            {
                Console.WriteLine($"  row {e.GetProperty("row_number")}: {e.GetProperty("code").GetString()} {e.GetProperty("message").GetString()}");
            }

            return state == "completed" ? 0 : 1;
        }

        await Task.Delay(1000);
    }

    Console.Error.WriteLine("Timed out waiting for processing.");
    return 1;
}

static async Task<int> Receive(string secret, int port)
{
    var app = WebApplication.Create();
    var processed = new ConcurrentDictionary<string, bool>();
    app.MapPost("/webhooks", async (HttpRequest req) =>
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var id = req.Headers["webhook-id"].ToString();
        if (!StandardWebhooks.Verify(secret, id, req.Headers["webhook-timestamp"].ToString(), req.Headers["webhook-signature"].ToString(), body))
        {
            return Results.Unauthorized();
        }

        if (!processed.TryAdd(id, true))
        {
            Console.WriteLine($"duplicate {id} ignored");
            return Results.NoContent();
        }

        using var doc = JsonDocument.Parse(body);
        Console.WriteLine($"{doc.RootElement.GetProperty("type").GetString()} {id}");
        return Results.NoContent();
    });
    await app.RunAsync($"http://localhost:{port}");
    return 0;
}

internal static class StandardWebhooks
{
    /// <summary>Verifies a Standard Webhooks signature and rejects timestamps more than five minutes away.</summary>
    public static bool Verify(string secret, string id, string timestamp, string signatureHeader, string body)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
        {
            return false;
        }

        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));
        return signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }
}
