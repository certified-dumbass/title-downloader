# Dreamstreaming Subtitle Guardian

Jellyfin 12.4 / .NET 10 plugin source. Audits every movie and **every individual episode** for existing non-forced English subtitles; where missing, searches and downloads English subtitles through your **installed and enabled** Jellyfin providers (e.g. Wyzie Subs or OpenSubtitles). Rechecks subtitle tracks after downloading and logs results.

## How to run

- Install/configure a subtitle provider in Jellyfin and enable it for the relevant libraries.
- Build with the .NET 10 SDK: `dotnet build src/Jellyfin.Plugin.SubtitleGuardian.csproj -c Release` or run `BUILD-EN-INSTALL.ps1` on Windows.
- Install the plugin's DLL and `src/meta.json` as a plugin folder in Jellyfin's data/plugins directory, restart Jellyfin.
- In **Dashboard → Scheduled Tasks**, select **Dreamstreaming: Verify & download English subtitles → Run now** to manually audit the **entire** video library. The task also runs daily by default.
- Set search and download budgets in the plugin configuration. Defaults: **25 searches, 12 download attempts, 5-second delay, 72-hour cooldown**. Setting a budget to **0** means unlimited; respect Wyzie and other provider quotas.
- Each run audits every film and episode, even if download budgets run out. If the budget is exhausted, the next run resumes download attempts from the first deferred item.
- Logs include summary counts; detailed results are saved as `last-report.json` in the plugin's data directory.

**What it does not do:** edit subtitle text, retime/synchronize subtitles, replace valid existing English subtitles, bypass download limits, or guarantee a match exists for every title.

**Status:** Source code / not yet compiled or tested on a live Jellyfin 12.4 server. Check the CI build result before installing, and back up Jellyfin before use.
