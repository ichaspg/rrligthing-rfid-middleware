using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RfidMiddleware.Core;
using RfidMiddleware.Host;
using RfidMiddleware.Persistence;
using RfidMiddleware.Reader;
using RfidMiddleware.Reader.Vendor;
using RfidMiddleware.Server;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var configPath = GetOption(args, "--config") ?? "appsettings.json";
if (command != "run")
{
    if (command == "reader" && args.Length > 1 && string.Equals(args[1], "network", StringComparison.OrdinalIgnoreCase))
        return RunReaderNetworkCommand(args, configPath);

    await RunClientCommandAsync(command, args, configPath);
    return 0;
}

var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile(configPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables("RFID_");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");

var options = builder.Configuration.Get<MiddlewareOptions>() ?? new MiddlewareOptions();
options.Server.MiddlewareId = options.Id;
ValidateOptions(options);
var socketPath = Path.GetFullPath(options.ControlSocket);
Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
if (File.Exists(socketPath)) File.Delete(socketPath);
builder.WebHost.ConfigureKestrel(server => server.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http1));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ValidationEngine>();
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<ManifestSyncTrigger>();
builder.Services.AddSingleton(sp => new PostgresMiddlewareStore(options.DatabaseConnectionString));
builder.Services.AddSingleton<IManifestRepository>(sp => sp.GetRequiredService<PostgresMiddlewareStore>());
builder.Services.AddSingleton<IEventRepository>(sp => sp.GetRequiredService<PostgresMiddlewareStore>());
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { BaseAddress = new Uri(options.Server.BaseUrl), Timeout = TimeSpan.FromSeconds(options.Server.TimeoutSeconds) };
    return http;
});
builder.Services.AddSingleton<IManifestClient>(sp => new HttpManifestClient(sp.GetRequiredService<HttpClient>(), options.Server, sp.GetRequiredService<IClock>()));

if (string.Equals(options.ReaderMode, "ManagedFake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IReaderAdapter>(sp => new ManagedFakeReader(
        sp.GetRequiredService<ILogger<ManagedFakeReader>>(), sp.GetRequiredService<IClock>(), options.FakeReader));
    builder.Services.AddSingleton<IAlarmController>(sp => new ManagedAlarmController(
        sp.GetRequiredService<ILogger<ManagedAlarmController>>(),
        sp.GetRequiredService<IClock>(),
        options.VendorReader.AlarmPulseMilliseconds));
}
else
{
    builder.Services.AddSingleton(sp => new VendorReaderAdapter(
        sp.GetRequiredService<ILogger<VendorReaderAdapter>>(), sp.GetRequiredService<IClock>(), options.VendorReader,
        options.ReaderLockFile));
    builder.Services.AddSingleton<IReaderAdapter>(sp => sp.GetRequiredService<VendorReaderAdapter>());
    builder.Services.AddSingleton<IAlarmController>(sp => new VendorAlarmController(
        sp.GetRequiredService<VendorReaderAdapter>(), sp.GetRequiredService<IClock>(), options.VendorReader));
}

builder.Services.AddHostedService<ManifestSyncWorker>();
builder.Services.AddHostedService<ReaderPipelineWorker>();
var app = builder.Build();
var startupStore = app.Services.GetRequiredService<PostgresMiddlewareStore>();
await startupStore.InitializeAsync();
var startupAuthorization = await startupStore.GetAuthorizationStateAsync();
var startupManifest = await startupStore.GetActiveAsync();
var startupState = app.Services.GetRequiredService<RuntimeState>();
startupState.AuthorizationState = startupAuthorization.State;
startupState.LatestManifestVersion = startupAuthorization.Version;
if (startupManifest is null && startupAuthorization.State == AuthorizationState.Uninitialized)
{
    try
    {
        var initial = await app.Services.GetRequiredService<IManifestClient>().GetCurrentAsync(null);
        if (initial.Directive == ManifestFetchDirective.Activate && initial.Manifest is not null)
        {
            await startupStore.StageAndActivateAsync(initial.Manifest);
            startupManifest = await startupStore.GetActiveAsync();
        }
        else if (initial.Directive == ManifestFetchDirective.Deactivate && initial.Completion is not null)
        {
            await startupStore.DeactivateAsync(initial.Completion);
            startupAuthorization = await startupStore.GetAuthorizationStateAsync();
            startupState.AuthorizationState = startupAuthorization.State;
            startupState.LatestManifestVersion = startupAuthorization.Version;
        }
    }
    catch (Exception ex)
    {
        startupState.LastManifestError = ex.Message;
    }
}
if (startupManifest is not null)
{
    app.Services.GetRequiredService<ValidationEngine>().Activate(startupManifest);
    startupState.AuthorizationState = AuthorizationState.Active;
    startupState.LatestManifestVersion = startupManifest.Version;
    startupState.ActiveManifestId = startupManifest.ManifestId;
    startupState.ActiveManifestVersion = startupManifest.Version;
    startupState.ManifestExpiresAt = startupManifest.ExpiresAt;
    startupState.LastManifestSync = startupManifest.ActivatedAt;
}

