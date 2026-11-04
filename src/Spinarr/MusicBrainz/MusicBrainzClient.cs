using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Spinarr.Options;

namespace Spinarr.MusicBrainz;

public sealed record MbTrack(int Position, string Number, string Title, string Artist, IReadOnlyList<string> ArtistIds,
    int? LengthMs, string TrackId, string RecordingId, IReadOnlyList<string> Isrcs);

public sealed record MbMedium(int Position, string Format, string Title, IReadOnlyList<string> DiscIds, IReadOnlyList<MbTrack> Tracks);

public sealed record MbLabel(string Name, string CatalogNumber);

/// <summary>One MusicBrainz release, with what the disc page shows and the tags need.</summary>
public sealed record MbRelease(
    string Id,
    string Title,
    string Artist,
    IReadOnlyList<string> ArtistIds,
    string Date,
    string Country,
    string Barcode,
    string Status,
    string Disambiguation,
    IReadOnlyList<MbLabel> Labels,
    string ReleaseGroupId,
    string PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string OriginalDate,
    IReadOnlyList<MbMedium> Media)
{
    public string Year => Date.Length >= 4 ? Date[..4] : "";

    /// <summary>The disc of this release that has the disc ID (a box set has several).</summary>
    public MbMedium? MediumFor(string discId, int audioTracks) =>
        Media.FirstOrDefault(m => m.DiscIds.Contains(discId))
        ?? Media.FirstOrDefault(m => m.Tracks.Count == audioTracks && m.Format.Contains("CD", StringComparison.OrdinalIgnoreCase))
        ?? Media.FirstOrDefault(m => m.Tracks.Count == audioTracks);

    public string CoverUrl(int size = 250) => $"https://coverartarchive.org/release/{Id}/front-{size}";
}

/// <summary>What MusicBrainz said about a disc: exact matches by disc ID, or near ones by its TOC.</summary>
public sealed record MbLookup(IReadOnlyList<MbRelease> Releases, bool Fuzzy, string? Problem);

/// <summary>
/// Asks MusicBrainz about a disc: by its disc ID first, then by its TOC (a release with the same
/// track lengths but no disc ID yet). At most one request a second with a named User-Agent, as
/// MusicBrainz asks; answers are kept in the cache folder, so a disc asked twice costs nothing.
/// </summary>
public sealed class MusicBrainzClient(IHttpClientFactory httpFactory, IOptions<SpinarrOptions> options, ILogger<MusicBrainzClient> logger)
{
    private const string Includes = "artist-credits+labels+recordings+release-groups+isrcs+discids";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    private readonly SpinarrOptions _options = options.Value;

    public async Task<MbLookup> LookupAsync(string discId, string toc, CancellationToken cancellationToken)
    {
        var (json, problem) = await GetAsync($"discid/{Uri.EscapeDataString(discId)}?inc={Includes}&cdstubs=no&fmt=json",
            $"discid-{discId}", cancellationToken);
        if (json != null)
        {
            var releases = ParseReleases(json);
            if (releases.Count > 0)
            {
                return new MbLookup(releases, false, null);
            }
        }

        if (problem != null)
        {
            return new MbLookup([], false, problem);
        }

        var tocParam = Uri.EscapeDataString(toc).Replace("%20", "+");
        (json, problem) = await GetAsync($"discid/-?toc={tocParam}&inc={Includes}&cdstubs=no&fmt=json",
            $"toc-{toc.Replace(' ', '-')}", cancellationToken);
        return new MbLookup(json == null ? [] : ParseReleases(json), true, problem);
    }

    /// <summary>One release by its ID (the release the person typed or pasted).</summary>
    public async Task<(MbRelease? Release, string? Problem)> ReleaseAsync(string releaseId, CancellationToken cancellationToken)
    {
        var (json, problem) = await GetAsync($"release/{Uri.EscapeDataString(releaseId)}?inc={Includes}+media&fmt=json",
            $"release-{releaseId}", cancellationToken);
        if (json == null)
        {
            return (null, problem ?? "MusicBrainz has no release with this ID.");
        }

        using var document = JsonDocument.Parse(json);
        return (ParseRelease(document.RootElement), null);
    }

