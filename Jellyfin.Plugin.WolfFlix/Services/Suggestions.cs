using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Un titre de la bibliothèque proposé dans une saga ou un cycle.</summary>
public sealed class TitrePropose
{
    public string Id { get; set; } = string.Empty;

    public string Nom { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int? Annee { get; set; }

    public string Date { get; set; } = string.Empty;

    /// <summary>Déjà dans la collection existante.</summary>
    public bool Dedans { get; set; }
}

/// <summary>Une saga (films reliés sur TMDB) ou un cycle (même univers sur Wikidata) possible.</summary>
public sealed class Proposition
{
    public string Cle { get; set; } = string.Empty;

    public string Nom { get; set; } = string.Empty;

    /// <summary>D'où vient le lien : « TMDB » ou « Wikidata ».</summary>
    public string Source { get; set; } = string.Empty;

    public List<TitrePropose> Titres { get; set; } = new();

    /// <summary>Collection Jellyfin qui contient déjà une partie de ces titres (à compléter), sinon vide.</summary>
    public string ExistanteId { get; set; } = string.Empty;

    public string ExistanteNom { get; set; } = string.Empty;
}

public sealed class DonneesSuggestions
{
    public DateTime Maj { get; set; }

    public List<Proposition> Sagas { get; set; } = new();

    public List<Proposition> Cycles { get; set; } = new();
}

/// <summary>Univers d'un titre selon Wikidata (gardé pour ne pas redemander).</summary>
public sealed class UniversWikidata
{
    public DateTime Date { get; set; }

    /// <summary>Identifiant Wikidata de l'univers → nom.</summary>
    public Dictionary<string, string> Univers { get; set; } = new();
}

/// <summary>
/// Propositions de sagas et de cycles pour l'administrateur :
///   · sagas = films de la bibliothèque qui appartiennent à la même collection TMDB ;
///   · cycles = séries (et films) qui se passent dans le même univers ou la même franchise d'après Wikidata
///     (propriétés P1434 « univers de fiction », P8345 « franchise », P179 « fait partie de la série »).
/// Les groupes déjà entièrement rangés dans une collection Jellyfin ne sont pas proposés ;
/// ceux qui le sont en partie sont proposés « à compléter ».
/// </summary>
public sealed class Suggestions
{
    private const string Sparql = "https://query.wikidata.org/sparql";
    private readonly ILibraryManager _bibliotheque;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Suggestions> _log;
    private readonly SemaphoreSlim _verrou = new(1, 1);

    public Suggestions(ILibraryManager bibliotheque, IHttpClientFactory http, ILogger<Suggestions> log)
    {
        _bibliotheque = bibliotheque;
        _http = http;
        _log = log;
    }

    public bool EnCours { get; private set; }

    public string Etape { get; private set; } = string.Empty;

    public DonneesSuggestions? Donnees => AVenir.Lire<DonneesSuggestions>("suggestions.json");

