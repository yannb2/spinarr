using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using Spinarr.Disc;

namespace Spinarr.AccurateRip;

/// <summary>One answer AccurateRip holds for a disc: a checksum and how many people got it, per track.</summary>
public sealed record AccurateRipResponse(int TrackCount, string DiscId1, string DiscId2, string FreedbId,
    IReadOnlyList<int> Confidences, IReadOnlyList<uint> Checksums);

/// <summary>How one track compares with AccurateRip.</summary>
public sealed record AccurateRipMatch(
    uint V1,
    uint V2,
    int? V1Confidence,
    int? V2Confidence,
    int MaxConfidence,
    uint? MaxConfidenceChecksum)
{
    public bool Accurate => V1Confidence != null || V2Confidence != null;

    /// <summary>The best match: v2 when both match (newer), with its confidence.</summary>
    public (string Version, int Confidence, uint Checksum)? Best =>
        V2Confidence is { } c2 && (V1Confidence is not { } c1 || c2 >= c1) ? ("v2", c2, V2)
        : V1Confidence is { } v1 ? ("v1", v1, V1)
        : null;

    /// <summary>AccurateRip knows the disc but no one ripped this track this way.</summary>
    public bool InDatabase => MaxConfidence > 0 || MaxConfidenceChecksum != null;
}

/// <summary>Reads AccurateRip's answers (dBAR files) and compares checksums with them.</summary>
public static class AccurateRipDatabase
{
    public const string BaseUrl = "http://www.accuraterip.com/accuraterip/";

    /// <summary>
    /// A dBAR file: one or more answers back to back, each 13 header bytes (track count, the two
    /// AccurateRip IDs, the freedb ID) then 9 bytes per track (confidence, checksum, a frame-450 checksum).
    /// </summary>
    public static IReadOnlyList<AccurateRipResponse> Parse(ReadOnlySpan<byte> data)
    {
        var responses = new List<AccurateRipResponse>();
        while (data.Length >= 13)
        {
            int count = data[0];
            var size = 13 + count * 9;
            if (data.Length < size)
            {
                break;
            }

            var confidences = new int[count];
            var checksums = new uint[count];
            for (var i = 0; i < count; i++)
            {
                var at = 13 + i * 9;
                confidences[i] = data[at];
                checksums[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 1, 4));
            }

            responses.Add(new AccurateRipResponse(count, Hex(data.Slice(1, 4)), Hex(data.Slice(5, 4)), Hex(data.Slice(9, 4)),
                confidences, checksums));
            data = data[size..];
        }

        return responses;
    }

    /// <summary>Compares one track's checksums with every answer.</summary>
    /// <param name="index">The track's place among the audio tracks, from 0 (the HTOA is not in AccurateRip).</param>
    public static AccurateRipMatch Match(IReadOnlyList<AccurateRipResponse> responses, int index, uint v1, uint v2)
    {
        int? v1Confidence = null, v2Confidence = null;
        var maxConfidence = 0;
        uint? maxChecksum = null;
        foreach (var response in responses.Where(r => index < r.TrackCount))
        {
            var confidence = response.Confidences[index];
            var checksum = response.Checksums[index];
            if (maxChecksum == null || confidence > maxConfidence)
            {
                (maxConfidence, maxChecksum) = (confidence, checksum);
            }

            if (checksum == v1 && (v1Confidence == null || confidence > v1Confidence))
            {
                v1Confidence = confidence;
            }

            if (checksum == v2 && (v2Confidence == null || confidence > v2Confidence))
            {
                v2Confidence = confidence;
            }
        }

        return new AccurateRipMatch(v1, v2, v1Confidence, v2Confidence, maxConfidence, maxChecksum);
    }

    /// <summary>True when some answer has this checksum (v1 or v2) for the track.</summary>
    public static bool Matches(IReadOnlyList<AccurateRipResponse> responses, int index, uint v1, uint v2) =>
        responses.Any(r => index < r.TrackCount && (r.Checksums[index] == v1 || r.Checksums[index] == v2));

    private static string Hex(ReadOnlySpan<byte> littleEndian) =>
        BinaryPrimitives.ReadUInt32LittleEndian(littleEndian).ToString("x8", CultureInfo.InvariantCulture);
}

/// <summary>Fetches a disc's answers from accuraterip.com.</summary>
public sealed class AccurateRipClient(IHttpClientFactory httpFactory, ILogger<AccurateRipClient> logger)
{
    /// <summary>The disc's answers; empty when AccurateRip doesn't know the disc; null when it couldn't be asked.</summary>
    public async Task<IReadOnlyList<AccurateRipResponse>?> FetchAsync(DiscIdentity disc, CancellationToken cancellationToken)
    {
        var url = AccurateRipDatabase.BaseUrl + disc.AccurateRipPath;
        try
        {
            using var response = await httpFactory.CreateClient("accuraterip").GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return AccurateRipDatabase.Parse(bytes);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("AccurateRip could not be reached ({Url}): {Message}", url, e.Message);
            return null;
        }
    }
}
