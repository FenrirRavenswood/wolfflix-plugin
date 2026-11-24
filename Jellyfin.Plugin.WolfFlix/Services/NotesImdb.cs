using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Une note IMDb : moyenne, nombre de votes, place dans le classement global.</summary>
public sealed class NoteImdb
{
    public double Note { get; set; }

    public int Votes { get; set; }

    /// <summary>Place parmi tous les titres IMDb du même type (0 = sous le seuil de votes).</summary>
    public int Rang { get; set; }

    public int Total { get; set; }

    /// <summary>"s" = série, "m" = film.</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>Note d'un épisode : saison, numéro, note, votes.</summary>
public sealed class NoteEpisode
{
    public int S { get; set; }

    public int E { get; set; }

    public double? N { get; set; }

    public int V { get; set; }
}

/// <summary>Tout ce que l'extension garde : seulement les titres de ta médiathèque.</summary>
public sealed class DonneesNotes
{
    public DateTime Maj { get; set; }

    public Dictionary<string, NoteImdb> Titres { get; set; } = new();

    public Dictionary<string, List<NoteEpisode>> Episodes { get; set; } = new();
}

/// <summary>
/// Notes IMDb : lit chaque jour les fichiers publics d'IMDb (datasets.imdbws.com) au fil du téléchargement,
/// sans les enregistrer, et ne garde que les titres de la médiathèque + leur place dans le classement.
/// </summary>
public sealed class NotesImdb
{
    private const string Base = "https://datasets.imdbws.com/";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ILibraryManager _bibliotheque;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NotesImdb> _log;
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private DonneesNotes? _donnees;

    public NotesImdb(ILibraryManager bibliotheque, IHttpClientFactory http, ILogger<NotesImdb> log)
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

    /// <summary>Données actuelles (lues sur le disque au premier appel).</summary>
    public DonneesNotes Donnees
    {
        get
        {
            if (_donnees != null)
            {
                return _donnees;
            }

            _donnees = Lire<DonneesNotes>("notes-imdb.json") ?? new DonneesNotes();
            return _donnees;
        }
    }

    /// <summary>Met à jour les notes. Les gros fichiers (types, épisodes) ne sont relus qu'une fois par semaine.</summary>
    public async Task MettreAJour(IProgress<double>? progression, CancellationToken annulation)
    {
        if (!await _verrou.WaitAsync(0, annulation).ConfigureAwait(false))
        {
            return; // déjà en cours
        }

        EnCours = true;
        try
        {
            var cfg = Plugin.Instance?.Configuration;
            int minSeries = Math.Max(1, cfg?.VotesMinSeries ?? 5000);
            int minFilms = Math.Max(1, cfg?.VotesMinFilms ?? 25000);
            int minTous = Math.Min(minSeries, minFilms);

            // 1. Titres de la médiathèque qui ont un identifiant IMDb
            Etape = "Médiathèque";
            var films = new HashSet<string>(StringComparer.Ordinal);
            var series = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in _bibliotheque.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            }))
            {
                var imdb = item.GetProviderId(MetadataProvider.Imdb);
                if (string.IsNullOrEmpty(imdb))
                {
                    continue;
                }

                if (item.GetBaseItemKind() == BaseItemKind.Series)
                {
                    series.Add(imdb);
                }
                else
                {
                    films.Add(imdb);
                }
            }

            progression?.Report(5);

            // 2. Épisodes des séries (une fois par semaine, ou dès qu'une nouvelle série arrive)
            var carte = Lire<CarteEpisodes>("episodes-imdb.json");
            if (carte == null || DateTime.UtcNow - carte.Maj > TimeSpan.FromDays(7) || series.Any(s => !carte.Series.ContainsKey(s)))
            {
                Etape = "Épisodes IMDb";
                carte = await LireEpisodes(series, annulation).ConfigureAwait(false);
                Ecrire("episodes-imdb.json", carte);
            }

            progression?.Report(35);

