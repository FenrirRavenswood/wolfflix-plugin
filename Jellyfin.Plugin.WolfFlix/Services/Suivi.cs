using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Un titre vu, lu ou écouté.</summary>
public sealed class Lecture
{
    /// <summary>Identifiant Jellyfin (sans tirets).</summary>
    public string I { get; set; } = string.Empty;

    /// <summary>Date (UTC).</summary>
    public DateTime D { get; set; }

    /// <summary>film, episode, livre, chapitre, audio.</summary>
    public string T { get; set; } = string.Empty;

    /// <summary>Nom du titre.</summary>
    public string N { get; set; } = string.Empty;

    /// <summary>Série, ou dossier du webtoon / de la saga de livres.</summary>
    public string S { get; set; } = string.Empty;

    /// <summary>Identifiant de la série ou du dossier.</summary>
    public string P { get; set; } = string.Empty;

    /// <summary>Précision : « S01E05 »…</summary>
    public string X { get; set; } = string.Empty;

    /// <summary>Durée en minutes (vidéo, livre audio).</summary>
    public int M { get; set; }

    /// <summary>Origine : « vu » (lecture terminée), « main » (marqué vu à la main), « import » (avant l'extension).</summary>
    public string O { get; set; } = "vu";
}

/// <summary>Objectifs d'un utilisateur pour l'année (0 = pas d'objectif).</summary>
public sealed class Objectifs
{
    public int Films { get; set; }

    public int Episodes { get; set; }

    public int Livres { get; set; }

    public int Chapitres { get; set; }

    public int Heures { get; set; }
}

/// <summary>Fichier du suivi.</summary>
public sealed class DonneesSuivi
{
    public Dictionary<string, List<Lecture>> Lectures { get; set; } = new();

    public Dictionary<string, Objectifs> Objectifs { get; set; } = new();

    /// <summary>Utilisateurs dont l'historique d'avant l'extension a déjà été repris.</summary>
    public List<string> Importes { get; set; } = new();
}

