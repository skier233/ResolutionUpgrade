# Resolution Upgrades for Cove

Finds videos in your Cove library whose source site now offers a higher resolution than the file you have,
downloads the better version into the **same** video, and swaps it in, optionally deleting the old file.
Tags, performers, ratings, play history and markers stay on the video because the video itself never changes;
only its primary file does.

## Requirements

- A Cove build that includes `IDownloaderService` and downloader site logins (added after Cove 1.4.1).
- A downloader extension that reports resolutions on its quality options, such as the community yt-dlp
  downloader. For sites that only serve higher resolutions to signed-in users (Pornhub's 1440p/4K, for
  example), add a login for the site under **Settings → Data Sources → Downloaders → Site Logins**.

## Using it

1. On the **Videos** page, select the videos to check (use *Select all matching* to take a whole filter,
   such as every 1080p video from one site) and choose **Check for higher resolution**. This queues a job
   that asks each installed downloader what it can fetch for the video's URLs.
2. Open **Resolution Upgrades** in the navigation. Videos with a better version are listed as
   *Upgrade available*.
3. Select them and choose **Download upgrades**, or use **Download higher resolution** from the Videos page.
   The download is attached to the existing video as an extra file; the current file stays primary.
4. When a video shows *Ready to replace*, select it and choose **Replace with downloaded file**. Tick
   **Delete the lower-resolution file after replacing** to remove the old file from disk as well.

Replacing uses Cove's own primary-file endpoint with the signed-in user's permissions. If the video has
markers, clips or other timed content, Cove only swaps files it can confirm are the same footage (matching
duration and perceptual hash). Otherwise the row explains why, and you can align the files from the video's
file list in Cove.

Rechecking a video resets its row, which also clears a download that was interrupted by a restart.

## Building

```bash
npm ci
npm run build:ui
dotnet build ResolutionUpgrade.slnx
dotnet test tests/ResolutionUpgrade.Tests/ResolutionUpgrade.Tests.csproj
```

With a sibling `../cove` checkout the build compiles against the local Cove source; CI uses the published
`Cove.Sdk`/`Cove.Core` packages. `scripts/stage-local-extensions.ps1` builds the extension and copies it into a
local Cove data directory's `extensions` folder.
