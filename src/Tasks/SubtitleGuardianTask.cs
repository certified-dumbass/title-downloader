using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleGuardian.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleGuardian.Tasks;

/// <summary>
/// Periodic, provider-agnostic audit that downloads only missing English subtitles.
/// Its search and download calls are delegated to Jellyfin's own ISubtitleManager.
/// </summary>
public sealed class SubtitleGuardianTask : IScheduledTask
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".ttml"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILogger<SubtitleGuardianTask> _logger;

    public SubtitleGuardianTask(
        ILibraryManager libraryManager,
        ISubtitleManager subtitleManager,
        ILogger<SubtitleGuardianTask> logger)
    {
        _libraryManager = libraryManager;
        _subtitleManager = subtitleManager;
        _logger = logger;
    }

    public string Name => "Dreamstreaming: Verify & download English subtitles";
    public string Description => "Checks movies and episodes for English subtitles, downloads only missing tracks through installed providers, then verifies and reports results.";
    public string Category => "Dreamstreaming";
    public string Key => "DreamstreamingSubtitleGuardian";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Subtitle Guardian plugin is not available.");
        var config = plugin.Configuration;
        if (!config.Enabled)
        {
            _logger.LogInformation("Subtitle Guardian is disabled in plugin settings.");
            progress.Report(100);
            return;
        }

        // Zero removes the per-run limit; use carefully with rate-limited subtitle providers.
        var maxSearches = Math.Clamp(config.MaxSearchesPerRun, 0, 10000);
        var maxDownloads = Math.Clamp(config.MaxDownloadsPerRun, 0, 10000);
        var retryHours = Math.Clamp(config.RetryAfterHours, 1, 24 * 365);
        var delaySeconds = Math.Clamp(config.SecondsBetweenSearches, 0, 120);
        var dataDirectory = plugin.DataFolderPath;
        Directory.CreateDirectory(dataDirectory);
        var attemptPath = Path.Combine(dataDirectory, "attempts.json");
        var reportPath = Path.Combine(dataDirectory, "last-report.json");
        var cursorPath = Path.Combine(dataDirectory, "next-item.json");
        var attempts = ReadAttempts(attemptPath);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        foreach (var stale in attempts.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
        {
            attempts.Remove(stale);
        }

        var report = new ScanReport { StartedUtc = DateTime.UtcNow };
        var videos = GetVideos(config).OrderBy(v => v.Id).ToList();
        report.TotalVideos = videos.Count;
        // Keep auditing every item each run, but resume download attempts from where
        // the previous rate-limited run stopped. This avoids starving later seasons.
        var nextItem = ReadCursor(cursorPath);
        var startingIndex = videos.FindIndex(v => v.Id == nextItem);
        if (startingIndex > 0)
        {
            videos = videos.Skip(startingIndex).Concat(videos.Take(startingIndex)).ToList();
        }
        Guid? nextDeferredItem = null;
        var searches = 0;
        var attemptedDownloads = 0;
        var changed = false;

        try
        {
            for (var index = 0; index < videos.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var video = videos[index];
                report.Checked++;

                try
                {
                    // Always re-check reality first; a manual download clears a previous failure.
                    if (HasEnglishSubtitles(video, config.TreatEmbeddedEnglishAsPresent))
                    {
                        report.AlreadyPresent++;
                        attempts.Remove(video.Id.ToString("N"));
                        continue;
                    }

                    report.Missing++;
                    var key = video.Id.ToString("N");
                    if (attempts.TryGetValue(key, out var lastAttempt)
                        && DateTime.UtcNow - lastAttempt < TimeSpan.FromHours(retryHours))
                    {
                        report.OnCooldown++;
                        continue;
                    }

                    if ((maxSearches > 0 && searches >= maxSearches)
                        || (maxDownloads > 0 && attemptedDownloads >= maxDownloads))
                    {
                        report.DeferredByLimit++;
                        nextDeferredItem ??= video.Id;
                        continue;
                    }

                    var libraryOptions = _libraryManager.GetLibraryOptions(video);
                    var disabled = libraryOptions.DisabledSubtitleFetchers ?? Array.Empty<string>();
                    var providerCount = _subtitleManager.GetSupportedProviders(video)
                        .Count(provider => !disabled.Contains(provider.Name, StringComparer.OrdinalIgnoreCase));
                    if (providerCount == 0)
                    {
                        report.NoProvider++;
                        AddResult(report, video, "NoProvider", "No subtitle provider available. Enable Wyzie Subs or OpenSubtitles for this library.");
                        continue;
                    }

                    // Throttle every provider request, including searches returning no matches.
                    if (searches > 0 && delaySeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                    }
                    searches++;
                    report.Searches = searches;
                    // Only charge a search request when a video really needs subtitles.
                    var request = BuildSearchRequest(video, libraryOptions);
                    var results = await _subtitleManager.SearchSubtitles(
                        request, cancellationToken).ConfigureAwait(false);
                    attempts[key] = DateTime.UtcNow;
                    changed = true;

                    var candidate = ChooseSubtitle(results, config.PreferNonHearingImpaired);
                    if (candidate is null)
                    {
                        report.NoMatch++;
                        AddResult(report, video, "NoMatch", "No eligible English subtitle was found.");
                        continue;
                    }

                    attemptedDownloads++;
                    report.DownloadAttempts = attemptedDownloads;
                    await _subtitleManager.DownloadSubtitles(
                        video,
                        libraryOptions,
                        candidate.Id,
                        cancellationToken).ConfigureAwait(false);

                    // Reindex video metadata, exactly as Jellyfin's built-in task does.
                    await video.RefreshMetadata(cancellationToken).ConfigureAwait(false);
                    if (HasEnglishSubtitles(video, config.TreatEmbeddedEnglishAsPresent))
                    {
                        report.VerifiedDownloads++;
                        attempts.Remove(key);
                        AddResult(report, video, "Verified", $"Verified after download from {candidate.ProviderName}.");
                    }
                    else
                    {
                        report.UnverifiedDownloads++;
                        AddResult(report, video, "NotVerified", $"Download returned but no English subtitle could be verified: {candidate.ProviderName}.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Errors++;
                    attempts[video.Id.ToString("N")] = DateTime.UtcNow;
                    changed = true;
                    AddResult(report, video, "Error", ex.Message);
                    _logger.LogWarning(ex, "Subtitle Guardian failed for {Title} ({Path})", video.Name, video.Path);
                }
                finally
                {
                    if (changed && index % 5 == 0)
                    {
                        WriteJson(attemptPath, attempts);
                        changed = false;
                    }
                    progress.Report(100.0 * (index + 1) / Math.Max(1, videos.Count));
                }

            }
        }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            try
            {
                WriteJson(attemptPath, attempts);
                WriteJson(reportPath, report);
                WriteJson(cursorPath, new ScanCursor { NextItemId = nextDeferredItem });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist Subtitle Guardian reports to {Folder}", dataDirectory);
            }
            _logger.LogInformation(
                "Subtitle Guardian: checked={Checked}, alreadyEnglish={Already}, missing={Missing}, searches={Searches}, attemptedDownloads={Attempted}, verified={Verified}, unverified={Unverified}, noMatch={NoMatch}, noProvider={NoProvider}, cooldown={Cooldown}, deferred={Deferred}, errors={Errors}. Report: {ReportPath}",
                report.Checked, report.AlreadyPresent, report.Missing, report.Searches,
                report.DownloadAttempts, report.VerifiedDownloads, report.UnverifiedDownloads,
                report.NoMatch, report.NoProvider, report.OnCooldown, report.DeferredByLimit,
                report.Errors, reportPath);
        }

        progress.Report(100);
    }

    private IEnumerable<Video> GetVideos(PluginConfiguration config)
    {
        var kinds = new List<BaseItemKind>();
        if (config.IncludeMovies) kinds.Add(BaseItemKind.Movie);
        if (config.IncludeEpisodes) kinds.Add(BaseItemKind.Episode);
        if (kinds.Count == 0) yield break;

        var seen = new HashSet<Guid>();
        foreach (var library in _libraryManager.RootFolder.Children.ToList())
        {
            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IncludeItemTypes = kinds.ToArray(),
                DtoOptions = new DtoOptions(true),
                SourceTypes = new[] { SourceType.Library },
                Parent = library,
                Recursive = true,
                IncludeOwnedItems = true
            };
            foreach (var video in _libraryManager.GetItemList(query).OfType<Video>())
            {
                if (seen.Add(video.Id) && !string.IsNullOrWhiteSpace(video.Path)
                    && video.VideoType == VideoType.VideoFile)
                {
                    yield return video;
                }
            }
        }
    }

    private static bool HasEnglishSubtitles(Video video, bool countEmbedded)
    {
        if (video.GetMediaStreams().Any(stream =>
                stream.Type == MediaStreamType.Subtitle
                && (countEmbedded || stream.IsExternal)
                && IsEnglish(stream.Language)
                && stream.IsForced != true))
        {
            return true;
        }

        // Sometimes a newly-written sidecar hasn't been indexed by Jellyfin yet.
        // Guard against treating an unrelated .srt in the same folder as a match.
        if (string.IsNullOrWhiteSpace(video.Path)) return false;
        try
        {
            var directory = Path.GetDirectoryName(video.Path);
            var name = Path.GetFileNameWithoutExtension(video.Path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;
            foreach (var file in Directory.EnumerateFiles(directory, name + ".*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(file);
                if (!SubtitleExtensions.Contains(extension) || new FileInfo(file).Length == 0) continue;
                var filenameWithoutExtension = Path.GetFileNameWithoutExtension(file);
                if (!filenameWithoutExtension.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) continue;
                var languagePart = filenameWithoutExtension[(name.Length + 1)..];
                var parts = languagePart.Split('.', '_', '-', ' ');
                if (!parts.Any(part => part.Equals("forced", StringComparison.OrdinalIgnoreCase))
                    && parts.Any(IsEnglish)) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsEnglish(string? code)
    {
        return code is not null &&
            (code.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || code.Equals("en", StringComparison.OrdinalIgnoreCase)
            || code.Equals("english", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en_", StringComparison.OrdinalIgnoreCase));
    }

    private static SubtitleSearchRequest BuildSearchRequest(Video video, MediaBrowser.Model.Configuration.LibraryOptions options)
    {
        // SearchSubtitles(Video, ...) ignores library-specific disabled providers.
        // The request overload lets us honour the user's Jellyfin library settings.
        var request = new SubtitleSearchRequest
        {
            ContentType = video is Episode ? VideoContentType.Episode : VideoContentType.Movie,
            Language = "eng",
            MediaPath = video.Path,
            Name = video.Name,
            IndexNumber = video.IndexNumber,
            ParentIndexNumber = video.ParentIndexNumber,
            ProductionYear = video.ProductionYear,
            RuntimeTicks = video.RunTimeTicks,
            ProviderIds = video.ProviderIds,
            IsPerfectMatch = options.RequirePerfectSubtitleMatch,
            IsAutomated = true,
            SearchAllProviders = false,
            DisabledSubtitleFetchers = options.DisabledSubtitleFetchers ?? Array.Empty<string>(),
            SubtitleFetcherOrder = options.SubtitleFetcherOrder ?? Array.Empty<string>()
        };
        if (video is Episode episode)
        {
            request.SeriesName = episode.SeriesName;
            request.IndexNumberEnd = episode.IndexNumberEnd;
        }
        return request;
    }

    private static Guid? ReadCursor(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ScanCursor>(File.ReadAllText(path))?.NextItemId
                : null;
        }
        catch (Exception) { return null; }
    }

    private sealed class ScanCursor
    {
        public Guid? NextItemId { get; set; }
    }

    private static RemoteSubtitleInfo? ChooseSubtitle(
        IEnumerable<RemoteSubtitleInfo>? found, bool preferNoSdh)
    {
        return found?
            .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                && IsEnglish(item.ThreeLetterISOLanguageName)
                && item.AiTranslated != true
                && item.MachineTranslated != true
                && item.Forced != true)
            .OrderByDescending(item => item.IsHashMatch == true)
            .ThenBy(item => preferNoSdh && item.HearingImpaired == true)
            .ThenByDescending(item => item.CommunityRating ?? 0)
            .ThenByDescending(item => item.DownloadCount ?? 0)
            .FirstOrDefault();
    }

    private static Dictionary<string, DateTime> ReadAttempts(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, true);
    }

    private static void AddResult(ScanReport report, Video video, string state, string detail)
    {
        if (report.Details.Count >= 300) return;
        report.Details.Add(new ScanDetail
        {
            ItemId = video.Id,
            Title = video.Name,
            Status = state,
            Detail = detail
        });
    }

    private sealed class ScanReport
    {
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public int TotalVideos { get; set; }
        public int Checked { get; set; }
        public int AlreadyPresent { get; set; }
        public int Missing { get; set; }
        public int Searches { get; set; }
        public int DownloadAttempts { get; set; }
        public int VerifiedDownloads { get; set; }
        public int UnverifiedDownloads { get; set; }
        public int NoMatch { get; set; }
        public int NoProvider { get; set; }
        public int OnCooldown { get; set; }
        public int DeferredByLimit { get; set; }
        public int Errors { get; set; }
        public List<ScanDetail> Details { get; set; } = new();
    }

    private sealed class ScanDetail
    {
        public Guid ItemId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }
}