/// <summary>
/// Suivi personnel : chaque titre terminé (film, épisode, livre, chapitre, livre audio) est noté avec sa date.
/// Écoute les « vu » de Jellyfin ; l'historique d'avant l'extension est repris une fois (date du dernier visionnage).
/// </summary>
public sealed class Suivi : IHostedService
{
    private static readonly Regex Archive = new(@"\.(cbz|cbr|cb7|cbt|zip|rar|7z|tar)$", RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IUserDataManager _donneesUtilisateur;
    private readonly IUserManager _utilisateurs;
    private readonly ILibraryManager _bibliotheque;
    private readonly ILogger<Suivi> _log;
    private readonly object _verrou = new();
    private DonneesSuivi? _donnees;
    private Timer? _ecriture;

    public Suivi(IUserDataManager donneesUtilisateur, IUserManager utilisateurs, ILibraryManager bibliotheque, ILogger<Suivi> log)
    {
        _donneesUtilisateur = donneesUtilisateur;
        _utilisateurs = utilisateurs;
        _bibliotheque = bibliotheque;
        _log = log;
    }

    private static string Fichier
    {
        get
        {
            var d = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "wolfflix");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "suivi.json");
        }
    }

    private DonneesSuivi Donnees
    {
        get
        {
            if (_donnees != null)
            {
                return _donnees;
            }

            try
            {
                if (File.Exists(Fichier))
                {
                    _donnees = JsonSerializer.Deserialize<DonneesSuivi>(File.ReadAllText(Fichier));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Suivi : fichier illisible, on repart de zéro");
                try
                {
                    File.Copy(Fichier, Fichier + ".abime", true);
                }
                catch
                {
                    // rien
                }
            }

            _donnees ??= new DonneesSuivi();
            return _donnees;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _donneesUtilisateur.UserDataSaved += Enregistre;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _donneesUtilisateur.UserDataSaved -= Enregistre;
        Ecrire();
        return Task.CompletedTask;
    }

    private void Enregistre(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e.Item is null || e.UserData is null || e.UserId == Guid.Empty)
            {
                return;
            }

            var raison = e.SaveReason;
            if (raison != UserDataSaveReason.PlaybackFinished && raison != UserDataSaveReason.TogglePlayed && raison != UserDataSaveReason.UpdateUserData)
            {
                return;
            }

            var uid = e.UserId.ToString("N");
            var id = e.Item.Id.ToString("N");
            if (!e.UserData.Played)
            {
                // « Non vu » juste après : on retire la dernière entrée (erreur de clic)
                if (raison == UserDataSaveReason.TogglePlayed)
                {
                    lock (_verrou)
                    {
                        if (Donnees.Lectures.TryGetValue(uid, out var l))
                        {
                            var k = l.FindLastIndex(x => x.I == id);
                            if (k >= 0 && DateTime.UtcNow - l[k].D < TimeSpan.FromHours(24))
                            {
                                l.RemoveAt(k);
                                Planifier();
                            }
                        }
                    }
                }

                return;
            }

            // Une lecture terminée remet la position à zéro ; une simple progression ne compte pas
            if (e.UserData.PlaybackPositionTicks != 0)
            {
                return;
            }

            var lecture = Decrire(e.Item, raison == UserDataSaveReason.TogglePlayed ? "main" : "vu", DateTime.UtcNow);
            if (lecture is null)
            {
                return;
            }

            lock (_verrou)
            {
                if (!Donnees.Lectures.TryGetValue(uid, out var liste))
                {
                    liste = new List<Lecture>();
                    Donnees.Lectures[uid] = liste;
                }

                var dernier = liste.FindLast(x => x.I == id);
                if (dernier != null && lecture.D - dernier.D < TimeSpan.FromHours(6))
                {
                    return;
                }

                liste.Add(lecture);
                Planifier();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suivi : enregistrement impossible");
        }
    }

    private static Lecture? Decrire(BaseItem item, string origine, DateTime date)
    {
        var l = new Lecture { I = item.Id.ToString("N"), D = date, N = item.Name ?? string.Empty, O = origine };
        var minutes = item.RunTimeTicks is long t && t > 0 ? (int)Math.Round(t / 600000000.0) : 0;
        switch (item)
        {
            case Episode ep:
                l.T = "episode";
                l.S = ep.SeriesName ?? ep.Series?.Name ?? string.Empty;
                l.P = ep.SeriesId.ToString("N");
                l.X = (ep.ParentIndexNumber is int s ? "S" + s.ToString("00") : string.Empty) + (ep.IndexNumber is int n ? "E" + n.ToString("00") : string.Empty);
                l.M = minutes;
                break;
            case Movie:
                l.T = "film";
                l.M = minutes;
                break;
            case MediaBrowser.Controller.Entities.AudioBook:
                l.T = "audio";
                l.M = minutes;
                l.S = item.GetParent()?.Name ?? string.Empty;
                l.P = item.ParentId.ToString("N");
                break;
            case Book:
                var parent = item.GetParent();
                l.T = Archive.IsMatch(item.Path ?? string.Empty) && parent is Folder && parent is not CollectionFolder ? "chapitre" : "livre";
                l.S = parent is CollectionFolder ? string.Empty : parent?.Name ?? string.Empty;
                l.P = parent is CollectionFolder ? string.Empty : item.ParentId.ToString("N");
                break;
            default:
                return null;
        }

        return l;
    }

    /// <summary>Historique d'un utilisateur (repris une fois depuis Jellyfin au premier appel).</summary>
    public List<Lecture> Historique(Guid utilisateur)
    {
        var uid = utilisateur.ToString("N");
        bool aImporter;
        lock (_verrou)
        {
            aImporter = !Donnees.Importes.Contains(uid);
        }

        if (aImporter)
        {
            Importer(utilisateur);
        }

        lock (_verrou)
        {
            return Donnees.Lectures.TryGetValue(uid, out var l) ? l.OrderBy(x => x.D).ToList() : new List<Lecture>();
        }
    }

    private void Importer(Guid utilisateur)
    {
        var uid = utilisateur.ToString("N");
        var u = _utilisateurs.GetUserById(utilisateur);
        if (u is null)
        {
            return;
        }

        var nouvelles = new List<Lecture>();
        try
        {
            var items = _bibliotheque.GetItemList(new InternalItemsQuery(u)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Book, BaseItemKind.AudioBook },
                Recursive = true,
                IsPlayed = true
            });
            foreach (var item in items)
            {
                var d = _donneesUtilisateur.GetUserData(u, item);
                if (d is null || !d.Played)
                {
                    continue;
                }

                var l = Decrire(item, "import", d.LastPlayedDate ?? item.DateCreated);
                if (l != null)
                {
                    nouvelles.Add(l);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suivi : reprise de l'historique impossible");
            return;
        }

        lock (_verrou)
        {
            if (Donnees.Importes.Contains(uid))
            {
                return;
            }

            if (!Donnees.Lectures.TryGetValue(uid, out var liste))
            {
                liste = new List<Lecture>();
                Donnees.Lectures[uid] = liste;
            }

            var deja = new HashSet<string>(liste.Select(x => x.I));
            liste.AddRange(nouvelles.Where(x => !deja.Contains(x.I)));
            Donnees.Importes.Add(uid);
            Planifier();
        }

        _log.LogInformation("Suivi : {N} titres repris pour un utilisateur", nouvelles.Count);
    }

    public Objectifs LireObjectifs(Guid utilisateur)
    {
        lock (_verrou)
        {
            return Donnees.Objectifs.TryGetValue(utilisateur.ToString("N"), out var o) ? o : new Objectifs();
        }
    }

    public void EcrireObjectifs(Guid utilisateur, Objectifs o)
    {
        static int Borne(int v) => Math.Clamp(v, 0, 100000);
        lock (_verrou)
        {
            Donnees.Objectifs[utilisateur.ToString("N")] = new Objectifs
            {
                Films = Borne(o.Films),
                Episodes = Borne(o.Episodes),
                Livres = Borne(o.Livres),
                Chapitres = Borne(o.Chapitres),
                Heures = Borne(o.Heures)
            };
            Planifier();
        }
    }

    /// <summary>Retire une entrée (correction par l'utilisateur).</summary>
    public bool Retirer(Guid utilisateur, string item, DateTime date)
    {
        lock (_verrou)
        {
            if (!Donnees.Lectures.TryGetValue(utilisateur.ToString("N"), out var l))
            {
                return false;
            }

            var k = l.FindIndex(x => x.I == item && Math.Abs((x.D - date.ToUniversalTime()).TotalSeconds) < 2);
            if (k < 0)
            {
                return false;
            }

            l.RemoveAt(k);
            Planifier();
            return true;
        }
    }

    private void Planifier()
    {
        // Écriture groupée : un marquage de toute une série ne réécrit pas le fichier 200 fois
        _ecriture ??= new Timer(_ => Ecrire(), null, Timeout.Infinite, Timeout.Infinite);
        _ecriture.Change(3000, Timeout.Infinite);
    }

    private void Ecrire()
    {
        try
        {
            string texte;
            lock (_verrou)
            {
                if (_donnees is null)
                {
                    return;
                }

                texte = JsonSerializer.Serialize(_donnees, Json);
            }

            var tmp = Fichier + ".tmp";
            File.WriteAllText(tmp, texte);
            File.Move(tmp, Fichier, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suivi : écriture impossible");
        }
    }
}
