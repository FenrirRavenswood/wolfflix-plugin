using Jellyfin.Plugin.WolfFlix.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WolfFlix;

/// <summary>Extension WolfFlix : données côté serveur pour le thème WolfFlix.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "WolfFlix";

    public override Guid Id => Guid.Parse("745eb480-581d-4756-a674-5fcb31f4cb01");

    public override string Description => "Données du thème WolfFlix : notes IMDb, avis, à venir, sous-titres.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "WolfFlix",
                EnableInMainMenu = true,
                MenuIcon = "pets",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
