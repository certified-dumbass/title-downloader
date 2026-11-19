using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.SubtitleGuardian.Configuration;

namespace Jellyfin.Plugin.SubtitleGuardian;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "8ca2e2f8-e82a-4f74-b663-ea18b5fe4aac";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Dreamstreaming Subtitle Guardian";

    public override Guid Id => Guid.Parse(PluginGuid);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "SubtitleGuardianConfig",
                DisplayName = "Subtitle Guardian",
                EmbeddedResourcePath = "Jellyfin.Plugin.SubtitleGuardian.Configuration.configPage.html"
            }
        };
    }
}
