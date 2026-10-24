using System.Collections.Concurrent;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using JobProgress = Cove.Core.Interfaces.IJobProgress;

namespace ResolutionUpgrade;

/// <summary>The work behind the extension's check and download jobs.</summary>
internal sealed class UpgradeWorkflow(
    UpgradeRecordStore records,
    IExtensionServiceScopeFactory scopes,
    IDownloaderService downloaders,
    ILogger logger)
{
    // A check asks every matching downloader about each of the video's URLs (for yt-dlp, one process per
    // URL), so keep the fan-out small to stay polite to the source sites.
    private const int CheckConcurrency = 3;

    public async Task CheckAsync(IReadOnlyList<int> videoIds, JobProgress progress, CancellationToken ct)
    {
        var completed = 0;
        var outcomes = new ConcurrentDictionary<string, int>();
        progress.Report(0d, $"Checking {Text.Count(videoIds.Count, "video")} for higher resolutions...");

        await Parallel.ForEachAsync(
            videoIds,
            new ParallelOptions { MaxDegreeOfParallelism = CheckConcurrency, CancellationToken = ct },
            async (videoId, token) =>
            {
                UpgradeRecord? record = null;
                try
                {
                    record = await CheckVideoAsync(videoId, token);
                    await records.SaveAsync(record, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One video must never take the whole batch down.
                    logger.LogWarning(ex, "Could not record the resolution check for video {VideoId}", videoId);
                }

                if (record is not null)
                    outcomes.AddOrUpdate(record.State, 1, (_, count) => count + 1);

                var done = Interlocked.Increment(ref completed);
                progress.Report(done / (double)videoIds.Count, $"Checked {done} of {videoIds.Count}: {record?.Title ?? $"video {videoId}"}");
            });

        progress.Report(1d, $"Checked {Text.Count(videoIds.Count, "video")}: {SummarizeChecks(outcomes)}.");
    }

    private static string SummarizeChecks(IReadOnlyDictionary<string, int> outcomes)
    {
        int Count(string state) => outcomes.GetValueOrDefault(state);

        var parts = new List<string> { $"{Count(UpgradeStates.UpgradeAvailable)} can be upgraded" };
        if (Count(UpgradeStates.UpToDate) > 0)
            parts.Add($"{Count(UpgradeStates.UpToDate)} already at the best available resolution");
        if (Count(UpgradeStates.NoSource) > 0)
            parts.Add($"{Count(UpgradeStates.NoSource)} have no downloadable source");
        if (Count(UpgradeStates.CheckFailed) > 0)
            parts.Add($"{Count(UpgradeStates.CheckFailed)} could not be checked (see Resolution Upgrades for why)");
        return string.Join(", ", parts);
    }

    public async Task DownloadAsync(
        IReadOnlyList<int> videoIds,
        int concurrency,
        bool makePrimary,
        bool deleteOldFile,
        JobProgress progress,
        CancellationToken ct)
    {
        var completed = 0;
        var outcomes = new ConcurrentDictionary<string, int>();
        progress.Report(0d, $"Downloading higher resolutions for {Text.Count(videoIds.Count, "video")}...");

        // Cove's downloader also caps concurrent downloads; matching that cap avoids idle queued workers.
        await Parallel.ForEachAsync(
            videoIds,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (videoId, token) =>
            {
                UpgradeRecord? record = null;
                try
                {
                    record = await DownloadVideoAsync(videoId, makePrimary, deleteOldFile, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failure on one video must never stop the rest of an unattended batch.
                    logger.LogWarning(ex, "Resolution upgrade failed for video {VideoId}", videoId);
                    record = await TryRecordFailureAsync(videoId, ex.Message, token);
                }

                outcomes.AddOrUpdate(record?.State ?? SkippedOutcome, 1, (_, count) => count + 1);

                var done = Interlocked.Increment(ref completed);
                progress.Report(done / (double)videoIds.Count, $"Finished {done} of {videoIds.Count}: {record?.Title ?? $"video {videoId}"}");
            });

        progress.Report(1d, $"Finished {Text.Count(videoIds.Count, "video")}: {SummarizeDownloads(outcomes)}.");
    }

    private const string SkippedOutcome = "skipped";

    private static string SummarizeDownloads(IReadOnlyDictionary<string, int> outcomes)
    {
        int Count(string state) => outcomes.GetValueOrDefault(state);

        var parts = new List<string>();
        if (Count(UpgradeStates.Replaced) > 0)
            parts.Add($"{Count(UpgradeStates.Replaced)} upgraded and made primary");
        if (Count(UpgradeStates.Downloaded) > 0)
            parts.Add($"{Count(UpgradeStates.Downloaded)} downloaded, waiting to be made primary");
        if (Count(UpgradeStates.DownloadFailed) > 0)
            parts.Add($"{Count(UpgradeStates.DownloadFailed)} failed (see Resolution Upgrades for why)");
        if (Count(SkippedOutcome) > 0)
            parts.Add($"{Count(SkippedOutcome)} skipped (already downloading, or no longer ready to download)");
        return parts.Count == 0 ? "nothing left to do" : string.Join(", ", parts);
    }

    private async Task<UpgradeRecord> CheckVideoAsync(int videoId, CancellationToken ct)
    {
        var checkedAt = DateTime.UtcNow;
        try
        {
            var video = await LoadVideoAsync(videoId, ct);
            if (video is null)
            {
                return new UpgradeRecord
                {
                    VideoId = videoId,
                    State = UpgradeStates.CheckFailed,
                    Title = $"Video {videoId}",
                    Message = "The video no longer exists.",
                    CheckedAt = checkedAt,
                };
            }

            var primary = PrimaryFile(video);
            var record = new UpgradeRecord
            {
                VideoId = videoId,
                State = UpgradeStates.NoSource,
                Title = DisplayTitle(video, primary),
                CurrentFileId = primary?.Id,
                CurrentWidth = primary?.Width,
                CurrentHeight = primary?.Height,
                CheckedAt = checkedAt,
            };

            if (primary is null)
                return record with { Message = "The video has no primary file to compare with." };
            if (primary.Height <= 0)
                return record with { State = UpgradeStates.CheckFailed, Message = "The primary file has no known resolution. Rescan it, then check again." };

            var urls = video.Urls
                .Select(url => url.Url.Trim())
                .Where(IsHttpUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (urls.Count == 0)
                return record with { Message = "The video has no source URLs." };

            var matches = new List<DownloaderMatchDto>();
            var errors = new List<string>();
            foreach (var url in urls)
            {
                try
                {
                    matches.AddRange(await downloaders.MatchUrlAsync(url, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{url}: {ex.Message}");
                }
            }

            var best = UpgradeCandidate.PickBest(matches);
            if (best is null)
            {
                if (errors.Count > 0)
                    return record with { State = UpgradeStates.CheckFailed, Message = string.Join(Environment.NewLine, errors) };

                var videoDownloaders = matches
                    .Where(match => string.Equals(match.SupportedEntity, "Video", StringComparison.OrdinalIgnoreCase))
                    .Select(match => match.DownloaderName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (videoDownloaders.Count == 0)
                    return record with { Message = "No installed downloader can download this video's URLs." };

                // A downloader that can fetch the video but reports no resolutions (e.g. an older extension build)
                // leaves the comparison undecided; that is a failed check, not "nothing to upgrade".
                return record with
                {
                    State = UpgradeStates.CheckFailed,
                    Message = $"{string.Join(", ", videoDownloaders)} can download this video but did not report its resolutions. "
                        + "Update the downloader extension to a version that reports stream resolutions, then check again.",
                };
            }

            record = record with
            {
                Url = best.Url,
                DownloaderId = best.DownloaderId,
                DownloaderName = best.DownloaderName,
                QualityId = best.QualityId,
                QualityLabel = best.QualityLabel,
                AvailableWidth = best.Width,
                AvailableHeight = best.Height,
            };
            var unreachable = errors.Count > 0 ? $"Some URLs could not be checked: {string.Join("; ", errors)}" : null;
            return best.Height > primary.Height
                ? record with { State = UpgradeStates.UpgradeAvailable, Message = unreachable }
                : record with { State = UpgradeStates.UpToDate, Message = unreachable ?? $"The best available version is {best.Height}p." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Resolution check failed for video {VideoId}", videoId);
            return new UpgradeRecord
            {
                VideoId = videoId,
                State = UpgradeStates.CheckFailed,
                Title = $"Video {videoId}",
                Message = ex.Message,
                CheckedAt = checkedAt,
            };
        }
    }

    private async Task<UpgradeRecord?> DownloadVideoAsync(int videoId, bool makePrimary, bool deleteOldFile, CancellationToken ct)
    {
        // One download per video: a video another batch is already downloading is left to that batch.
        if (!records.TryBeginDownload(videoId))
            return null;
        try
        {
            return await DownloadClaimedVideoAsync(videoId, makePrimary, deleteOldFile, ct);
        }
        finally
        {
            records.EndDownload(videoId);
        }
    }

    private async Task<UpgradeRecord?> DownloadClaimedVideoAsync(int videoId, bool makePrimary, bool deleteOldFile, CancellationToken ct)
    {
        // The video is marked downloading only now that its download starts, so a batch cancelled part way leaves
        // the videos it never reached untouched. A recheck or dismissal since the batch was queued takes precedence.
        // This batch holds the video's download claim, so a record still saying queued or downloading is stale.
        var record = await records.UpdateAsync(
            videoId,
            current => UpgradeStates.CanDownload(current.State)
                || current.State is UpgradeStates.Downloading or UpgradeStates.DownloadQueued
                ? current with { State = UpgradeStates.Downloading, Message = null }
                : null,
            ct);
        if (record is null)
            return null;

        try
        {
            var result = await downloaders.DownloadAndImportAsync(
                new DownloaderImportRequest(record.DownloaderId!, record.Url!, DownloaderEntity.Video, videoId, record.QualityId)
                {
                    // The video already has its lower-resolution file and URL; that is the point.
                    AllowDuplicateDownload = true,
                },
                progress: null,
                ct);
            if (result is null)
                return await FailAsync(videoId, "The downloader finished without producing a file.", ct);

            var video = await LoadVideoAsync(videoId, ct)
                ?? throw new InvalidOperationException("The video was deleted while its upgrade was downloading.");
            var libraryPath = NormalizePath(result.LibraryPath);
            var replacement = video.Files.FirstOrDefault(file => string.Equals(NormalizePath(file.Path), libraryPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The downloaded file {result.LibraryPath} was not attached to the video.");
            var primary = PrimaryFile(video);

            if (primary is not null && replacement.Height <= primary.Height)
            {
                return await records.UpdateAsync(videoId, current => current with
                {
                    State = UpgradeStates.DownloadFailed,
                    CurrentFileId = primary.Id,
                    CurrentWidth = primary.Width,
                    CurrentHeight = primary.Height,
                    Message = $"The download is {replacement.Height}p, which is not higher than the current {primary.Height}p file. "
                        + $"It is attached to the video as file {replacement.Id}; remove it from the video's files if you don't want it.",
                }, ct);
            }

            var downloaded = await records.UpdateAsync(videoId, current => current with
            {
                State = UpgradeStates.Downloaded,
                CurrentFileId = primary?.Id,
                CurrentWidth = primary?.Width,
                CurrentHeight = primary?.Height,
                ReplacementFileId = replacement.Id,
                ReplacementWidth = replacement.Width,
                ReplacementHeight = replacement.Height,
                Message = null,
            }, ct);

            return makePrimary && downloaded is not null
                ? await MakePrimaryAsync(videoId, primary?.Id, replacement, deleteOldFile, ct)
                : downloaded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelling is not a failure: the upgrade is still there to download later.
            await records.UpdateAsync(
                videoId,
                current => current.State == UpgradeStates.Downloading
                    ? current with { State = UpgradeStates.UpgradeAvailable, Message = "The download was cancelled before it finished." }
                    : null,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resolution upgrade download failed for video {VideoId}", videoId);
            return await FailAsync(videoId, ex.Message, ct);
        }
    }

    /// <summary>
    /// Makes the downloaded file primary as soon as its download finishes, so a long unattended run needs
    /// nobody watching. Cove only swaps files it can confirm hold the same footage; anything else stays
    /// attached to the video and is reported for review.
    /// </summary>
    private async Task<UpgradeRecord?> MakePrimaryAsync(
        int videoId, int? previousFileId, VideoFile replacement, bool deleteOldFile, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IVideoFileMaintenanceService>();

        var swap = await maintenance.MakePrimaryWhenSameContentAsync(videoId, replacement.Id, ct);
        if (!swap.Applied)
        {
            return await records.UpdateAsync(videoId, current => current with
            {
                State = UpgradeStates.Downloaded,
                Message = swap.Reason ?? "The downloaded file could not be made primary automatically.",
            }, ct);
        }

        var note = "Replaced. The lower-resolution file is still attached to the video.";
        if (deleteOldFile && previousFileId is { } oldFileId)
        {
            var deletion = await maintenance.DeleteFileAsync(oldFileId, deleteFromDisk: true, ct);
            note = deletion.Applied
                ? "Replaced. The lower-resolution file was deleted."
                : $"Replaced, but the lower-resolution file was kept: {deletion.Reason}";
        }

        // A finished upgrade is no longer a work item: drop the record so the store only holds videos that
        // still need something. The returned record exists purely so the job summary can count it.
        var finished = await records.GetAsync(videoId, ct);
        await records.DeleteAsync(videoId, ct);
        return (finished ?? new UpgradeRecord { VideoId = videoId, State = UpgradeStates.Replaced }) with
        {
            State = UpgradeStates.Replaced,
            CurrentFileId = replacement.Id,
            CurrentWidth = replacement.Width,
            CurrentHeight = replacement.Height,
            ReplacementFileId = null,
            ReplacementWidth = null,
            ReplacementHeight = null,
            Message = note,
        };
    }

    /// <summary>Records a failure without letting the store itself break the batch.</summary>
    private async Task<UpgradeRecord?> TryRecordFailureAsync(int videoId, string message, CancellationToken ct)
    {
        try
        {
            return await FailAsync(videoId, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the upgrade failure for video {VideoId}", videoId);
            return null;
        }
    }

    private Task<UpgradeRecord?> FailAsync(int videoId, string message, CancellationToken ct)
        => records.UpdateAsync(videoId, current => current with { State = UpgradeStates.DownloadFailed, Message = message }, ct);

    private async Task<Video?> LoadVideoAsync(int videoId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IVideoRepository>().GetByIdWithRelationsAsync(videoId, ct);
    }

    private static VideoFile? PrimaryFile(Video video)
        => video.Files.FirstOrDefault(file => file.Id == video.PrimaryFileId);

    private static string DisplayTitle(Video video, VideoFile? primary)
        => !string.IsNullOrWhiteSpace(video.Title) ? video.Title : primary?.Basename ?? $"Video {video.Id}";

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}

internal static class Text
{
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