    /// <summary>Lance le calcul en arrière-plan (sans attendre).</summary>
    public void Demarrer()
    {
        if (EnCours)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Calculer(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WolfFlix : calcul des sagas et cycles possibles impossible");
            }
        });
    }

    public async Task Calculer(CancellationToken annulation)
    {
        if (!await _verrou.WaitAsync(0, annulation).ConfigureAwait(false))
        {
            return;
        }

        EnCours = true;
        try
        {
            var d = new DonneesSuggestions { Maj = DateTime.UtcNow };
            var couverts = Collections();

            Etape = "Sagas (TMDB)";
            d.Sagas = await Sagas(couverts, annulation).ConfigureAwait(false);

            Etape = "Cycles (Wikidata)";
            d.Cycles = await Cycles(couverts, annulation).ConfigureAwait(false);

            AVenir.Ecrire("suggestions.json", d);
        }
        finally
        {
            Etape = string.Empty;
            EnCours = false;
            _verrou.Release();
        }
    }

    /// <summary>Collections Jellyfin existantes : id → (nom, titres couverts, séries comprises).</summary>
    private List<(string Id, string Nom, HashSet<Guid> Ids)> Collections()
    {
        var res = new List<(string, string, HashSet<Guid>)>();
        foreach (var b in _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.BoxSet }, Recursive = true }).OfType<BoxSet>())
        {
            var ids = new HashSet<Guid>();
            foreach (var e in b.GetLinkedChildren())
            {
                ids.Add(e.Id);
                switch (e)
                {
                    case Season s:
                        ids.Add(s.SeriesId);
                        break;
                    case Episode ep:
                        ids.Add(ep.SeriesId);
                        break;
                }
            }

            res.Add((b.Id.ToString("N"), b.Name ?? string.Empty, ids));
        }

        return res;
    }

    private static Proposition? Construire(string cle, string nom, string source, List<BaseItem> titres, List<(string Id, string Nom, HashSet<Guid> Ids)> couverts)
    {
        if (titres.Count < 2)
        {
            return null;
        }

        // La collection qui en contient le plus
        var meilleure = couverts
            .Select(c => (c.Id, c.Nom, N: titres.Count(t => c.Ids.Contains(t.Id))))
            .OrderByDescending(x => x.N)
            .FirstOrDefault();
        if (meilleure.N == titres.Count)
        {
            return null; // déjà rangé
        }

        var dans = meilleure.N > 0 ? couverts.First(c => c.Id == meilleure.Id).Ids : new HashSet<Guid>();
        return new Proposition
        {
            Cle = cle,
            Nom = nom,
            Source = source,
            ExistanteId = meilleure.N > 0 ? meilleure.Id : string.Empty,
            ExistanteNom = meilleure.N > 0 ? meilleure.Nom : string.Empty,
            Titres = titres
                .OrderBy(t => t.PremiereDate ?? (t.ProductionYear is int a ? new DateTime(a, 1, 1) : DateTime.MaxValue))
                .ThenBy(t => t.SortName)
                .Select(t => new TitrePropose
                {
                    Id = t.Id.ToString("N"),
                    Nom = t.Name ?? string.Empty,
                    Type = t is Series ? "Series" : "Movie",
                    Annee = t.ProductionYear,
                    Date = t.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    Dedans = dans.Contains(t.Id)
                }).ToList()
        };
    }

    // ------------------------------------------------------------------ sagas : collections TMDB
    private async Task<List<Proposition>> Sagas(List<(string Id, string Nom, HashSet<Guid> Ids)> couverts, CancellationToken annulation)
    {
        var res = new List<Proposition>();
        var cle = Plugin.Instance?.Configuration.TmdbCle;
        if (string.IsNullOrWhiteSpace(cle))
        {
            return res;
        }

        var films = _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true, IsVirtualItem = false });
        var parTmdb = new Dictionary<int, BaseItem>();
        foreach (var f in films)
        {
            if (int.TryParse(f.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
            {
                parTmdb.TryAdd(t, f);
            }
        }

        var client = _http.CreateClient("WolfFlix");
        client.Timeout = TimeSpan.FromSeconds(30);
        var sagaDe = AVenir.Lire<Dictionary<int, int>>("films-sagas.json") ?? new Dictionary<int, int>();
        var aDemander = parTmdb.Keys.Where(k => !sagaDe.ContainsKey(k)).ToList();
        await AVenir.Parallele(aDemander, 6, async id =>
        {
            using var j = await AVenir.Demander(client, $"movie/{id}", cle, annulation).ConfigureAwait(false);
            int c = 0;
            if (j != null && j.RootElement.TryGetProperty("belongs_to_collection", out var col) && col.ValueKind == JsonValueKind.Object
                && col.TryGetProperty("id", out var cid))
            {
                c = cid.GetInt32();
            }

            lock (sagaDe)
            {
                sagaDe[id] = c;
            }
        }, annulation).ConfigureAwait(false);
        AVenir.Ecrire("films-sagas.json", sagaDe);

        var groupes = parTmdb.Where(p => sagaDe.TryGetValue(p.Key, out var c) && c > 0)
            .GroupBy(p => sagaDe[p.Key])
            .Where(g => g.Count() >= 2)
            .ToList();
        var noms = AVenir.Lire<Dictionary<int, string>>("collections-noms.json") ?? new Dictionary<int, string>();
        await AVenir.Parallele(groupes.Select(g => g.Key).Where(k => !noms.ContainsKey(k)).ToList(), 6, async cid =>
        {
            using var j = await AVenir.Demander(client, $"collection/{cid}", cle, annulation).ConfigureAwait(false);
            var n = j != null ? AVenir.Texte(j.RootElement, "name") : string.Empty;
            lock (noms)
            {
                noms[cid] = n;
            }
        }, annulation).ConfigureAwait(false);
        AVenir.Ecrire("collections-noms.json", noms);

        foreach (var g in groupes)
        {
            var nom = noms.TryGetValue(g.Key, out var n) && !string.IsNullOrEmpty(n) ? n : g.First().Value.Name ?? "Saga";
            nom = nom.Replace(" - Saga", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" - Collection", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var p = Construire("tmdb:" + g.Key, nom, "TMDB", g.Select(x => x.Value).ToList(), couverts);
            if (p != null)
            {
                res.Add(p);
            }
        }

        return res.OrderBy(p => p.ExistanteId.Length == 0).ThenByDescending(p => p.Titres.Count).ThenBy(p => p.Nom).ToList();
    }

    // ------------------------------------------------------------------ cycles : univers Wikidata
    private async Task<List<Proposition>> Cycles(List<(string Id, string Nom, HashSet<Guid> Ids)> couverts, CancellationToken annulation)
    {
        var res = new List<Proposition>();
        var titres = _bibliotheque.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie }, Recursive = true, IsVirtualItem = false });
        var parImdb = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in titres)
        {
            var imdb = t.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrEmpty(imdb) && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                parImdb.TryAdd(imdb, t);
            }
        }

        var cache = AVenir.Lire<Dictionary<string, UniversWikidata>>("univers-wikidata.json") ?? new Dictionary<string, UniversWikidata>(StringComparer.OrdinalIgnoreCase);
        var limite = DateTime.UtcNow.AddDays(-30);
        var aDemander = parImdb.Keys.Where(k => !cache.TryGetValue(k, out var u) || u.Date < limite).ToList();
        var client = _http.CreateClient("WolfFlix");
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WolfFlix-Jellyfin/1.0 (plugin personnel)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
        for (int i = 0; i < aDemander.Count; i += 150)
        {
            annulation.ThrowIfCancellationRequested();
            var lot = aDemander.Skip(i).Take(150).ToList();
            var trouve = await Univers(client, lot, annulation).ConfigureAwait(false);
            if (trouve == null)
            {
                continue; // Wikidata indisponible : on réessaiera au prochain calcul
            }

            foreach (var imdb in lot)
            {
                cache[imdb] = new UniversWikidata { Date = DateTime.UtcNow, Univers = trouve.TryGetValue(imdb, out var u) ? u : new Dictionary<string, string>() };
            }

            await Task.Delay(500, annulation).ConfigureAwait(false);
        }

        AVenir.Ecrire("univers-wikidata.json", cache);

        // Regroupement : un univers avec au moins 2 titres dont au moins une série
        var parUnivers = new Dictionary<string, (string Nom, List<BaseItem> Titres)>();
        foreach (var (imdb, item) in parImdb)
        {
            if (!cache.TryGetValue(imdb, out var u))
            {
                continue;
            }

            foreach (var (qid, nom) in u.Univers)
            {
                if (!parUnivers.TryGetValue(qid, out var g))
                {
                    g = (nom, new List<BaseItem>());
                    parUnivers[qid] = g;
                }

                if (!g.Titres.Contains(item))
                {
                    g.Titres.Add(item);
                }
            }
        }

        foreach (var (qid, g) in parUnivers)
        {
            if (!g.Titres.OfType<Series>().Any() || g.Titres.Count < 2 || string.IsNullOrWhiteSpace(g.Nom) || System.Text.RegularExpressions.Regex.IsMatch(g.Nom, @"^Q\d+$"))
            {
                continue;
            }

            var p = Construire("wd:" + qid, g.Nom, "Wikidata", g.Titres, couverts);
            if (p != null)
            {
                res.Add(p);
            }
        }

        // Un univers et sa sous-partie peuvent proposer exactement les mêmes titres : on garde le plus grand nom une fois
        res = res.GroupBy(p => string.Join(',', p.Titres.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal)))
            .Select(g => g.OrderByDescending(p => p.Titres.Count).First())
            .ToList();
        return res.OrderBy(p => p.ExistanteId.Length == 0).ThenByDescending(p => p.Titres.Count).ThenBy(p => p.Nom).ToList();
    }

    /// <summary>imdb → (identifiant Wikidata de l'univers → nom en français), ou null si Wikidata ne répond pas.</summary>
    private async Task<Dictionary<string, Dictionary<string, string>>?> Univers(HttpClient client, List<string> imdbs, CancellationToken annulation)
    {
        var valeurs = string.Join(' ', imdbs.Select(i => "\"" + i.Replace("\"", string.Empty, StringComparison.Ordinal) + "\""));
        var requete = "SELECT ?imdb ?u ?uLabel WHERE { VALUES ?imdb { " + valeurs + " } ?item wdt:P345 ?imdb . " +
                      "{ ?item wdt:P1434 ?u . } UNION { ?item wdt:P8345 ?u . } UNION { ?item wdt:P179 ?u . } " +
                      "SERVICE wikibase:label { bd:serviceParam wikibase:language \"fr,en\". } }";
        try
        {
            using var corps = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", requete) });
            using var r = await client.PostAsync(Sparql, corps, annulation).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                _log.LogInformation("Wikidata : réponse {Code}", (int)r.StatusCode);
                return null;
            }

            await using var flux = await r.Content.ReadAsStreamAsync(annulation).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(flux, default, annulation).ConfigureAwait(false);
            var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in doc.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
            {
                var imdb = b.GetProperty("imdb").GetProperty("value").GetString() ?? string.Empty;
                var u = b.GetProperty("u").GetProperty("value").GetString() ?? string.Empty;
                var qid = u[(u.LastIndexOf('/') + 1)..];
                var nom = b.TryGetProperty("uLabel", out var l) ? l.GetProperty("value").GetString() ?? qid : qid;
                if (!res.TryGetValue(imdb, out var d))
                {
                    d = new Dictionary<string, string>();
                    res[imdb] = d;
                }

                d[qid] = nom;
            }

            return res;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Wikidata : requête impossible");
            return null;
        }
    }
}
