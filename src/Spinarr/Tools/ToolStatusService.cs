using Microsoft.Extensions.Options;
using Spinarr.Options;

namespace Spinarr.Tools;

public sealed record ToolStatus
{
    public required ToolDefinition Tool { get; init; }
    public string? Installed { get; init; }
    public bool Missing { get; init; }
    public string? Latest { get; init; }
    public string? UpstreamError { get; init; }

    public string? Pinned => Tool.Pinned;

    /// <summary>
    /// What the image holds: the pin, which is exactly what the Dockerfile installed (cd-paranoia only
    /// prints part of its version, whipper a made-up one), else the version the tool reports.
    /// </summary>
    public string? Current => Pinned ?? Installed;

    public bool UpdateAvailable => ToolCatalog.IsNewer(Tool, Latest, Current);
}

/// <summary>
/// Asks every tool for its version when the app starts and looks for newer versions
/// upstream once a day. Nothing is installed: an update is a new image.
/// </summary>
public sealed class ToolStatusService(IOptions<SpinarrOptions> options, ILogger<ToolStatusService> logger) : BackgroundService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly SpinarrOptions _options = options.Value;
    private readonly SemaphoreSlim _checkNow = new(0);

    public IReadOnlyList<ToolStatus> Statuses { get; private set; } = [];
    public DateTime? UpstreamCheckedAt { get; private set; }
    public bool Checking { get; private set; }

    public event Action? Changed;

    public int UpdateCount => Statuses.Count(s => s.UpdateAvailable);

    public void CheckNow() => _checkNow.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(_options.Updates.Enabled, stoppingToken);
            try
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.Updates.IntervalHours));
                await _checkNow.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(bool upstream, CancellationToken cancellationToken)
    {
        Checking = true;
        Changed?.Invoke();
        try
        {
            var statuses = new List<ToolStatus>();
            foreach (var tool in ToolCatalog.All)
            {
                statuses.Add(await CheckAsync(tool, upstream, cancellationToken));
            }

            Statuses = statuses;
            if (upstream)
            {
                UpstreamCheckedAt = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Tool check failed");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    private async Task<ToolStatus> CheckAsync(ToolDefinition tool, bool upstream, CancellationToken cancellationToken)
    {
        string? installed;
        bool missing;
        if (tool.Command == null)
        {
            missing = tool.VersionFile == null || !File.Exists(tool.VersionFile);
            installed = missing ? null : ToolCatalog.InstalledVersion(tool, await File.ReadAllTextAsync(tool.VersionFile!, cancellationToken));
        }
        else
        {
            var spec = new ProcessSpec(tool.Command(_options.Tools), tool.VersionArguments) { Environment = _options.WhipperEnvironment };
            var result = await ProcessRunner.RunAsync(spec, TimeSpan.FromSeconds(30), null, cancellationToken);
            missing = result.NotStarted;
            installed = missing ? null : ToolCatalog.InstalledVersion(tool, result.Output + "\n" + result.Error);
        }

        string? latest = null, error = null;
        var previous = Statuses.FirstOrDefault(s => s.Tool.Id == tool.Id);
        if (upstream && tool.Upstream != UpstreamKind.None)
        {
            (latest, error) = await LatestAsync(tool, cancellationToken);
        }
        else if (previous != null)
        {
            (latest, error) = (previous.Latest, previous.UpstreamError);
        }

        return new ToolStatus { Tool = tool, Installed = installed, Missing = missing, Latest = latest, UpstreamError = error };
    }

    private static async Task<(string? Latest, string? Error)> LatestAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        try
        {
            if (tool.Upstream == UpstreamKind.GitHubCommit)
            {
                var json = await Http.GetStringAsync(ToolCatalog.CommitUrl(tool.UpstreamSource!), cancellationToken);
                var sha = ToolCatalog.CommitSha(json);
                return sha == null ? (null, "no commit in GitHub's answer") : (sha, null);
            }

            var page = await Http.GetStringAsync(tool.UpstreamSource!, cancellationToken);
            var highest = ToolCatalog.HighestOnPage(tool.UpstreamPattern!, page);
            return highest == null ? (null, $"no version found on {tool.UpstreamSource}") : (highest, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, e.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Spinarr/{AppInfo.Version} (+https://github.com/yannb2/spinarr)");
        return client;
    }
}
