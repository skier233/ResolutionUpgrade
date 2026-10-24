using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ResolutionUpgrade;

/// <summary>
/// Finds videos whose source site offers a higher resolution than the library file, downloads it into the
/// same video, and hands the swap (primary file + optional deletion of the old file) to the page, which
/// performs it through Cove's own file endpoints so the signed-in user's permissions and Cove's timeline
/// safety checks apply.
/// </summary>
public sealed class ResolutionUpgradeExtension : FullExtensionBase
{
    // Identity & metadata live in extension.json (the single source of truth); CoveExtensionBase surfaces them.

    private const string ApiBase = "/api/ext/resolution-upgrade";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private UpgradeRecordStore? _records;

    private UpgradeRecordStore Records => LazyInitializer.EnsureInitialized(ref _records, () => new UpgradeRecordStore(Store));

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        // Resolved lazily: the host sets the extension store after services are configured.
        services.AddSingleton<IExtensionEntityFilterProvider>(_ => new UpgradeStatusFilterProvider(() => Records));
    }

    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddPage(
                route: "resolution-upgrades",
                label: "Resolution Upgrades",
                componentName: "ResolutionUpgradesPage",
                icon: "circle-arrow-up",
                showInNav: true,
                navOrder: 60)
            // Both actions open the guided dialog (check → review → download → make primary) rather than
            // queuing a job silently. The dialog lives in the app-wide floating slot so it survives navigation.
            .AddAction(
                "check-resolution", "Check for resolution upgrades", "bulk", ["video"],
                "search", null, "openResolutionCheck", 60, Permissions.JobsRun, suppressSuccessAlert: true)
            .AddAction(
                "download-resolution-upgrade", "Upgrade resolution", "bulk", ["video"],
                "download", null, "openResolutionUpgrade", 61, Permissions.VideosWrite, suppressSuccessAlert: true)
            .AddSlot("app-floating-ui", "ResolutionUpgradeDialog")
            // Lets the normal Videos list show only the videos with an upgrade waiting, so they can be
            // browsed, previewed and picked with every other Cove filter still available.
            .AddExtensionListFilter(
                entityType: "video",
                id: "upgrade-status",
                label: "Resolution upgrade",
                criterionType: "enum",
                filterId: UpgradeStatusFilterProvider.FilterId,
                order: 70,
                modifiers: ["equals", "notEquals"],
                options: UpgradeStatusFilterProvider.Options.Select(option => new UIListFilterOption(option.Value, option.Label)))
            .Build();

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiBase).WithTags("Resolution Upgrades");

        group.MapGet("/results", async (
            ICurrentPrincipalAccessor principals, IAuthorizationService authorization, CancellationToken ct) =>
        {
            var records = await Records.GetAllAsync(ct);
            var (readable, _) = await FilterAuthorizedAsync(
                records.Select(record => record.VideoId).ToList(), Permissions.VideosRead, principals, authorization, ct);
            var readableIds = readable.ToHashSet();
            return Results.Json(
                records
                    .Where(record => readableIds.Contains(record.VideoId))
                    .OrderBy(record => StateOrder(record.State))
                    .ThenBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Json);
        }).RequireCovePermission(Permissions.VideosRead);

        // The dialog polls only the videos it is working on.
        group.MapPost("/results/query", async (
            VideoSelection selection, ICurrentPrincipalAccessor principals, IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var requested = selection.VideoIdList.ToHashSet();
            var records = (await Records.GetAllAsync(ct)).Where(record => requested.Contains(record.VideoId)).ToList();
            var (readable, _) = await FilterAuthorizedAsync(
                records.Select(record => record.VideoId).ToList(), Permissions.VideosRead, principals, authorization, ct);
            var readableIds = readable.ToHashSet();
            return Results.Json(records.Where(record => readableIds.Contains(record.VideoId)).ToList(), Json);
        }).RequireCovePermission(Permissions.VideosRead);

        group.MapPost("/check", async (
            VideoSelection selection, ICurrentPrincipalAccessor principals, IAuthorizationService authorization,
            IJobService jobs, IExtensionServiceScopeFactory scopes, IDownloaderService downloaders, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var (videoIds, denied) = await FilterAuthorizedAsync(
                selection.VideoIdList, Permissions.VideosRead, principals, authorization, ct);
            if (videoIds.Count == 0)
                return Error(denied > 0 ? "You don't have access to the selected videos." : "Select at least one video to check.");

            var workflow = CreateWorkflow(scopes, downloaders, loggers);
            var description = $"Checking {Text.Count(videoIds.Count, "video")} for higher resolutions";
            var jobId = jobs.EnqueueFor(
                JobOwner.FromPrincipal(principals.Current),
                "resolution-upgrade-check",
                description,
                (progress, jobCt) => workflow.CheckAsync(videoIds, progress, jobCt),
                exclusive: false);

            return Results.Json(new { jobId, videoIds, description = WithSkipped(description, Skipped(denied, "you don't have access to")) }, Json);
        }).RequireCovePermission(Permissions.VideosRead, Permissions.JobsRun);

        group.MapPost("/download", async (
            VideoSelection selection, ICurrentPrincipalAccessor principals, IAuthorizationService authorization,
            IJobService jobs, IExtensionServiceScopeFactory scopes, IDownloaderService downloaders, CoveConfiguration config,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var makePrimary = selection.MakePrimary ?? false;
            var deleteOldFile = makePrimary && (selection.DeleteOldFile ?? false);
            if (deleteOldFile && !authorization.Has(principals.Current, Permissions.FilesDelete))
                return Error("You don't have permission to delete files, so the lower-resolution files can't be removed.");

            var (writable, denied) = await FilterAuthorizedAsync(
                selection.VideoIdList, Permissions.VideosWrite, principals, authorization, ct);
            // Nothing is marked yet: each video becomes "downloading" when its own download starts.
            var queued = new List<int>();
            var alreadyDownloading = 0;
            foreach (var videoId in writable)
            {
                if (Records.IsDownloading(videoId))
                {
                    alreadyDownloading++;
                    continue;
                }

                var record = await Records.GetAsync(videoId, ct);
                if (record is not null && UpgradeStates.CanDownload(record.State))
                    queued.Add(videoId);
            }

            if (queued.Count == 0)
            {
                return Error(writable.Count == 0 && denied > 0
                    ? "You don't have permission to change the selected videos."
                    : alreadyDownloading > 0 && alreadyDownloading == writable.Count
                        ? "The selected videos are already downloading."
                        : "None of the selected videos have a higher resolution waiting to download. Check them for higher resolutions first.");
            }

            var workflow = CreateWorkflow(scopes, downloaders, loggers);
            var finalize = makePrimary
                ? deleteOldFile
                    ? ", making them primary and deleting the old files"
                    : ", making them primary"
                : string.Empty;
            var description = $"Downloading higher resolutions for {Text.Count(queued.Count, "video")}{finalize}";
            var jobId = jobs.EnqueueFor(
                JobOwner.FromPrincipal(principals.Current),
                "resolution-upgrade-download",
                description,
                (progress, jobCt) => workflow.DownloadAsync(queued, config.MaxConcurrentDownloads, makePrimary, deleteOldFile, progress, jobCt),
                exclusive: false);

            var skipped = new[]
            {
                Skipped(alreadyDownloading, "are already downloading"),
                Skipped(writable.Count - queued.Count - alreadyDownloading, "had no upgrade ready to download"),
                Skipped(denied, "you can't change"),
            };
            return Results.Json(new { jobId, videoIds = queued, description = WithSkipped(description, skipped) }, Json);
        }).RequireCovePermission(Permissions.VideosWrite, Permissions.JobsRun);

        // The page calls this after it has made the downloaded file primary through Cove's own endpoints.
        group.MapPost("/replaced", async (
            ReplacedRequest request, ICurrentPrincipalAccessor principals, IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var access = await authorization.AuthorizeAsync(
                principals.Current, Permissions.VideosWrite, EntityRef.Of(EntityKinds.Video, request.VideoId), ct);
            if (!access.Allowed)
                return Error("You don't have permission to change this video.", StatusCodes.Status403Forbidden);

            var record = await Records.GetAsync(request.VideoId, ct);
            if (record is null || record.State != UpgradeStates.Downloaded)
                return Error("This video has no downloaded upgrade waiting to be applied.", StatusCodes.Status409Conflict);

            // The upgrade is done, so it stops being a work item and the record goes away.
            await Records.DeleteAsync(request.VideoId, ct);
            return Results.Json(record with { State = UpgradeStates.Replaced, Message = null }, Json);
        }).RequireCovePermission(Permissions.VideosWrite);

        group.MapPost("/dismiss", async (
            VideoSelection selection, ICurrentPrincipalAccessor principals, IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var (videoIds, denied) = await FilterAuthorizedAsync(
                selection.VideoIdList, Permissions.VideosRead, principals, authorization, ct);
            foreach (var videoId in videoIds)
                await Records.DeleteAsync(videoId, ct);

            return Results.Json(new { dismissed = videoIds.Count, denied }, Json);
        }).RequireCovePermission(Permissions.VideosRead);
    }

    private UpgradeWorkflow CreateWorkflow(
        IExtensionServiceScopeFactory scopes, IDownloaderService downloaders, ILoggerFactory loggers)
        => new(Records, scopes, downloaders, loggers.CreateLogger<ResolutionUpgradeExtension>());

    private static async Task<(List<int> Allowed, int Denied)> FilterAuthorizedAsync(
        IReadOnlyList<int> videoIds,
        string permission,
        ICurrentPrincipalAccessor principals,
        IAuthorizationService authorization,
        CancellationToken ct)
    {
        var allowed = new List<int>();
        foreach (var videoId in videoIds)
        {
            var result = await authorization.AuthorizeAsync(
                principals.Current, permission, EntityRef.Of(EntityKinds.Video, videoId), ct);
            if (result.Allowed)
                allowed.Add(videoId);
        }

        return (allowed, videoIds.Count - allowed.Count);
    }

    private static int StateOrder(string state) => state switch
    {
        UpgradeStates.Downloaded => 0,
        UpgradeStates.UpgradeAvailable => 1,
        UpgradeStates.DownloadQueued or UpgradeStates.Downloading => 2,
        UpgradeStates.DownloadFailed => 3,
        UpgradeStates.CheckFailed => 4,
        UpgradeStates.NoSource => 5,
        UpgradeStates.UpToDate => 6,
        _ => 7,
    };

    private static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest)
        => Results.Json(new { error = message }, Json, statusCode: statusCode);

    private static string? Skipped(int count, string reason)
        => count > 0 ? $"{Text.Count(count, "video")} {reason}" : null;

    private static string WithSkipped(string description, params string?[] skipped)
    {
        var notes = skipped.OfType<string>().ToList();
        return notes.Count == 0 ? $"{description}." : $"{description}. Skipped {string.Join("; ", notes)}.";
    }

    /// <summary>
    /// Accepts the host's bulk-action payload (<c>entityIds</c>) and the page's requests (<c>videoIds</c>).
    /// A download also carries what to do when each file lands, chosen before the run starts so an
    /// unattended batch never waits for a browser.
    /// </summary>
    private sealed record VideoSelection(
        IReadOnlyList<int>? VideoIds,
        IReadOnlyList<int>? EntityIds,
        bool? MakePrimary = null,
        bool? DeleteOldFile = null)
    {
        public IReadOnlyList<int> VideoIdList => (VideoIds ?? EntityIds ?? []).Where(id => id > 0).Distinct().ToList();
    }

    private sealed record ReplacedRequest(int VideoId, bool OldFileDeleted);
}
