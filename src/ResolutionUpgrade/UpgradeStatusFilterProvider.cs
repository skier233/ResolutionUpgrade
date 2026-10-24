using System.Text.Json;
using Cove.Plugins;

namespace ResolutionUpgrade;

/// <summary>
/// Runs the "Resolution upgrade" filter on Cove's Videos list. Cove hands over the authorized candidate
/// ids for the rest of the query and this returns the ones whose stored record matches the chosen status,
/// so the normal list (with hover previews and every other filter) can show just the upgradeable videos.
/// </summary>
internal sealed class UpgradeStatusFilterProvider(Func<UpgradeRecordStore> records) : IExtensionEntityFilterProvider
{
    public const string FilterId = "upgrade-status";

    /// <summary>Filter values offered in the UI, mapped to the record states they cover.</summary>
    public static readonly IReadOnlyList<(string Value, string Label, string[] States)> Options =
    [
        ("available", "Upgrade available", [UpgradeStates.UpgradeAvailable]),
        ("downloaded", "Downloaded, not primary yet", [UpgradeStates.Downloaded]),
        ("downloading", "Downloading", [UpgradeStates.DownloadQueued, UpgradeStates.Downloading]),
        ("failed", "Failed", [UpgradeStates.CheckFailed, UpgradeStates.DownloadFailed]),
        ("no-upgrade", "No upgrade available", [UpgradeStates.UpToDate, UpgradeStates.NoSource]),
        ("checked", "Checked (any result)", []),
        ("unchecked", "Not checked yet", []),
    ];

    public IReadOnlyCollection<ExtensionEntityFilterDefinition> Filters { get; } =
        [new(FilterId, "videos")];

    public async Task<ExtensionEntityFilterResult> ResolveAsync(ExtensionEntityFilterRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.FilterId, FilterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown filter '{request.FilterId}'.");
        if (request.Value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("The resolution upgrade filter needs one of its listed values.");

        var value = request.Value.GetString()?.Trim().ToLowerInvariant();
        var option = Options.FirstOrDefault(entry => string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase));
        if (option.Value is null)
            throw new InvalidOperationException($"Unknown resolution upgrade status '{value}'.");

        var stored = await records().GetAllAsync(ct);
        var byVideoId = stored.ToDictionary(record => record.VideoId, record => record);
        var matches = request.CandidateIds.Where(id => Matches(option.Value, option.States, byVideoId, id));
        if (IsNegated(request.Modifier))
        {
            var included = matches.ToHashSet();
            matches = request.CandidateIds.Where(id => !included.Contains(id));
        }

        // The revision lets Cove reuse a batch result only while the records behind it are unchanged.
        var revision = stored.Count == 0
            ? "empty"
            : $"{stored.Count}:{stored.Max(record => record.UpdatedAt):O}";
        return new ExtensionEntityFilterResult(matches.ToArray(), revision);
    }

    private static bool Matches(string value, string[] states, Dictionary<int, UpgradeRecord> byVideoId, int videoId)
    {
        var known = byVideoId.TryGetValue(videoId, out var record);
        return value switch
        {
            "checked" => known,
            "unchecked" => !known,
            _ => known && states.Contains(record!.State, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static bool IsNegated(string modifier)
        => modifier.Replace("_", string.Empty).Replace("-", string.Empty)
            .Equals("notequals", StringComparison.OrdinalIgnoreCase);
}
