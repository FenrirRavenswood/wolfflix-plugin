using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.SousTitres;

/// <summary>
/// Sous-titres Podnapisi (gratuit, sans clé).
/// Apparaît dans « Rechercher des sous-titres » de Jellyfin et dans le téléchargement automatique.
/// </summary>
public sealed class SourcePodnapisi : ISubtitleProvider
{
    private const string Site = "https://www.podnapisi.net/subtitles";

    private readonly IHttpClientFactory _http;
    private readonly ILibraryManager _bibliotheque;
    private readonly ILogger<SourcePodnapisi> _log;

    public SourcePodnapisi(IHttpClientFactory http, ILibraryManager bibliotheque, ILogger<SourcePodnapisi> log)
    {
        _http = http;
        _bibliotheque = bibliotheque;
        _log = log;
    }

    public string Name => "WolfFlix · Podnapisi";

    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

    private HttpClient Client()
    {
        var c = _http.CreateClient("WolfFlix");
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WolfFlix-Jellyfin/0.4");
        return c;
    }

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var episode = request.ContentType == VideoContentType.Episode;
        var titre = episode ? OutilsSousTitres.Serie(_bibliotheque, request).Nom : request.Name;
        if (string.IsNullOrWhiteSpace(titre))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var q = new List<string>
        {
            "keywords=" + Uri.EscapeDataString(titre),
            "language=" + OutilsSousTitres.DeuxLettres(request)
        };
        int? saison = null;
        int? numero = null;
        if (episode)
        {
            if (request.ParentIndexNumber is not int s || request.IndexNumber is not int e)
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            saison = s;
            numero = e;
            q.Add("seasons=" + s.ToString(CultureInfo.InvariantCulture));
            q.Add("episodes=" + e.ToString(CultureInfo.InvariantCulture));
            q.Add("movie_type=tv-series");
            q.Add("movie_type=mini-series");
        }
        else
        {
            q.Add("movie_type=movie");
            if (request.ProductionYear is int an)
            {
                q.Add("year=" + an.ToString(CultureInfo.InvariantCulture));
            }
        }

        var resultats = new List<RemoteSubtitleInfo>();
        var vus = new HashSet<string>();
        var client = Client();
        for (int page = 1; page <= 2; page++)
        {
            JsonDocument doc;
            try
            {
                var url = Site + "/search/advanced?" + string.Join("&", q) + (page > 1 ? "&page=" + page : string.Empty);
                using var r = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode)
                {
                    _log.LogInformation("Podnapisi : réponse {Code}", (int)r.StatusCode);
                    break;
                }

                await using var flux = await r.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                doc = await JsonDocument.ParseAsync(flux, default, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Podnapisi : recherche impossible");
                break;
            }

            using (doc)
            {
                var racine = doc.RootElement;
                if (!racine.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (var it in data.EnumerateArray())
                {
                    var id = it.TryGetProperty("id", out var idv) ? (idv.ValueKind == JsonValueKind.String ? idv.GetString() : idv.ToString()) : null;
                    if (string.IsNullOrEmpty(id) || !vus.Add(id))
                    {
                        continue;
                    }

                    if (episode && it.TryGetProperty("movie", out var film) && film.ValueKind == JsonValueKind.Object
                        && film.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "movie")
                    {
                        continue;
                    }

                    var sorties = Chaines(it, "releases").Concat(Chaines(it, "custom_releases")).Where(x => x.Length > 0).ToList();
                    var drapeaux = Chaines(it, "flags").ToList();
                    var force = drapeaux.Any(f => f.Contains("foreign", StringComparison.OrdinalIgnoreCase) || f.Contains("forced", StringComparison.OrdinalIgnoreCase))
                        || OutilsSousTitres.ParleDeForce(string.Join(' ', sorties));
                    var sourds = drapeaux.Any(f => f.Contains("hearing", StringComparison.OrdinalIgnoreCase));
                    int? telechargements = null;
                    if (it.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Number && dl.TryGetInt32(out var n))
                    {
                        telechargements = n;
                    }

                    var nom = sorties.FirstOrDefault() ?? titre;
                    resultats.Add(new RemoteSubtitleInfo
                    {
                        Id = OutilsSousTitres.Encoder(new Jeton { U = id, S = saison, E = numero, F = force, H = sourds, L = request.Language ?? "fre" }),
                        ProviderName = Name,
                        Name = sorties.Count > 1 ? nom + " (+" + (sorties.Count - 1) + ")" : nom,
                        Format = "srt",
                        ThreeLetterISOLanguageName = request.Language,
                        DownloadCount = telechargements,
                        Forced = force,
                        HearingImpaired = sourds,
                        IsHashMatch = false
                    });
                }

                if (!racine.TryGetProperty("all_pages", out var tout) || !int.TryParse(tout.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) || page >= pages)
                {
                    break;
                }
            }
        }

        return resultats;
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var j = OutilsSousTitres.Decoder(id);
        var client = Client();
        var octets = await client.GetByteArrayAsync(Site + "/" + Uri.EscapeDataString(j.U) + "/download?container=zip", cancellationToken).ConfigureAwait(false);
        return OutilsSousTitres.Extraire(octets, j) ?? throw new InvalidOperationException("Podnapisi : archive sans sous-titres utilisables.");
    }

    private static IEnumerable<string> Chaines(JsonElement e, string nom)
    {
        if (e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String)
                {
                    yield return x.GetString() ?? string.Empty;
                }
            }
        }
    }
}
