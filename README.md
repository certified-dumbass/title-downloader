# Dreamstreaming Subtitle Guardian 1.1.0.0

**Target:** Jellyfin 12.4, .NET 10, Windows or other Jellyfin OSes. This archive contains the *source code*, not a precompiled DLL. It has not yet been compiled or tested against a live server in the creator's environment.

## What it does

- Daily scheduled task **and manual Run now task** that audits **every movie and episode individually**. When the provider request budget is reached, it still checks the rest for existing subtitles and resumes missing-download attempts at the first deferred title on the next run.
- Checks non-forced English subtitle streams registered by Jellyfin and scans for matching external sidecar files (`.eng.srt`, `.en.srt`, `.english.ass`, etc.).
- If English subtitles are missing, asks Jellyfin's configured subtitle providers (e.g. Wyzie Subs, OpenSubtitles) to search for English.
- Ignores AI/machine-translated and forced-only subtitle matches; favors file-hash matches, non-SDH and higher ratings.
- Downloads one subtitle candidate; refreshes media metadata; verifies English is present, otherwise logs an unverified download.
- Saves `last-report.json` and `attempts.json` to Jellyfin's plugin-specific data folder.
- Defaults to **25 searches and 12 download attempts per run**, 5-second delay, 72-hour cooldown for failed/missing results. Set the corresponding limit to **0** for an unlimited full-library download pass (respect your provider rate limit).
- Respects provider availability; it does not provide or circumvent any external account/API limit.

## Prerequisites

1. Jellyfin **12.4** (not 10.11.x) running on Windows or another platform.
2. .NET **10 SDK** installed on the machine that builds the project.
3. One configured provider, such as Wyzie Subs or the official OpenSubtitles plugin; make sure the provider is enabled under your Movies and Series libraries. Its provider API key belongs in the provider plugin, not in Subtitle Guardian.
4. Back up Jellyfin's data/configuration and try a small run first.

## Build on Windows

1. Extract this source ZIP.
2. In Windows PowerShell, `cd` to the extracted folder.
3. Run `powershell -ExecutionPolicy Bypass -File .\BUILD-EN-INSTALL.ps1`.
4. It creates `Dreamstreaming-Subtitle-Guardian-install.zip` and an unpacked `Dreamstreaming Subtitle Guardian_1.1.0.0` folder.

## Install

1. Stop the Jellyfin server/service.
2. Copy the **unpacked plugin folder** (containing `Jellyfin.Plugin.SubtitleGuardian.dll` and `meta.json`) to your Jellyfin **data directory's** `plugins` folder. On Windows this can vary by installation mode; look for the same directory where other manually installed plugin folders live.
3. Restart Jellyfin.
4. Open Dashboard → Plugins → Dreamstreaming Subtitle Guardian; configure limits.
5. Open Dashboard → Scheduled Tasks → **Dreamstreaming: Verify & download English subtitles**, and select Run now to test. There is a daily interval trigger by default, adjustable in Scheduled Tasks.
6. Review the task in the Jellyfin logs and the plugin's `last-report.json` report. The log line begins `Subtitle Guardian:`.

## Common mistakes

- **Wyzie or OpenSubtitles not configured**: the Guardian itself is *not* a subtitle provider. It orchestrates installed providers.
- **Wyzie quota**: search and download requests still consume the provider's quota; 12 downloads plus 25 searches may exceed a small free-plan daily quota. Set the limits accordingly (e.g. 5/5 for an initial test).
- **No English match available**: cannot fabricate subtitles; the item is retried after the cooldown.
- **Subtitles already inside video**: by default those count as present; uncheck the embedded-subtitles option to request a separate subtitle file.
- **No visible result**: inspect logs for the explicit per-title failure reason or consult `last-report.json`. Check media folder permission when a download succeeds but verification fails.
- **Other automatic tasks**: disable Jellyfin's default `Download Missing Subtitles` scheduler or set it to a nonoverlapping time if you want Guardian to fully control request budgets.

## Notes and limitations

- This is a development source package. A GitHub Actions build validates compilation after every push; compilation success does not establish compatibility with a running server. Successful compilation and real Jellyfin 12.4 testing must be performed on a machine with .NET 10 and the installed provider plugins.
- Verification checks whether a valid English subtitle track or nonempty matching sidecar is present; it does **not** analyze whether text is semantically correct, correctly synchronized, or really in English.
- Does not download AI transcriptions/paid rows, and does not expose a public HTTP endpoint.
- Library metadata may have stale language tags; refresh movie metadata or correct mislabelled subtitle languages.
- State is local to the Jellyfin installation and stores last attempts (to avoid repeated paid API searches). It can be deleted if you want to reset cooldown state. A `next-item.json` file tracks which item to prioritize on the next download pass.

## Developer references

- Jellyfin 12.4 `ISubtitleManager`: https://github.com/jellyfin/jellyfin/blob/v12.4/MediaBrowser.Controller/Subtitles/ISubtitleManager.cs
- Built-in subtitle scheduled task: https://github.com/jellyfin/jellyfin/blob/v12.4/MediaBrowser.Providers/MediaInfo/SubtitleScheduledTask.cs
- Official plugin template: https://github.com/jellyfin/jellyfin-plugin-template
- Wyzie Jellyfin provider docs: https://docs.wyzie.io/plugins/jellyfin
