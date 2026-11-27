using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayStack.Platform.Security;

namespace RelayStack.DevReceiver;

/// <summary>
/// A deliberately restricted, development-only customer simulator:
/// <list type="bullet">
/// <item>POST /webhooks verifies Standard Webhooks signatures and records what it received (in memory, capped).</item>
/// <item>/control lets tests and demos switch between success, failures and slow responses.</item>
/// <item>GET /synthetic/employees is a fake REST API for the polling experiment.</item>
/// </list>
/// It refuses to start outside the Development environment, has no outbound calls and no persistence.
/// RelayStack can only reach it through the explicit, development-only SSRF exception (exact host:port).
/// </summary>
public static class ReceiverApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException("The development receiver only runs in the Development environment.");
        }

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Services.AddSingleton<ReceiverState>();
        var app = builder.Build();
        var state = app.Services.GetRequiredService<ReceiverState>();
        foreach (var secret in builder.Configuration.GetSection("Receiver:Secrets").Get<string[]>() ?? [])
        {
            state.Secrets.Add(secret);
        }

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/webhooks", async (HttpRequest request, ReceiverState s) =>
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var id = request.Headers[WebhookSigner.IdHeader].ToString();
            var ts = request.Headers[WebhookSigner.TimestampHeader].ToString();
            var sig = request.Headers[WebhookSigner.SignatureHeader].ToString();
            var verified = s.Secrets.Any(secret => WebhookSigner.Verify(secret, id, ts, sig, body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
            string? type = null;
            int records = 0;
            try
            {
                var json = JsonNode.Parse(body);
                type = json?["type"]?.GetValue<string>();
                records = json?["data"]?["record_count"]?.GetValue<int>() ?? 0;
            }
            catch (JsonException)
            {
            }

            var status = s.NextStatus();
            if (s.DelayMs > 0)
            {
                await Task.Delay(s.DelayMs);
            }

            var duplicate = !s.SeenIds.TryAdd(id, 0);
            s.Record(new ReceivedWebhook(id, type, records, body.Length, verified, duplicate, status, DateTimeOffset.UtcNow, s.CaptureBodies ? body : null));
            if (!verified && s.RequireSignature)
            {
                return Results.StatusCode(401);
            }

            return Results.StatusCode(status);
        });

        app.MapGet("/received", (ReceiverState s) => Results.Ok(s.Received.ToArray()));
        app.MapPost("/control/reset", (ReceiverState s) =>
        {
            s.Reset();
            return Results.NoContent();
        });
        app.MapPost("/control/secrets", (SecretRequest req, ReceiverState s) =>
        {
            s.Secrets.Add(req.Secret);
            return Results.NoContent();
        });
        app.MapPost("/control/mode", (ModeRequest req, ReceiverState s) =>
        {
            s.SetMode(req.FailNext ?? 0, req.FailureStatus ?? 503, req.DelayMs ?? 0, req.PermanentStatus);
            return Results.NoContent();
        });

        // Synthetic REST API for the polling experiment. Records change when /synthetic/employees/mutate is called.
        app.MapGet("/synthetic/employees", (ReceiverState s) => Results.Json(s.SyntheticEmployees()));
        app.MapPost("/synthetic/employees/mutate", (ReceiverState s) =>
        {
            s.MutateSynthetic();
            return Results.NoContent();
        });
        return app;
    }
}

public sealed record SecretRequest(string Secret);

public sealed record ModeRequest(int? FailNext, int? FailureStatus, int? DelayMs, int? PermanentStatus);

public sealed record ReceivedWebhook(string WebhookId, string? Type, int RecordCount, int BodyBytes, bool SignatureValid, bool Duplicate,
    int RespondedWith, DateTimeOffset ReceivedAt, string? Body);

public sealed class ReceiverState
{
    private readonly object _gate = new();
    private int _failNext;
    private int _failureStatus = 503;
    private int? _permanentStatus;
    private int _mutations;

    public ConcurrentBag<string> Secrets { get; } = [];
    public ConcurrentDictionary<string, byte> SeenIds { get; } = new();
    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();
    public int DelayMs { get; private set; }
    public bool RequireSignature { get; set; } = true;
    public bool CaptureBodies { get; set; } = true;

    public void Record(ReceivedWebhook w)
    {
        Received.Enqueue(w);
        while (Received.Count > 1000 && Received.TryDequeue(out _))
        {
        }
    }

    public void SetMode(int failNext, int failureStatus, int delayMs, int? permanentStatus)
    {
        lock (_gate)
        {
            _failNext = failNext;
            _failureStatus = failureStatus;
            _permanentStatus = permanentStatus;
            DelayMs = Math.Clamp(delayMs, 0, 60_000);
        }
    }

    public int NextStatus()
    {
        lock (_gate)
        {
            if (_permanentStatus is { } p)
            {
                return p;
            }

            if (_failNext > 0)
            {
                _failNext--;
                return _failureStatus;
            }

            return 204;
        }
    }

    public void Reset()
    {
        SetMode(0, 503, 0, null);
        Received.Clear();
        SeenIds.Clear();
    }

    public void MutateSynthetic() => Interlocked.Increment(ref _mutations);

    public object[] SyntheticEmployees()
    {
        var m = Volatile.Read(ref _mutations);
        var list = new List<object>
        {
            new { id = "E-1001", name = "Avery Quinn", department = "Engineering", title = m > 0 ? "Staff Engineer" : "Senior Engineer" },
            new { id = "E-1002", name = "Jordan Blake", department = "Finance", title = "Controller" },
            new { id = "E-1003", name = "Sam Rivera", department = "Operations", title = "Logistics Lead" },
        };
        if (m > 0)
        {
            list.Add(new { id = "E-1004", name = "Riley Chen", department = "Support", title = "Support Engineer" });
        }

        return list.ToArray();
    }
}
