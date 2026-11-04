using System.Globalization;
using Spinarr.Components;
using Spinarr.Drives;
using Spinarr.Options;
using Spinarr.Tools;

// Numbers and dates the same way everywhere.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SpinarrOptions>()
    .Bind(builder.Configuration.GetSection(SpinarrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

builder.Services.AddScoped<Spinarr.Toasts>();
builder.Services.AddSingleton<DriveScanner>();
builder.Services.AddSingleton<DriveTasks>();
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
app.MapGet("/api/stats", async (DriveScanner scanner, DriveTasks tasks, ToolStatusService tools) =>
{
    var drives = await scanner.ScanAsync();
    return Results.Ok(new
    {
        drives = drives.Count,
        discs = drives.Count(d => CdromDevice.State(d.Device) is DiscState.AudioCd or DiscState.MixedCd),
        driveTasks = tasks.AnyRunning ? 1 : 0,
        toolUpdates = tools.UpdateCount
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
