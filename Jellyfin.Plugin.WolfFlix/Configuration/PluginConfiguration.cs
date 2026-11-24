using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WolfFlix.Configuration;

/// <summary>Réglages de l'extension (saisis dans le tableau de bord, jamais dans le dépôt).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Clé API TMDB (v3), pour « À venir » et « Ce qui manque ».</summary>
    public string TmdbCle { get; set; } = string.Empty;

    /// <summary>Clé API SubDL, pour les sous-titres.</summary>
    public string SubdlCle { get; set; } = string.Empty;

    /// <summary>Votes IMDb minimum pour qu'une série entre dans le classement global.</summary>
    public int VotesMinSeries { get; set; } = 5000;

    /// <summary>Votes IMDb minimum pour qu'un film entre dans le classement global.</summary>
    public int VotesMinFilms { get; set; } = 25000;
}