app.MapGet("/status", (RuntimeState state, IReaderAdapter reader, IAlarmController alarm) =>
{
    var now = DateTimeOffset.UtcNow;
    var authorizationCritical = state.AuthorizationState == AuthorizationState.Uninitialized ||
        state.AuthorizationState == AuthorizationState.Active &&
        (state.ActiveManifestVersion is null || state.ManifestExpiresAt is null || state.ManifestExpiresAt <= now);
    var critical = !reader.Health.Connected || !alarm.Health.Healthy || state.ReadsDropped > 0 ||
        state.LastSystemError is not null || state.LastManifestError is not null || authorizationCritical;
    var warning = state.LastManifestSync is null ||
        now - state.LastManifestSync > TimeSpan.FromSeconds(options.ManifestWarningSeconds);
    var alarmLatency = state.AlarmLatency();
    return Results.Ok(new
    {
        status = critical ? "critical" : warning ? "degraded" : "healthy",
        state.StartedAt,
        reader = reader.Health,
        alarm = alarm.Health,
        authorization = new { state.AuthorizationState, state.LatestManifestVersion },
        manifest = new { state.ActiveManifestId, state.ActiveManifestVersion, state.ManifestExpiresAt, state.LastManifestSync, state.LastManifestError },
        system = new { state.LastSystemError },
        reads = new { received = state.ReadsReceived, dropped = state.ReadsDropped },
        alarmLatency = new { samples = alarmLatency.Samples, p99Milliseconds = alarmLatency.P99Milliseconds },
        scanDataPolicy = "LOCAL_ONLY"
    });
});
app.MapGet("/manifest", (RuntimeState state) => Results.Ok(new { state.AuthorizationState, state.LatestManifestVersion, state.ActiveManifestId, state.ActiveManifestVersion, state.ManifestExpiresAt, state.LastManifestSync, state.LastManifestError }));
app.MapPost("/manifest/sync", (ManifestSyncTrigger trigger) => { trigger.Signal(); return Results.Accepted(); });
app.MapGet("/audit", async (IEventRepository repository, CancellationToken token) => Results.Ok(await repository.GetStatsAsync(token)));
app.MapGet("/db/verify", async (IEventRepository repository, CancellationToken token) => Results.Ok(new { result = await repository.VerifyAsync(token) }));
app.MapGet("/reader/test", (IReaderAdapter reader) => Results.Ok(reader.Health));
app.MapGet("/reader/recent", (RuntimeState state) => Results.Ok(state.RecentReads()));
app.MapGet("/healthz", (RuntimeState state, IReaderAdapter reader, IAlarmController alarm) =>
    reader.Health.Connected && alarm.Health.Healthy && state.LastSystemError is null && state.LastManifestError is null && state.ReadsDropped == 0 &&
    (state.AuthorizationState == AuthorizationState.Completed ||
     state.AuthorizationState == AuthorizationState.Active && state.ActiveManifestVersion is not null && state.ManifestExpiresAt > DateTimeOffset.UtcNow)
        ? Results.Ok()
        : Results.StatusCode(503));
app.MapGet("/metrics", (RuntimeState state) =>
{
    var latency = state.AlarmLatency();
    return Results.Text($"rfid_reads_total {state.ReadsReceived}\nrfid_reads_dropped_total {state.ReadsDropped}\nrfid_scan_data_local_only 1\nrfid_system_fault {((state.LastSystemError is null) ? 0 : 1)}\nrfid_manifest_sync_fault {((state.LastManifestError is null) ? 0 : 1)}\nrfid_alarm_latency_p99_milliseconds {latency.P99Milliseconds ?? 0}\n", "text/plain");
});

try
{
    await app.StartAsync();
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await app.WaitForShutdownAsync();
}
finally { if (File.Exists(socketPath)) File.Delete(socketPath); }

return 0;

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static void ValidateOptions(MiddlewareOptions value)
{
    if (string.IsNullOrWhiteSpace(value.Id)) throw new InvalidDataException("Middleware Id is required.");
    if (string.IsNullOrWhiteSpace(value.DatabaseConnectionString)) throw new InvalidDataException("DatabaseConnectionString is required.");
    if (string.IsNullOrWhiteSpace(value.ReaderLockFile)) throw new InvalidDataException("ReaderLockFile is required.");
    if (string.IsNullOrWhiteSpace(value.CommissioningBackupDirectory)) throw new InvalidDataException("CommissioningBackupDirectory is required.");
    if (value.DedupWindowMilliseconds is < 100 or > 60_000) throw new InvalidDataException("DedupWindowMilliseconds is invalid.");
    if (value.VendorReader.AntennaPower is < 1 or > 33)
        throw new InvalidDataException("VendorReader.AntennaPower must be between 1 and 33 dBm when configured.");
    if (!Uri.TryCreate(value.Server.BaseUrl, UriKind.Absolute, out _)) throw new InvalidDataException("Server BaseUrl is invalid.");
}

