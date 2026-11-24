using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Une entrée de « À venir » ou de « Ce qui manque ».</summary>
public sealed class Entree
{
    /// <summary>Clé stable (pour « ignorer ») : film:123, serie:456:s3, serie:456:prochain…</summary>
    public string Cle { get; set; } = string.Empty;

    /// <summary>« film » ou « serie ».</summary>
    public string Type { get; set; } = string.Empty;

    public string Titre { get; set; } = string.Empty;

    /// <summary>Précision : « Saison 3 · épisode 4 », « 2 épisodes manquants »…</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Date de sortie (AAAA-MM-JJ), vide si inconnue.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>La date est celle de la sortie en France.</summary>
    public bool DateFrance { get; set; }

    public string Affiche { get; set; } = string.Empty;

    public int Tmdb { get; set; }

    /// <summary>Saga ou série d'où vient l'entrée.</summary>
    public string Groupe { get; set; } = string.Empty;

    /// <summary>Titre de ta médiathèque lié (série, ou film de la même saga) : identifiant Jellyfin.</summary>
    public string Lien { get; set; } = string.Empty;
}

/// <summary>Résultat calculé.</summary>
public sealed class DonneesAVenir
{
    public DateTime Maj { get; set; }

    public List<Entree> AVenir { get; set; } = new();

    public List<Entree> Manquants { get; set; } = new();
}

/// <summary>
/// « À venir » et « Ce qui manque » : films des sagas (collections TMDB) et saisons / épisodes des séries,
/// comparés à la médiathèque. Dates de sortie en France quand TMDB les connaît.
/// </summary>
public sealed class AVenir
{
    private const string Api = "https://api.themoviedb.org/3/";
    private const string Images = "https://image.tmdb.org/t/p/w342";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ILibraryManager _bibliotheque;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AVenir> _log;
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly object _ignores = new();
    private DonneesAVenir? _donnees;

    public AVenir(ILibraryManager bibliotheque, IHttpClientFactory http, ILogger<AVenir> log)
    {
        _bibliotheque = bibliotheque;
        _http = http;
        _log = log;
    }

    public bool EnCours { get; private set; }

    public string Etape { get; private set; } = string.Empty;

