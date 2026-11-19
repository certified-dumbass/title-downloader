using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleGuardian.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool IncludeMovies { get; set; } = true;
    public bool IncludeEpisodes { get; set; } = true;
    public bool TreatEmbeddedEnglishAsPresent { get; set; } = true;
    public bool PreferNonHearingImpaired { get; set; } = true;
    public int MaxSearchesPerRun { get; set; } = 25;
    public int MaxDownloadsPerRun { get; set; } = 12;
    public int SecondsBetweenSearches { get; set; } = 5;
    public int RetryAfterHours { get; set; } = 72;
}