static async Task RunClientCommandAsync(string command, string[] values, string config)
{
    if (command == "version")
    {
        var assembly = Assembly.GetExecutingAssembly();
        Console.WriteLine(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown");
        return;
    }
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            rfid-middleware run --config <file>
            rfid-middleware status [--json]
            rfid-middleware healthcheck
            rfid-middleware version
            rfid-middleware reader test|watch [--duration <seconds>]
            rfid-middleware reader network show [--json] --config <file>
            rfid-middleware reader network configure --ip <ip> --mask <mask> --gateway <ip> [--port 1969] [--dns <ip>] [--apply] --config <file>
            rfid-middleware reader network verify --address <ip> [--port 1969] --config <file>
            rfid-middleware manifest status|sync
            rfid-middleware audit stats
            rfid-middleware db verify
            rfid-middleware diagnostics bundle --output <file>
            """);
        return;
    }

    var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(config), false).AddEnvironmentVariables("RFID_").Build();
    var middleware = configuration.Get<MiddlewareOptions>() ?? new MiddlewareOptions();
    using var client = new ControlClient(Path.GetFullPath(middleware.ControlSocket));
    var subcommand = values.Skip(1).FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
    string output = command switch
    {
        "status" => await client.GetAsync("/status"),
        "healthcheck" => await client.GetAsync("/healthz"),
        "reader" when subcommand == "test" => await client.GetAsync("/reader/test"),
        "reader" when subcommand == "watch" => await WatchAsync(client, values),
        "manifest" when subcommand == "status" => await client.GetAsync("/manifest"),
        "manifest" when subcommand == "sync" => await client.PostAsync("/manifest/sync"),
        "audit" when subcommand == "stats" => await client.GetAsync("/audit"),
        "db" when subcommand == "verify" => await client.GetAsync("/db/verify"),
        "diagnostics" when subcommand == "bundle" => await DiagnosticsAsync(client, values),
        _ => throw new ArgumentException("Unknown command. Run 'rfid-middleware help'.")
    };
    Console.WriteLine(Pretty(output));
}

static int RunReaderNetworkCommand(string[] values, string config)
{
    try
    {
        var action = values.Length > 2 ? values[2].ToLowerInvariant() : string.Empty;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(config), false)
            .AddEnvironmentVariables("RFID_")
            .Build();
        var middleware = configuration.Get<MiddlewareOptions>() ?? new MiddlewareOptions();
        var commissioner = new ReaderNetworkCommissioner();
        object output = action switch
        {
            "show" => commissioner.ShowSerial(middleware.VendorReader, middleware.ReaderLockFile),
            "configure" => commissioner.ConfigureSerial(
                middleware.VendorReader,
                middleware.ReaderLockFile,
                new ReaderNetworkSettings(
                    RequireOption(values, "--ip"),
                    RequireOption(values, "--mask"),
                    RequireOption(values, "--gateway"),
                    GetIntOption(values, "--port", 1969),
                    GetOption(values, "--dns")),
                values.Contains("--apply", StringComparer.OrdinalIgnoreCase),
                middleware.CommissioningBackupDirectory),
            "verify" => commissioner.VerifyTcp(
                RequireOption(values, "--address"),
                GetIntOption(values, "--port", 1969),
                middleware.VendorReader.TimeoutMilliseconds,
                middleware.ReaderLockFile),
            _ => throw new ArgumentException("Unknown reader network command. Use show, configure, or verify.")
        };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (ReaderDeviceBusyException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    catch (ReaderNetworkVerificationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 4;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static string RequireOption(string[] values, string name) =>
    GetOption(values, name) ?? throw new ArgumentException($"{name} is required.");

static int GetIntOption(string[] values, string name, int defaultValue)
{
    var raw = GetOption(values, name);
    if (raw is null) return defaultValue;
    return int.TryParse(raw, out var parsed)
        ? parsed
        : throw new ArgumentException($"{name} must be an integer.");
}

static async Task<string> WatchAsync(ControlClient client, string[] values)
{
    var raw = GetOption(values, "--duration");
    var seconds = raw is null ? 5 : Math.Clamp(int.Parse(raw), 1, 300);
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    return await client.GetAsync("/reader/recent");
}

static async Task<string> DiagnosticsAsync(ControlClient client, string[] values)
{
    var output = GetOption(values, "--output") ?? "rfid-diagnostics.json";
    var status = await client.GetAsync("/status");
    var database = await client.GetAsync("/db/verify");
    var payload = JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, status = JsonDocument.Parse(status).RootElement, database = JsonDocument.Parse(database).RootElement }, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(output, payload);
    return JsonSerializer.Serialize(new { output = Path.GetFullPath(output) });
}

static string Pretty(string json)
{
    if (string.IsNullOrWhiteSpace(json)) return "OK";
    try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
    catch (JsonException) { return json; }
}

public partial class Program;
