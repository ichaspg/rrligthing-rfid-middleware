using System.Text.Json;
using RfidMiddleware.Core;
using RfidMiddleware.MockServer;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("MockServer")
    ?? throw new InvalidDataException("ConnectionStrings:MockServer is required.");
var database = new PostgresMockDatabase(connectionString);
await database.InitializeAsync();
if (args.Contains("--reset", StringComparer.OrdinalIgnoreCase))
{
    var countIndex = Array.IndexOf(args, "--seed-count");
    var count = countIndex >= 0 && countIndex + 1 < args.Length ? int.Parse(args[countIndex + 1]) : 100;
    await database.ResetAndSeedAsync(count);
}
else
{
    await database.EnsureSeededAsync();
}
builder.Services.AddSingleton(database);
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin")) { await next(); return; }
    var id = context.Request.RouteValues["middlewareId"]?.ToString() ?? string.Empty;
    var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    if (!await database.IsAuthorizedAsync(id, token, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/api/v1/middleware/{middlewareId}/manifest", async (string middlewareId, HttpContext context) =>
{
    var fault = await database.GetFaultAsync(middlewareId, context.RequestAborted);
    if (fault.LatencyMs > 0) await Task.Delay(fault.LatencyMs, context.RequestAborted);
    if (fault.Profile == "timeout") await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
    if (fault.Profile == "500") return Results.StatusCode(500);
    if (fault.Profile == "429") return Results.StatusCode(429);
    if (fault.Profile == "malformed") return Results.Text("{not-json", "application/json");
    var manifest = await database.GetManifestAsync(middlewareId, context.RequestAborted);
    if (manifest is null) return Results.NotFound();
    manifest = ApplyManifestFault(manifest, fault.Profile);
    var etag = $"\"{manifest.ManifestId}-{manifest.Version}\"";
    if (context.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    context.Response.Headers.ETag = etag;
    return Results.Ok(manifest);
});

app.MapPost("/admin/reset", async (int? count, CancellationToken token) =>
{
    await database.ResetAndSeedAsync(Math.Clamp(count ?? 100, 1, 1_000_000), token);
    return Results.Ok();
});
app.MapPut("/admin/middleware/{middlewareId}/fault", async (string middlewareId, FaultProfile profile, CancellationToken token) =>
{
    await database.SetFaultAsync(middlewareId, profile, token);
    return Results.Ok(profile);
});
app.MapPost("/admin/middleware/{middlewareId}/manifest/activate", async (string middlewareId, ManifestActivation request, CancellationToken token) =>
{
    await database.ActivateManifestAsync(middlewareId, request, token);
    return Results.Ok(request);
});
app.MapPost("/api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/revoke", async (
    string middlewareId, string manifestId, ManifestRevocation request, CancellationToken token) =>
{
    try
    {
        var result = await database.RevokeAsync(middlewareId, manifestId, request, token);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.Run();

static ManifestResponse ApplyManifestFault(ManifestResponse manifest, string profile) => profile switch
{
    "empty" => manifest with { ItemCount = 0, Items = [] },
    "expired" => manifest with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
    "wrong-count" => manifest with { ItemCount = manifest.ItemCount + 1 },
    "invalid-checksum" => manifest with { Checksum = new string('0', 64) },
    "duplicate-epc" => manifest with
    {
        ItemCount = manifest.ItemCount + 1,
        Items = manifest.Items.Concat([new ManifestItem("DUPLICATE", manifest.Items[0].Epc)]).ToArray()
    },
    _ => manifest
};

public partial class Program;
