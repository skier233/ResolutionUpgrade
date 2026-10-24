using Cove.Core.DTOs;

namespace ResolutionUpgrade.Tests;

public sealed class UpgradeCandidateTests
{
    [Fact]
    public void PickBest_ChoosesTheHighestResolutionAcrossDownloaders()
    {
        var best = UpgradeCandidate.PickBest(
        [
            VideoMatch("generic", "https://example.com/watch/1", Option("720", 1280, 720)),
            VideoMatch("yt-dlp", "https://www.example.com/watch/1", Option("best", 3840, 2160), Option("max-height-1080", 1920, 1080)),
        ]);

        Assert.NotNull(best);
        Assert.Equal("yt-dlp", best.DownloaderId);
        Assert.Equal("best", best.QualityId);
        Assert.Equal(2160, best.Height);
        Assert.Equal(3840, best.Width);
        Assert.Equal("https://www.example.com/watch/1", best.Url);
    }

    [Fact]
    public void PickBest_KeepsTheFirstOptionWhenHeightsTie()
    {
        var best = UpgradeCandidate.PickBest(
        [
            VideoMatch("yt-dlp", "https://example.com/watch/1", Option("best", 3840, 2160), Option("max-height-2160", 3840, 2160)),
        ]);

        Assert.Equal("best", best?.QualityId);
    }

    [Fact]
    public void PickBest_IgnoresNonVideoMatchesAndOptionsWithoutAHeight()
    {
        var best = UpgradeCandidate.PickBest(
        [
            new DownloaderMatchDto("audio", "Audio", "Audio", "https://example.com/a", null, [Option("hq", null, 4320)]),
            VideoMatch("images", "https://example.com/v", new DownloaderQualityOptionDto("original", "Original")),
        ]);

        Assert.Null(best);
    }

    private static DownloaderMatchDto VideoMatch(string downloaderId, string url, params DownloaderQualityOptionDto[] options)
        => new(downloaderId, downloaderId, "Video", url, null, [.. options]);

    private static DownloaderQualityOptionDto Option(string id, int? width, int height)
        => new(id, $"{height}p") { Width = width, Height = height };
}