    /// <summary>The JSON answer, or (null, null) for "not found", or (null, why) when MusicBrainz couldn't be asked.</summary>
    private async Task<(string? Json, string? Problem)> GetAsync(string path, string cacheKey, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(_options.CacheFolder, "musicbrainz", Safe(cacheKey) + ".json");
        if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddDays(-7))
        {
            return (await File.ReadAllTextAsync(cacheFile, cancellationToken), null);
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _last.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _last = DateTime.UtcNow;
            using var response = await httpFactory.CreateClient("musicbrainz").GetAsync("https://musicbrainz.org/ws/2/" + path, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return (null, null);
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllTextAsync(cacheFile, json, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("MusicBrainz answer not cached: {Message}", e.Message);
            }

            return (json, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("MusicBrainz could not be reached: {Message}", e.Message);
            return (null, $"MusicBrainz could not be reached: {e.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Safe(string key) => string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_'));

    /// <summary>The releases of a discid answer ({"releases": [...]}), or of a single release.</summary>
    public static IReadOnlyList<MbRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
        {
            return releases.EnumerateArray().Select(ParseRelease).ToList();
        }

        return root.TryGetProperty("media", out _) ? [ParseRelease(root)] : [];
    }

    private static MbRelease ParseRelease(JsonElement r)
    {
        var (artist, artistIds) = Credit(r);
        var group = r.TryGetProperty("release-group", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
        var labels = Array(r, "label-info")
            .Select(l => new MbLabel(
                l.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object ? Text(label, "name") : "",
                Text(l, "catalog-number")))
            .ToList();
        var media = Array(r, "media").Select(m => new MbMedium(
            Int(m, "position") ?? 1,
            Text(m, "format"),
            Text(m, "title"),
            Array(m, "discs").Select(d => Text(d, "id")).ToList(),
            Array(m, "tracks").Select(t =>
            {
                var recording = t.TryGetProperty("recording", out var rec) && rec.ValueKind == JsonValueKind.Object ? rec : default;
                var (trackArtist, trackArtistIds) = Credit(t);
                if (trackArtist.Length == 0 && recording.ValueKind == JsonValueKind.Object)
                {
                    (trackArtist, trackArtistIds) = Credit(recording);
                }

                return new MbTrack(
                    Int(t, "position") ?? 0,
                    Text(t, "number"),
                    Text(t, "title") is { Length: > 0 } title ? title : Text(recording, "title"),
                    trackArtist.Length > 0 ? trackArtist : artist,
                    trackArtistIds.Count > 0 ? trackArtistIds : artistIds,
                    Int(t, "length") ?? Int(recording, "length"),
                    Text(t, "id"),
                    Text(recording, "id"),
                    Array(recording, "isrcs").Select(i => i.GetString() ?? "").Where(i => i.Length > 0).ToList());
            }).ToList())).ToList();

        return new MbRelease(
            Text(r, "id"), Text(r, "title"), artist, artistIds, Text(r, "date"), Text(r, "country"), Text(r, "barcode"),
            Text(r, "status"), Text(r, "disambiguation"), labels,
            Text(group, "id"), Text(group, "primary-type"),
            Array(group, "secondary-types").Select(s => s.GetString() ?? "").ToList(),
            Text(group, "first-release-date"), media);
    }

    /// <summary>"Artist A feat. Artist B", as MusicBrainz joins an artist credit, and the artist IDs.</summary>
    private static (string Name, IReadOnlyList<string> Ids) Credit(JsonElement element)
    {
        var parts = Array(element, "artist-credit").ToList();
        var name = string.Concat(parts.Select(p => Text(p, "name") + Text(p, "joinphrase")));
        var ids = parts.Select(p => p.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "id") : "")
            .Where(id => id.Length > 0).ToList();
        return (name, ids);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