    private static string Dossier
    {
        get
        {
            var d = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "wolfflix");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public DonneesAVenir Donnees => _donnees ??= Lire<DonneesAVenir>("a-venir.json") ?? new DonneesAVenir();

    public HashSet<string> Ignores()
    {
        lock (_ignores)
        {
            return Lire<HashSet<string>>("ignores.json") ?? new HashSet<string>();
        }
    }

    public void Ignorer(string cle, bool oui)
    {
        lock (_ignores)
        {
            var s = Lire<HashSet<string>>("ignores.json") ?? new HashSet<string>();
            if (oui)
            {
                s.Add(cle);
            }
            else
            {
                s.Remove(cle);
            }

            Ecrire("ignores.json", s);
        }
    }

    public async Task MettreAJour(IProgress<double>? progression, CancellationToken annulation)
    {
        var cle = Plugin.Instance?.Configuration.TmdbCle;
        if (string.IsNullOrWhiteSpace(cle))
        {
            _log.LogWarning("WolfFlix : pas de clé TMDB, « À venir » ne peut pas être calculé.");
            return;
        }

        if (!await _verrou.WaitAsync(0, annulation).ConfigureAwait(false))
        {
            return;
        }

        EnCours = true;
        try
        {
            var client = _http.CreateClient("WolfFlix");
            client.Timeout = TimeSpan.FromSeconds(30);
            var aujourdhui = DateTime.UtcNow.Date;
            var d = new DonneesAVenir { Maj = DateTime.UtcNow };

            // ---------- Films : sagas (collections TMDB) ----------
            Etape = "Films";
            var films = _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true, IsVirtualItem = false });
            var parTmdb = new Dictionary<int, BaseItem>();
            foreach (var f in films)
            {
                if (int.TryParse(f.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
                {
                    parTmdb.TryAdd(t, f);
                }
            }

            // Saga de chaque film (gardée : seuls les nouveaux films sont demandés à TMDB)
            var sagaDe = Lire<Dictionary<int, int>>("films-sagas.json") ?? new Dictionary<int, int>();
            var aDemander = parTmdb.Keys.Where(k => !sagaDe.ContainsKey(k)).ToList();
            int fait = 0;
            await Parallele(aDemander, 6, async id =>
            {
                using var j = await Demander(client, $"movie/{id}", cle, annulation).ConfigureAwait(false);
                int c = 0;
                if (j != null && j.RootElement.TryGetProperty("belongs_to_collection", out var col) && col.ValueKind == JsonValueKind.Object
                    && col.TryGetProperty("id", out var cid))
                {
                    c = cid.GetInt32();
                }

                lock (sagaDe)
                {
                    sagaDe[id] = c;
                    fait++;
                }

                progression?.Report(40.0 * fait / Math.Max(1, aDemander.Count));
            }, annulation).ConfigureAwait(false);
            Ecrire("films-sagas.json", sagaDe);

            var collections = parTmdb.Keys.Select(k => sagaDe.TryGetValue(k, out var c) ? c : 0).Where(c => c > 0).Distinct().ToList();
            Etape = "Sagas";
            fait = 0;
            var entreesFilms = new List<(Entree E, bool Futur)>();
            await Parallele(collections, 6, async cid =>
            {
                using var j = await Demander(client, $"collection/{cid}", cle, annulation).ConfigureAwait(false);
                if (j == null || !j.RootElement.TryGetProperty("parts", out var parts))
                {
                    return;
                }

                var nom = Texte(j.RootElement, "name");
                var possedes = parts.EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).Where(parTmdb.ContainsKey).ToList();
                var lien = possedes.Count > 0 ? parTmdb[possedes[0]].Id.ToString("N") : string.Empty;
                foreach (var p in parts.EnumerateArray())
                {
                    var id = p.GetProperty("id").GetInt32();
                    if (parTmdb.ContainsKey(id))
                    {
                        continue;
                    }

                    var date = Texte(p, "release_date");
                    bool futur = string.IsNullOrEmpty(date) || DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) && dt.Date > aujourdhui;
                    var e = new Entree
                    {
                        Cle = "film:" + id,
                        Type = "film",
                        Titre = Texte(p, "title"),
                        Detail = "Saga " + nom,
                        Date = date,
                        Affiche = Affiche(p),
                        Tmdb = id,
                        Groupe = nom,
                        Lien = lien
                    };

                    // Date de sortie en France (cinéma, puis vidéo / streaming) pour ce qui sort ou vient de sortir
                    if (futur || (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dr) && dr > aujourdhui.AddYears(-1)))
                    {
                        var fr = await DateFrance(client, id, cle, annulation).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(fr))
                        {
                            e.Date = fr;
                            e.DateFrance = true;
                            futur = DateTime.TryParse(fr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dfr) && dfr.Date > aujourdhui;
                        }
                    }

                    lock (entreesFilms)
                    {
                        entreesFilms.Add((e, futur));
                    }
                }

                lock (entreesFilms)
                {
                    fait++;
                }

                progression?.Report(40 + (30.0 * fait / Math.Max(1, collections.Count)));
            }, annulation).ConfigureAwait(false);

            d.AVenir.AddRange(entreesFilms.Where(x => x.Futur).Select(x => x.E));
            d.Manquants.AddRange(entreesFilms.Where(x => !x.Futur).Select(x => x.E));

            // ---------- Séries ----------
            Etape = "Séries";
            var series = _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true });
            var episodes = _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, Recursive = true, IsVirtualItem = false });
            // Une même série TMDB peut exister plusieurs fois dans Jellyfin (deux bibliothèques, deux dossiers) :
            // on la traite une seule fois, avec les épisodes de toutes ses copies.
            var tmdbDeSerie = new Dictionary<Guid, int>();
            var seriesParTmdb = new Dictionary<int, List<BaseItem>>();
            foreach (var se in series)
            {
                if (int.TryParse(se.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
                {
                    tmdbDeSerie[se.Id] = t;
                    if (!seriesParTmdb.TryGetValue(t, out var l))
                    {
                        seriesParTmdb[t] = l = new List<BaseItem>();
                    }

                    l.Add(se);
                }
            }

            var possedesParSaison = new Dictionary<(int, int), HashSet<int>>();
            var totalPossede = new Dictionary<int, int>();
            foreach (var ep in episodes.OfType<Episode>())
            {
                var numSaison = ep.ParentIndexNumber ?? ep.Season?.IndexNumber;
                if (numSaison is not int s || ep.IndexNumber is not int n || !tmdbDeSerie.TryGetValue(ep.SeriesId, out var tmdbSerie))
                {
                    continue;
                }

                var k = (tmdbSerie, s);
                if (!possedesParSaison.TryGetValue(k, out var set))
                {
                    possedesParSaison[k] = set = new HashSet<int>();
                }

                if (set.Add(n) && s > 0)
                {
                    totalPossede[tmdbSerie] = totalPossede.GetValueOrDefault(tmdbSerie) + 1;
                }

                if (ep.IndexNumberEnd is int fin)
                {
                    for (int x = n + 1; x <= fin; x++)
                    {
                        if (set.Add(x) && s > 0)
                        {
                            totalPossede[tmdbSerie] = totalPossede.GetValueOrDefault(tmdbSerie) + 1;
                        }
                    }
                }
            }

            var entreesSeries = new List<(Entree E, bool Futur)>();
            fait = 0;
            await Parallele(seriesParTmdb.Keys.ToList(), 6, async tid =>
            {
                var serie = seriesParTmdb[tid][0];
                using var j = await Demander(client, $"tv/{tid}", cle, annulation).ConfigureAwait(false);
                if (j == null)
                {
                    return;
                }

                var r = j.RootElement;
                var nom = string.IsNullOrEmpty(serie.Name) ? Texte(r, "name") : serie.Name;
                var aff = Affiche(r);
                var lien = serie.Id.ToString("N");
                var liste = new List<(Entree, bool)>();
                int derniereS = 0, dernierE = 0;
                if (r.TryGetProperty("last_episode_to_air", out var le) && le.ValueKind == JsonValueKind.Object)
                {
                    derniereS = Entier(le, "season_number");
                    dernierE = Entier(le, "episode_number");
                }

                // Prochain épisode
                if (r.TryGetProperty("next_episode_to_air", out var ne) && ne.ValueKind == JsonValueKind.Object)
                {
                    int s = Entier(ne, "season_number"), n = Entier(ne, "episode_number");
                    var titreEp = Texte(ne, "name");
                    liste.Add((new Entree
                    {
                        Cle = $"serie:{tid}:s{s}e{n}",
                        Type = "serie",
                        Titre = nom,
                        Detail = $"Saison {s} · épisode {n}" + (string.IsNullOrEmpty(titreEp) || titreEp.StartsWith("Épisode", StringComparison.OrdinalIgnoreCase) ? string.Empty : " — " + titreEp),
                        Date = Texte(ne, "air_date"),
                        Affiche = aff,
                        Tmdb = tid,
                        Groupe = nom,
                        Lien = lien
                    }, true));
                }

                // Total des épisodes déjà diffusés (hors épisodes spéciaux) : si tu en as autant, la série est complète
                // même quand la numérotation de Jellyfin (TVDB, ordre DVD…) diffère de celle de TMDB.
                int totalAttendu = 0;
                if (r.TryGetProperty("seasons", out var toutes))
                {
                    foreach (var sa in toutes.EnumerateArray())
                    {
                        int s = Entier(sa, "season_number");
                        if (s > 0 && s <= derniereS)
                        {
                            totalAttendu += s < derniereS ? Entier(sa, "episode_count") : dernierE;
                        }
                    }
                }

                bool complete = totalAttendu > 0 && totalPossede.GetValueOrDefault(tid) >= totalAttendu;

                if (r.TryGetProperty("seasons", out var saisons))
                {
                    foreach (var sa in saisons.EnumerateArray())
                    {
                        int s = Entier(sa, "season_number");
                        if (s <= 0)
                        {
                            continue;
                        }

                        int nb = Entier(sa, "episode_count");
                        var date = Texte(sa, "air_date");

                        // Saison annoncée, pas encore diffusée
                        if (s > derniereS)
                        {
                            bool dejaProchain = liste.Any(x => x.Item1.Cle.StartsWith($"serie:{tid}:s{s}e", StringComparison.Ordinal));
                            if (!dejaProchain)
                            {
                                liste.Add((new Entree
                                {
                                    Cle = $"serie:{tid}:s{s}",
                                    Type = "serie",
                                    Titre = nom,
                                    Detail = $"Saison {s}" + (nb > 0 ? $" · {nb} épisodes" : string.Empty),
                                    Date = date,
                                    Affiche = string.IsNullOrEmpty(Affiche(sa)) ? aff : Affiche(sa),
                                    Tmdb = tid,
                                    Groupe = nom,
                                    Lien = lien
                                }, true));
                            }

                            continue;
                        }

                        // Épisodes déjà diffusés qui manquent chez toi
                        if (complete)
                        {
                            continue;
                        }

                        int attendus = s < derniereS ? nb : dernierE;
                        possedesParSaison.TryGetValue((tid, s), out var possedes);
                        int manque = Enumerable.Range(1, Math.Max(0, attendus)).Count(n => possedes == null || !possedes.Contains(n));

                        // Autant d'épisodes que prévu mais numérotés autrement : la saison est complète
                        if (possedes != null && possedes.Count >= attendus)
                        {
                            manque = 0;
                        }

                        if (manque > 0)
                        {
                            liste.Add((new Entree
                            {
                                Cle = $"serie:{tid}:manque:s{s}",
                                Type = "serie",
                                Titre = nom,
                                Detail = possedes == null || possedes.Count == 0
                                    ? $"Saison {s} entière ({attendus} épisodes)"
                                    : $"Saison {s} : {manque} épisode{(manque > 1 ? "s" : string.Empty)} manquant{(manque > 1 ? "s" : string.Empty)} sur {attendus}",
                                Date = date,
                                Affiche = string.IsNullOrEmpty(Affiche(sa)) ? aff : Affiche(sa),
                                Tmdb = tid,
                                Groupe = nom,
                                Lien = lien
                            }, false));
                        }
                    }
                }

                lock (entreesSeries)
                {
                    entreesSeries.AddRange(liste);
                    fait++;
                }

                progression?.Report(70 + (30.0 * fait / Math.Max(1, seriesParTmdb.Count)));
            }, annulation).ConfigureAwait(false);

            d.AVenir.AddRange(entreesSeries.Where(x => x.Futur).Select(x => x.E));
            d.Manquants.AddRange(entreesSeries.Where(x => !x.Futur).Select(x => x.E));

            // Sans doublon (même clé), au cas où
            d.AVenir = d.AVenir.GroupBy(e => e.Cle).Select(g => g.First()).ToList();
            d.Manquants = d.Manquants.GroupBy(e => e.Cle).Select(g => g.First()).ToList();

            // Tri : par date (inconnues à la fin pour « À venir »), par groupe pour « Ce qui manque »
            d.AVenir = d.AVenir.OrderBy(e => string.IsNullOrEmpty(e.Date) ? "9999" : e.Date, StringComparer.Ordinal).ThenBy(e => e.Titre, StringComparer.CurrentCulture).ToList();
            d.Manquants = d.Manquants.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Groupe, StringComparer.CurrentCulture).ThenBy(e => e.Date, StringComparer.Ordinal).ToList();

            Ecrire("a-venir.json", d);
            _donnees = d;
            progression?.Report(100);
            _log.LogInformation("WolfFlix : à venir {AVenir}, manquants {Manquants}", d.AVenir.Count, d.Manquants.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WolfFlix : échec de « À venir » ({Etape})", Etape);
            throw;
        }
        finally
        {
            Etape = string.Empty;
            EnCours = false;
            _verrou.Release();
        }
    }

    private async Task<string> DateFrance(HttpClient client, int film, string cle, CancellationToken annulation)
    {
        using var j = await Demander(client, $"movie/{film}/release_dates", cle, annulation, false).ConfigureAwait(false);
        if (j == null || !j.RootElement.TryGetProperty("results", out var res))
        {
            return string.Empty;
        }

        foreach (var pays in res.EnumerateArray())
        {
            if (Texte(pays, "iso_3166_1") != "FR" || !pays.TryGetProperty("release_dates", out var dates))
            {
                continue;
            }

            // 3 = cinéma, 4 = numérique, 5 = physique ; la première sortie française compte
            var liste = dates.EnumerateArray()
                .Where(x => Entier(x, "type") is 3 or 4 or 5 or 2)
                .Select(x => Texte(x, "release_date"))
                .Where(x => x.Length >= 10)
                .Select(x => x[..10])
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return liste.FirstOrDefault() ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task Parallele<T>(IList<T> elements, int largeur, Func<T, Task> action, CancellationToken annulation)
    {
        using var porte = new SemaphoreSlim(largeur);
        var taches = elements.Select(async e =>
        {
            await porte.WaitAsync(annulation).ConfigureAwait(false);
            try
            {
                await action(e).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // un titre en erreur n'arrête pas les autres
            }
            finally
            {
                porte.Release();
            }
        });
        await Task.WhenAll(taches).ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> Demander(HttpClient client, string chemin, string cle, CancellationToken annulation, bool francais = true)
    {
        var url = Api + chemin + "?api_key=" + Uri.EscapeDataString(cle) + (francais ? "&language=fr-FR" : string.Empty);
        for (int essai = 0; essai < 3; essai++)
        {
            using var r = await client.GetAsync(url, annulation).ConfigureAwait(false);
            if ((int)r.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 + essai * 2), annulation).ConfigureAwait(false);
                continue;
            }

            if (!r.IsSuccessStatusCode)
            {
                return null;
            }

            await using var flux = await r.Content.ReadAsStreamAsync(annulation).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(flux, default, annulation).ConfigureAwait(false);
        }

        return null;
    }

    private static string Texte(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Entier(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string Affiche(JsonElement e)
    {
        var p = Texte(e, "poster_path");
        return string.IsNullOrEmpty(p) ? string.Empty : Images + p;
    }

    private static T? Lire<T>(string nom)
        where T : class
    {
        try
        {
            var p = Path.Combine(Dossier, nom);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Ecrire<T>(string nom, T valeur)
    {
        var p = Path.Combine(Dossier, nom);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(valeur, Json));
        File.Move(tmp, p, true);
    }
}
