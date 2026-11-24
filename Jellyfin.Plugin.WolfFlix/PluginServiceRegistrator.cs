using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WolfFlix;

/// <summary>Services de l'extension.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<NotesImdb>();
        serviceCollection.AddSingleton<AnalyseSousTitres>();
        serviceCollection.AddSingleton<Avis>();
        serviceCollection.AddSingleton<AVenir>();
    }
}
