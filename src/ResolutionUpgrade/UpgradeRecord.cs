using Cove.Core.DTOs;

namespace ResolutionUpgrade;

internal static class UpgradeStates
{
    public const string UpgradeAvailable = "upgrade-available";
    public const string UpToDate = "up-to-date";
    public const string NoSource = "no-source";
    public const string CheckFailed = "check-failed";
    /// <summary>No longer written: a video now becomes <see cref="Downloading"/> only when its download starts.
    /// Kept so records saved by earlier versions still read.</summary>
    public const string DownloadQueued = "download-queued";
    public const string Downloading = "downloading";
    public const string DownloadFailed = "download-failed";
    public const string Downloaded = "downloaded";
    public const string Replaced = "replaced";

    /// <summary>States a download can be started from.</summary>
    public static bool CanDownload(string state) => state is UpgradeAvailable or DownloadFailed;
}

/// <summary>What the extension knows about one video's resolution upgrade.</summary>
internal sealed record UpgradeRecord
{
    public required int VideoId { get; init; }
    public required string State { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }

    /// <summary>The video's primary file when the record was last updated.</summary>
    public int? CurrentFileId { get; init; }
    public int? CurrentWidth { get; init; }
    public int? CurrentHeight { get; init; }

    public string? Url { get; init; }
    public string? DownloaderId { get; init; }
    public string? DownloaderName { get; init; }
    public string? QualityId { get; init; }
    public string? QualityLabel { get; init; }
    public int? AvailableWidth { get; init; }
    public int? AvailableHeight { get; init; }

    /// <summary>The downloaded higher-resolution file attached to the video, waiting to become primary.</summary>
    public int? ReplacementFileId { get; init; }
    public int? ReplacementWidth { get; init; }
    public int? ReplacementHeight { get; init; }

    public DateTime CheckedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>The highest-resolution video download any matching downloader offers.</summary>
internal sealed record UpgradeCandidate(
    string DownloaderId,
    string DownloaderName,
    string Url,
    string QualityId,
    string QualityLabel,
    int? Width,
    int Height)
{
    /// <summary>
    /// Highest height wins; ties keep the order downloaders and their options were returned in. Options
    /// without a height are ignored because they cannot be compared with the library file.
    /// </summary>
    public static UpgradeCandidate? PickBest(IEnumerable<DownloaderMatchDto> matches)
        => matches
            .Where(match => string.Equals(match.SupportedEntity, "Video", StringComparison.OrdinalIgnoreCase))
            .SelectMany(match => match.QualityOptions
                .Where(option => option.Height is > 0)
                .Select(option => new UpgradeCandidate(
                    match.DownloaderId,
                    match.DownloaderName,
                    match.NormalizedUrl,
                    option.Id,
                    option.Label,
                    option.Width,
                    option.Height!.Value)))
            .OrderByDescending(candidate => candidate.Height)
            .FirstOrDefault();
}