            var idsEpisodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in carte.Series.Values)
            {
                foreach (var e in l)
                {
                    idsEpisodes.Add(e.T);
                }
            }

            // 3. Notes du jour
            Etape = "Notes IMDb";
            var notes = new Dictionary<string, (double Note, int Votes)>(StringComparer.Ordinal);
            await foreach (var c in Lignes("title.ratings.tsv.gz", annulation).ConfigureAwait(false))
            {
                if (c.Length < 3 || !int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes)
                    || !double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var note))
                {
                    continue;
                }

                if (votes >= minTous || films.Contains(c[0]) || series.Contains(c[0]) || idsEpisodes.Contains(c[0]))
                {
                    notes[c[0]] = (note, votes);
                }
            }

            progression?.Report(60);

            // 4. Type (film ou série) des titres classables : une fois par semaine
            var types = Lire<TypesTitres>("types-imdb.json");
            if (types == null || DateTime.UtcNow - types.Maj > TimeSpan.FromDays(7) || types.Min > minTous)
            {
                Etape = "Types IMDb";
                types = new TypesTitres { Maj = DateTime.UtcNow, Min = minTous };
                await foreach (var c in Lignes("title.basics.tsv.gz", annulation).ConfigureAwait(false))
                {
                    if (c.Length < 2 || !notes.TryGetValue(c[0], out var n) || n.Votes < minTous)
                    {
                        continue;
                    }

                    if (c[1] == "movie")
                    {
                        types.Ids[c[0]] = "m";
                    }
                    else if (c[1] == "tvSeries" || c[1] == "tvMiniSeries")
                    {
                        types.Ids[c[0]] = "s";
                    }
                }

                Ecrire("types-imdb.json", types);
            }

            progression?.Report(90);

            // 5. Classements globaux
            Etape = "Classements";
            var classeS = Classement(notes, types, "s", minSeries);
            var classeM = Classement(notes, types, "m", minFilms);

            var d = new DonneesNotes { Maj = DateTime.UtcNow };
            foreach (var id in series)
            {
                if (notes.TryGetValue(id, out var n))
                {
                    d.Titres[id] = new NoteImdb { Note = n.Note, Votes = n.Votes, Type = "s", Rang = classeS.Rang(id), Total = classeS.Total };
                }

                if (carte.Series.TryGetValue(id, out var eps))
                {
                    d.Episodes[id] = eps.Select(e =>
                    {
                        var ok = notes.TryGetValue(e.T, out var ne);
                        return new NoteEpisode { S = e.S, E = e.E, N = ok ? ne.Note : null, V = ok ? ne.Votes : 0 };
                    }).OrderBy(e => e.S).ThenBy(e => e.E).ToList();
                }
            }

            foreach (var id in films)
            {
                if (notes.TryGetValue(id, out var n))
                {
                    d.Titres[id] = new NoteImdb { Note = n.Note, Votes = n.Votes, Type = "m", Rang = classeM.Rang(id), Total = classeM.Total };
                }
            }

            Ecrire("notes-imdb.json", d);
            _donnees = d;
            progression?.Report(100);
            _log.LogInformation("WolfFlix : notes IMDb mises à jour ({Titres} titres, {Series} séries avec épisodes)", d.Titres.Count, d.Episodes.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WolfFlix : échec de la mise à jour des notes IMDb ({Etape})", Etape);
            throw;
        }
        finally
        {
            Etape = string.Empty;
            EnCours = false;
            _verrou.Release();
        }
    }

    private async Task<CarteEpisodes> LireEpisodes(HashSet<string> series, CancellationToken annulation)
    {
        var carte = new CarteEpisodes { Maj = DateTime.UtcNow };
        foreach (var s in series)
        {
            carte.Series[s] = new List<EpisodeImdb>();
        }

        await foreach (var c in Lignes("title.episode.tsv.gz", annulation).ConfigureAwait(false))
        {
            if (c.Length < 4 || !carte.Series.TryGetValue(c[1], out var liste))
            {
                continue;
            }

            if (int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                && int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
            {
                liste.Add(new EpisodeImdb { T = c[0], S = s, E = e });
            }
        }

        return carte;
    }

    /// <summary>Lit un fichier IMDb compressé ligne par ligne pendant le téléchargement.</summary>
    private async IAsyncEnumerable<string[]> Lignes(string fichier, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken annulation)
    {
        var client = _http.CreateClient("WolfFlix");
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WolfFlix-Jellyfin/0.1");
        using var reponse = await client.GetAsync(Base + fichier, HttpCompletionOption.ResponseHeadersRead, annulation).ConfigureAwait(false);
        reponse.EnsureSuccessStatusCode();
        await using var flux = await reponse.Content.ReadAsStreamAsync(annulation).ConfigureAwait(false);
        await using var gz = new GZipStream(flux, CompressionMode.Decompress);
        using var lecteur = new StreamReader(gz);
        await lecteur.ReadLineAsync(annulation).ConfigureAwait(false); // en-tête
        string? ligne;
        while ((ligne = await lecteur.ReadLineAsync(annulation).ConfigureAwait(false)) != null)
        {
            yield return ligne.Split('\t');
        }
    }

    private static Classe Classement(Dictionary<string, (double Note, int Votes)> notes, TypesTitres types, string type, int min)
    {
        var liste = notes.Where(kv => kv.Value.Votes >= min && types.Ids.TryGetValue(kv.Key, out var t) && t == type)
            .OrderByDescending(kv => kv.Value.Note).ThenByDescending(kv => kv.Value.Votes)
            .Select(kv => kv.Key).ToList();
        var rangs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < liste.Count; i++)
        {
            rangs[liste[i]] = i + 1;
        }

        return new Classe(rangs, liste.Count);
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

    private sealed record Classe(Dictionary<string, int> Rangs, int Total)
    {
        public int Rang(string id) => Rangs.TryGetValue(id, out var r) ? r : 0;
    }

    /// <summary>Épisodes IMDb des séries de la médiathèque.</summary>
    public sealed class CarteEpisodes
    {
        public DateTime Maj { get; set; }

        public Dictionary<string, List<EpisodeImdb>> Series { get; set; } = new();
    }

    /// <summary>Un épisode IMDb.</summary>
    public sealed class EpisodeImdb
    {
        public string T { get; set; } = string.Empty;

        public int S { get; set; }

        public int E { get; set; }
    }

    /// <summary>Type des titres assez votés pour être classés.</summary>
    public sealed class TypesTitres
    {
        public DateTime Maj { get; set; }

        public int Min { get; set; }

        public Dictionary<string, string> Ids { get; set; } = new();
    }
}
