using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.SousTitres;

/// <summary>
/// Sous-titres SubDL (gratuit, clé API dans les réglages de l'extension).
/// Apparaît dans « Rechercher des sous-titres » de Jellyfin et dans le téléchargement automatique.
/// </summary>
public sealed class SourceSubdl : ISubtitleProvider
{
    private const string Api = "https://api.subdl.com/api/v1/subtitles";
    private const string Telechargement = "https://dl.subdl.com";

    private readonly IHttpClientFactory _http;
    private readonly ILibraryManager _bibliotheque;
    private readonly ILogger<SourceSubdl> _log;

    public SourceSubdl(IHttpClientFactory http, ILibraryManager bibliotheque, ILogger<SourceSubdl> log)
    {
        _http = http;
        _bibliotheque = bibliotheque;
        _log = log;
    }

    public string Name => "WolfFlix · SubDL";

    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var cle = Plugin.Instance?.Configuration.SubdlCle;
        if (string.IsNullOrWhiteSpace(cle))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var langue = OutilsSousTitres.DeuxLettres(request).ToUpperInvariant();
        var param = new Dictionary<string, string>
        {
            ["api_key"] = cle.Trim(),
            ["languages"] = langue,
            ["subs_per_page"] = "30",
            ["releases"] = "1",
            ["comment"] = "1"
        };

