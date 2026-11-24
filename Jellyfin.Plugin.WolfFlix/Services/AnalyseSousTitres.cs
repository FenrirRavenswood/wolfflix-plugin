using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Résultat de l'analyse d'une piste de sous-titres.</summary>
public sealed class PisteAnalysee
{
    /// <summary>Nombre de répliques (paquets) de la piste.</summary>
    public int Repliques { get; set; }

    /// <summary>« force », « complet » ou « sdh ».</summary>
    public string Classe { get; set; } = string.Empty;
}

/// <summary>Analyses gardées pour un fichier vidéo (refaites si le fichier change).</summary>
public sealed class FichierAnalyse
{
    public long Taille { get; set; }

    public DateTime Modifie { get; set; }

    public Dictionary<int, PisteAnalysee> Pistes { get; set; } = new();
}

/// <summary>
/// Sous-titres français : « forcé » ou « complet », d'après le nombre de répliques.
/// Un sous-titre forcé n'a que quelques dizaines de répliques (panneaux, passages en langue étrangère),
/// un complet en a des centaines. Compté avec ffprobe (celui de Jellyfin), sans modifier les fichiers.
/// </summary>
public sealed partial class AnalyseSousTitres
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ILibraryManager _bibliotheque;
    private readonly IMediaSourceManager _sources;
    private readonly IMediaEncoder _encodeur;
    private readonly ILogger<AnalyseSousTitres> _log;
    private readonly object _ecriture = new();
    private readonly ConcurrentDictionary<Guid, byte> _demandes = new();
    private ConcurrentDictionary<string, FichierAnalyse>? _cache;

    public AnalyseSousTitres(ILibraryManager bibliotheque, IMediaSourceManager sources, IMediaEncoder encodeur, ILogger<AnalyseSousTitres> log)
    {
        _bibliotheque = bibliotheque;
        _sources = sources;
        _encodeur = encodeur;
        _log = log;
    }

    public bool EnCours { get; private set; }

    public int Faits { get; private set; }

    public int AFaire { get; private set; }

    private static string Chemin
    {
        get
        {
            var d = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "wolfflix");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "sous-titres.json");
        }
    }

    private ConcurrentDictionary<string, FichierAnalyse> Cache
    {
        get
        {
            if (_cache != null)
            {
                return _cache;
            }

            try
            {
                var d = File.Exists(Chemin) ? JsonSerializer.Deserialize<Dictionary<string, FichierAnalyse>>(File.ReadAllText(Chemin), Json) : null;
                _cache = new ConcurrentDictionary<string, FichierAnalyse>(d ?? new Dictionary<string, FichierAnalyse>());
            }
            catch (Exception)
            {
                _cache = new ConcurrentDictionary<string, FichierAnalyse>();
            }

            return _cache;
        }
    }

    public int NbAnalyses => Cache.Count;

    [GeneratedRegex(@"forc[ée]|forced|\bsigns?\b|panneaux", RegexOptions.IgnoreCase)]
    private static partial Regex NomForce();

    [GeneratedRegex(@"\bsdh\b|\bcc\b|malentendant|sourds|hearing", RegexOptions.IgnoreCase)]
    private static partial Regex NomSdh();

    [GeneratedRegex(@"fran[cç]ais|french|\bvf[fqi]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex NomFrancais();

    private static bool Francais(MediaStream s)
    {
        var l = (s.Language ?? string.Empty).ToLowerInvariant();
        return l is "fre" or "fra" or "fr" || l.StartsWith("fr-", StringComparison.Ordinal) || NomFrancais().IsMatch((s.Title ?? string.Empty) + " " + (s.DisplayTitle ?? string.Empty));
    }

    /// <summary>Résultats connus pour une vidéo (sans lancer d'analyse).</summary>
    public Dictionary<int, PisteAnalysee> Resultats(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path) || !Cache.TryGetValue(item.Path, out var f) || !AJour(item.Path, f))
        {
            return new Dictionary<int, PisteAnalysee>();
        }

        return f.Pistes;
    }

    /// <summary>Demande l'analyse d'une vidéo en arrière-plan (une seule à la fois par vidéo).</summary>
    public void Demander(BaseItem item)
    {
        if (!_demandes.TryAdd(item.Id, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AnalyserItem(item, CancellationToken.None).ConfigureAwait(false);
                Sauver();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "WolfFlix : analyse des sous-titres impossible pour {Nom}", item.Name);
            }
            finally
            {
                _demandes.TryRemove(item.Id, out _);
            }
        });
    }

    /// <summary>Analyse toute la médiathèque, en commençant par les vidéos jamais analysées. S'arrête après la durée donnée.</summary>
    public async Task ToutAnalyser(IProgress<double>? progression, TimeSpan duree, CancellationToken annulation)
    {
        if (EnCours)
        {
            return;
        }

        EnCours = true;
        var fin = DateTime.UtcNow + duree;
        try
        {
            var videos = _bibliotheque.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            }).Where(i => !string.IsNullOrEmpty(i.Path) && (!Cache.TryGetValue(i.Path, out var f) || !AJour(i.Path, f))).ToList();

            AFaire = videos.Count;
            Faits = 0;
            var dernierSauvegarde = DateTime.UtcNow;
            foreach (var v in videos)
            {
                annulation.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > fin)
                {
                    break;
                }

                try
                {
                    await AnalyserItem(v, annulation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "WolfFlix : analyse impossible pour {Nom}", v.Name);
                }

                Faits++;
                progression?.Report(100.0 * Faits / Math.Max(1, AFaire));
                if (DateTime.UtcNow - dernierSauvegarde > TimeSpan.FromMinutes(2))
                {
                    Sauver();
                    dernierSauvegarde = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            Sauver();
            EnCours = false;
        }
    }

    private static bool AJour(string chemin, FichierAnalyse f)
    {
        try
        {
            var i = new FileInfo(chemin);
            return i.Exists && i.Length == f.Taille && i.LastWriteTimeUtc == f.Modifie;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task AnalyserItem(BaseItem item, CancellationToken annulation)
    {
        var chemin = item.Path;
        if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin))
        {
            return;
        }

        var info = new FileInfo(chemin);
        var res = new FichierAnalyse { Taille = info.Length, Modifie = info.LastWriteTimeUtc };
        var pistes = _sources.GetMediaStreams(item.Id).Where(s => s.Type == MediaStreamType.Subtitle && Francais(s)).ToList();
        double minutes = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes : 0;

        var internes = pistes.Where(s => !s.IsExternal).ToList();
        Dictionary<int, int> comptes = internes.Count > 0 ? await CompterInternes(chemin, internes.Select(s => s.Index), annulation).ConfigureAwait(false) : new();

        foreach (var s in pistes)
        {
            int n;
            if (s.IsExternal)
            {
                n = CompterExterne(s.Path);
            }
            else if (!comptes.TryGetValue(s.Index, out n))
            {
                continue;
            }

            if (n < 0)
            {
                continue;
            }

            // Les sous-titres en image (PGS, VobSub) ont environ 2 paquets par réplique (affichage + effacement)
            bool image = s.Codec is not null && (s.Codec.Contains("pgs", StringComparison.OrdinalIgnoreCase) || s.Codec.Contains("dvd", StringComparison.OrdinalIgnoreCase) || s.Codec.Contains("vobsub", StringComparison.OrdinalIgnoreCase));
            int repliques = image ? n / 2 : n;
            var nom = (s.Title ?? string.Empty) + " " + (s.DisplayTitle ?? string.Empty);
            string classe;
            if (NomSdh().IsMatch(nom) && !NomForce().IsMatch(nom))
            {
                classe = "sdh";
            }
            else
            {
                // Seuil : moins de ~2 répliques par minute (ou moins de 150 si la durée est inconnue) = forcé
                double parMinute = minutes > 0 ? repliques / minutes : double.NaN;
                classe = (minutes > 0 ? parMinute < 2.0 : repliques < 150) ? "force" : "complet";
            }

            res.Pistes[s.Index] = new PisteAnalysee { Repliques = repliques, Classe = classe };
        }

        // Deux pistes françaises : si la plus courte a moins d'un quart des répliques de l'autre, c'est la forcée
        var fr = res.Pistes.Where(p => p.Value.Classe != "sdh").OrderBy(p => p.Value.Repliques).ToList();
        if (fr.Count >= 2 && fr[0].Value.Repliques * 4 < fr[^1].Value.Repliques)
        {
            fr[0].Value.Classe = "force";
        }

        Cache[chemin] = res;
    }

    private static int CompterExterne(string? chemin)
    {
        try
        {
            if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin))
            {
                return -1;
            }

            var ext = Path.GetExtension(chemin).ToLowerInvariant();
            var lignes = File.ReadLines(chemin);
            return ext switch
            {
                ".srt" or ".vtt" => lignes.Count(l => l.Contains("-->", StringComparison.Ordinal)),
                ".ass" or ".ssa" => lignes.Count(l => l.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)),
                _ => -1
            };
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>Compte les paquets de chaque piste de sous-titres intégrée, en une seule lecture du fichier.</summary>
    private async Task<Dictionary<int, int>> CompterInternes(string chemin, IEnumerable<int> index, CancellationToken annulation)
    {
        var res = new Dictionary<int, int>();
        var voulus = new HashSet<int>(index);
        var probe = _encodeur.ProbePath;
        if (string.IsNullOrEmpty(probe))
        {
            return res;
        }

        var psi = new ProcessStartInfo
        {
            FileName = probe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "s", "-count_packets", "-show_entries", "stream=index,nb_read_packets", "-of", "csv=p=0", chemin })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p == null)
        {
            return res;
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(annulation);
        limite.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var sortie = await p.StandardOutput.ReadToEndAsync(limite.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(limite.Token).ConfigureAwait(false);
            foreach (var ligne in sortie.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = ligne.Split(',');
                if (c.Length >= 2 && int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    && int.TryParse(c[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && voulus.Contains(i))
                {
                    res[i] = n;
                }
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (Exception)
            {
                // déjà terminé
            }

            if (annulation.IsCancellationRequested)
            {
                throw;
            }
        }

        return res;
    }

    private void Sauver()
    {
        lock (_ecriture)
        {
            var tmp = Chemin + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, FichierAnalyse>(Cache), Json));
            File.Move(tmp, Chemin, true);
        }
    }
}
