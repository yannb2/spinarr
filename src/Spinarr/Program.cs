using System.Globalization;
using Arrkit.Hosting;
using Arrkit.Logging;
using Spinarr;
using Spinarr.AccurateRip;
using Spinarr.Components;
using Spinarr.Drives;
using Spinarr.MusicBrainz;
using Spinarr.Options;
using Spinarr.Rip;
using Spinarr.Tools;

// Numbers and dates the same way everywhere.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

var configFolder = builder.Configuration["Spinarr:ConfigFolder"] ?? "/config";
DataProtectionKeys.PersistKeysIn(builder, configFolder, "Spinarr");

// The Logs page: the app's recent log lines, in memory and in /config/logs (kept across restarts).
var memoryLog = new MemoryLog();
memoryLog.KeepFile(Path.Combine(configFolder, "logs", "spinarr.jsonl"),
    builder.Configuration.GetValue(MemoryLog.KeepLinesKey, MemoryLog.Capacity));
builder.Logging.AddProvider(new MemoryLoggerProvider(memoryLog));
builder.Services.AddSingleton(memoryLog);

builder.Services.AddOptions<SpinarrOptions>()
    .Bind(builder.Configuration.GetSection(SpinarrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

builder.Services.AddScoped<Spinarr.Toasts>();
builder.Services.AddSingleton<DriveScanner>();
builder.Services.AddSingleton<DriveSettingsStore>();
builder.Services.AddSingleton<DiscReader>();
builder.Services.AddSingleton<DriveTasks>();
builder.Services.AddSingleton<RipService>();
builder.Services.AddSingleton<AccurateRipClient>();
builder.Services.AddSingleton<MusicBrainzClient>();

// MusicBrainz asks every client to name itself and give a way to reach its author.
var userAgent = $"Spinarr/{AppInfo.Version} ( https://github.com/yannb2/spinarr )";
builder.Services.AddHttpClient("musicbrainz", client =>
{
    client.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("accuraterip", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ToolStatusService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ToolStatusService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok("ok"));

// Counts for a dashboard (Homepage's custom API widget). Read-only, no key.
app.MapGet("/api/stats", async (DriveScanner scanner, DriveTasks tasks, RipService rips, ToolStatusService tools) =>
{
    var drives = await scanner.ScanAsync();
    return Results.Ok(new
    {
        drives = drives.Count,
        discs = drives.Count(d => CdromDevice.State(d.Device) is DiscState.AudioCd or DiscState.MixedCd),
        driveTasks = tasks.AnyRunning ? 1 : 0,
        rips = rips.Jobs.Count(j => j.State == RipState.Running),
        toolUpdates = tools.UpdateCount
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