        var episode = request.ContentType == VideoContentType.Episode;
        int? saison = request.ParentIndexNumber;
        int? numero = request.IndexNumber;
        if (episode)
        {
            var (imdb, tmdb, nom) = OutilsSousTitres.Serie(_bibliotheque, request);
            param["type"] = "tv";
            if (!string.IsNullOrEmpty(imdb))
            {
                param["imdb_id"] = imdb;
            }
            else if (!string.IsNullOrEmpty(tmdb))
            {
                param["tmdb_id"] = tmdb;
            }
            else
            {
                param["film_name"] = Nettoyer(nom);
            }
        }
        else
        {
            param["type"] = "movie";
            var imdb = request.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
            var tmdb = request.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(imdb))
            {
                param["imdb_id"] = imdb;
            }
            else if (!string.IsNullOrEmpty(tmdb))
            {
                param["tmdb_id"] = tmdb;
            }
            else
            {
                param["film_name"] = Nettoyer(request.Name ?? string.Empty);
                if (request.ProductionYear is int an)
                {
                    param["year"] = an.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        var resultats = new List<RemoteSubtitleInfo>();
        var vus = new HashSet<string>();
        if (episode && saison is int s && numero is int e)
        {
            // D'abord l'épisode précis, puis les packs de la saison
            await Chercher(new Dictionary<string, string>(param) { ["season_number"] = s.ToString(CultureInfo.InvariantCulture), ["episode_number"] = e.ToString(CultureInfo.InvariantCulture) }, request, s, e, true, resultats, vus, cancellationToken).ConfigureAwait(false);
            await Chercher(new Dictionary<string, string>(param) { ["season_number"] = s.ToString(CultureInfo.InvariantCulture) }, request, s, e, false, resultats, vus, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Chercher(param, request, null, null, true, resultats, vus, cancellationToken).ConfigureAwait(false);
        }

        return resultats;
    }

    private async Task Chercher(Dictionary<string, string> param, SubtitleSearchRequest request, int? saison, int? numero, bool precis, List<RemoteSubtitleInfo> resultats, HashSet<string> vus, CancellationToken annulation)
    {
        var url = Api + "?" + string.Join("&", param.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
        JsonDocument doc;
        try
        {
            var client = _http.CreateClient("WolfFlix");
            client.Timeout = TimeSpan.FromSeconds(20);
            using var r = await client.GetAsync(url, annulation).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                _log.LogInformation("SubDL : réponse {Code}", (int)r.StatusCode);
                return;
            }

            await using var flux = await r.Content.ReadAsStreamAsync(annulation).ConfigureAwait(false);
            doc = await JsonDocument.ParseAsync(flux, default, annulation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "SubDL : recherche impossible");
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("subtitles", out var liste) || liste.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var it in liste.EnumerateArray())
            {
                var lien = Texte(it, "url");
                if (string.IsNullOrEmpty(lien) || !vus.Add(lien))
                {
                    continue;
                }

                if (Booleen(it, "ai_translated") || Booleen(it, "machine_translated"))
                {
                    continue;
                }

                var sorties = new List<string>();
                if (it.TryGetProperty("releases", out var rel) && rel.ValueKind == JsonValueKind.Array)
                {
                    sorties.AddRange(rel.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0));
                }

                var nom = Texte(it, "release_name");
                if (string.IsNullOrEmpty(nom))
                {
                    nom = sorties.FirstOrDefault() ?? Texte(it, "name");
                }

                if (numero is int e)
                {
                    // Garder ce qui contient l'épisode : l'épisode lui-même, une plage qui l'inclut, ou la saison entière
                    var de = Entier(it, "episode_from") ?? Entier(it, "episode");
                    var a = Entier(it, "episode_end") ?? de;
                    var saisonEntiere = Booleen(it, "full_season");
                    var s = Entier(it, "season");
                    if (s is int ss && saison is int sv && ss != sv && ss != 0)
                    {
                        continue;
                    }

                    if (!saisonEntiere && de is int d && a is int f && (e < d || e > f))
                    {
                        continue;
                    }

                    if (!saisonEntiere && de is null && !Contient(sorties.Append(nom), saison, e, precis))
                    {
                        continue;
                    }
                }

                var commentaire = Texte(it, "comment");
                var force = OutilsSousTitres.ParleDeForce(nom + " " + commentaire + " " + string.Join(' ', sorties));
                var sourds = Booleen(it, "hi");
                resultats.Add(new RemoteSubtitleInfo
                {
                    Id = OutilsSousTitres.Encoder(new Jeton { U = lien, S = saison, E = numero, F = force, H = sourds, L = request.Language ?? "fre" }),
                    ProviderName = Name,
                    Name = sorties.Count > 1 ? nom + " (+" + (sorties.Count - 1) + ")" : nom,
                    Comment = string.Join(" · ", new[] { Booleen(it, "full_season") ? "Pack de saison" : string.Empty, commentaire }.Where(x => x.Length > 0)),
                    Author = Texte(it, "author"),
                    Format = "srt",
                    ThreeLetterISOLanguageName = request.Language,
                    Forced = force,
                    HearingImpaired = sourds,
                    IsHashMatch = false
                });
            }
        }
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var j = OutilsSousTitres.Decoder(id);
        var client = _http.CreateClient("WolfFlix");
        var lien = j.U.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? j.U : Telechargement + (j.U.StartsWith('/') ? j.U : "/" + j.U);
        var octets = await client.GetByteArrayAsync(lien, cancellationToken).ConfigureAwait(false);
        return OutilsSousTitres.Extraire(octets, j) ?? throw new InvalidOperationException("SubDL : aucun fichier de sous-titres pour cet épisode dans l'archive.");
    }

    private static string Nettoyer(string titre)
        => string.Join(' ', System.Text.RegularExpressions.Regex.Replace(titre, "[<>{}\\[\\]'\"`´’;\\\\/]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool Contient(IEnumerable<string> noms, int? saison, int episode, bool precis)
    {
        foreach (var n in noms)
        {
            var m = System.Text.RegularExpressions.Regex.Match(n ?? string.Empty, @"[Ss](\d{1,2})[ ._-]?[Ee](\d{1,3})");
            if (m.Success)
            {
                return int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) == episode && (saison is null || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == saison);
            }
        }

        // Rien de reconnaissable : on garde seulement si la recherche visait déjà l'épisode
        return precis;
    }

    private static string Texte(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int? Entier(JsonElement e, string nom)
    {
        if (!e.TryGetProperty(nom, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
        {
            return i;
        }

        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j))
        {
            return j;
        }

        return null;
    }

    private static bool Booleen(JsonElement e, string nom)
    {
        if (!e.TryGetProperty(nom, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetInt32(out var i) && i != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false
        };
    }
}
