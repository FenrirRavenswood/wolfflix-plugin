using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

public sealed class Disque
{
    public string Point { get; set; } = string.Empty;

    public long Total { get; set; }

    public long Libre { get; set; }

    public List<string> Bibliotheques { get; set; } = new();
}

public sealed class ElementSante
{
    public string Id { get; set; } = string.Empty;

    public string Nom { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public long Taille { get; set; }
}

public sealed class AlerteDossier
{
    public DateTime Date { get; set; }

    public string Chemin { get; set; } = string.Empty;

    public string Bibliotheque { get; set; } = string.Empty;
}

public sealed class DonneesSante
{
    public DateTime Maj { get; set; }

    public List<Disque> Disques { get; set; } = new();

    public Dictionary<string, long> Compteurs { get; set; } = new();

    public List<AlerteDossier> Alertes { get; set; } = new();

    public List<ElementSante> SansAffiche { get; set; } = new();

    public List<ElementSante> NonIdentifies { get; set; } = new();

    public List<ElementSante> Doublons { get; set; } = new();

    public List<ElementSante> SansVf { get; set; } = new();

    public List<ElementSante> AReencoder { get; set; } = new();

    public long GainEstime { get; set; }
}

/// <summary>Photo des dossiers des bibliothèques, pour repérer ceux qui disparaissent.</summary>
public sealed class PhotoDossiers
{
    public DateTime Date { get; set; }

    public Dictionary<string, string> Dossiers { get; set; } = new();
}

/// <summary>
/// Santé du serveur (administrateur) : place sur les disques, dossiers disparus, affiches manquantes,
/// titres non identifiés, doublons, films sans VF et fichiers lourds à réencoder en priorité.
/// </summary>
public sealed class Sante
{
    private const int Max = 300;
    private readonly ILibraryManager _bibliotheque;
    private readonly ILogger<Sante> _log;
    private readonly SemaphoreSlim _verrou = new(1, 1);

    public Sante(ILibraryManager bibliotheque, ILogger<Sante> log)
    {
        _bibliotheque = bibliotheque;
        _log = log;
    }

    public bool EnCours { get; private set; }

    public DonneesSante? Donnees => AVenir.Lire<DonneesSante>("sante.json");

    public void Demarrer()
    {
        if (EnCours)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Calculer(null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WolfFlix : bilan de santé impossible");
            }
        });
    }

    public void EffacerAlertes()
    {
        var d = Donnees;
        if (d != null)
        {
            d.Alertes.Clear();
            AVenir.Ecrire("sante.json", d);
        }
    }

    public void Calculer(IProgress<double>? progression, CancellationToken annulation)
    {
        if (!_verrou.Wait(0, annulation))
        {
            return;
        }

        EnCours = true;
        try
        {
            var ancien = Donnees;
            var d = new DonneesSante { Maj = DateTime.UtcNow, Alertes = ancien?.Alertes ?? new List<AlerteDossier>() };
            var dossiersBib = _bibliotheque.GetVirtualFolders();

            // ---------- Disques ----------
            var lecteurs = DriveInfo.GetDrives().Where(x =>
            {
                try
                {
                    return x.IsReady && x.TotalSize > 0;
                }
                catch
                {
                    return false;
                }
            }).ToList();
            var parPoint = new Dictionary<string, Disque>();
            foreach (var b in dossiersBib)
            {
                foreach (var loc in b.Locations ?? Array.Empty<string>())
                {
                    var l = lecteurs.Where(x => loc.StartsWith(x.RootDirectory.FullName, StringComparison.Ordinal))
                        .OrderByDescending(x => x.RootDirectory.FullName.Length).FirstOrDefault();
                    if (l == null)
                    {
                        continue;
                    }

                    if (!parPoint.TryGetValue(l.RootDirectory.FullName, out var disque))
                    {
                        disque = new Disque { Point = l.RootDirectory.FullName, Total = l.TotalSize, Libre = l.AvailableFreeSpace };
                        parPoint[disque.Point] = disque;
                    }

                    if (!disque.Bibliotheques.Contains(b.Name))
                    {
                        disque.Bibliotheques.Add(b.Name);
                    }
                }
            }

            d.Disques = parPoint.Values.OrderBy(x => x.Point).ToList();
            progression?.Report(10);

            // ---------- Dossiers disparus ----------
            var photo = new PhotoDossiers { Date = DateTime.UtcNow };
            foreach (var b in dossiersBib)
            {
                foreach (var loc in b.Locations ?? Array.Empty<string>())
                {
                    if (!Directory.Exists(loc))
                    {
                        continue; // disque débranché : on ne crie pas au loup pour tout son contenu
                    }

                    try
                    {
                        foreach (var n1 in Directory.EnumerateDirectories(loc))
                        {
                            photo.Dossiers[n1] = b.Name;
                            foreach (var n2 in Directory.EnumerateDirectories(n1))
                            {
                                photo.Dossiers[n2] = b.Name;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Santé : lecture de {Dossier} impossible", loc);
                    }
                }
            }

            var avant = AVenir.Lire<PhotoDossiers>("sante-dossiers.json");
            if (avant != null)
            {
                var racinesPresentes = dossiersBib.SelectMany(b => b.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
                foreach (var (chemin, bib) in avant.Dossiers)
                {
                    // Seulement les dossiers dont la bibliothèque est toujours accessible
                    if (photo.Dossiers.ContainsKey(chemin) || !racinesPresentes.Any(r => chemin.StartsWith(r, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    // Un sous-dossier d'un dossier déjà signalé disparu n'est pas répété
                    var parent = Path.GetDirectoryName(chemin) ?? string.Empty;
                    if (avant.Dossiers.ContainsKey(parent) && !photo.Dossiers.ContainsKey(parent))
                    {
                        continue;
                    }

                    d.Alertes.Insert(0, new AlerteDossier { Date = DateTime.UtcNow, Chemin = chemin, Bibliotheque = bib });
                    _log.LogWarning("WolfFlix : le dossier {Chemin} a disparu de la bibliothèque {Bibliotheque}", chemin, bib);
                }
            }

            d.Alertes = d.Alertes.Take(200).ToList();
            AVenir.Ecrire("sante-dossiers.json", photo);
            progression?.Report(25);

            // ---------- Titres ----------
            var titres = _bibliotheque.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Book, BaseItemKind.BoxSet },
                Recursive = true,
                IsVirtualItem = false
            });
            d.Compteurs["films"] = titres.Count(t => t is Movie);
            d.Compteurs["series"] = titres.Count(t => t is Series);
            d.Compteurs["livres"] = titres.Count(t => t is Book);
            d.Compteurs["collections"] = titres.Count(t => t is BoxSet);
            d.Compteurs["episodes"] = _bibliotheque.GetCount(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, Recursive = true, IsVirtualItem = false });

            foreach (var t in titres.Where(t => t is not Book))
            {
                if (!t.HasImage(ImageType.Primary, 0))
                {
                    d.SansAffiche.Add(Element(t, string.Empty));
                }

                if ((t is Movie || t is Series) && string.IsNullOrEmpty(t.GetProviderId(MetadataProvider.Tmdb)) && string.IsNullOrEmpty(t.GetProviderId(MetadataProvider.Imdb))
                    && string.IsNullOrEmpty(t.GetProviderId(MetadataProvider.Tvdb)))
                {
                    d.NonIdentifies.Add(Element(t, t.Path ?? string.Empty));
                }
            }

            foreach (var g in titres.Where(t => t is Movie || t is Series)
                         .Select(t => (t, cle: t.GetProviderId(MetadataProvider.Tmdb) ?? t.GetProviderId(MetadataProvider.Imdb)))
                         .Where(x => !string.IsNullOrEmpty(x.cle))
                         .GroupBy(x => (x.t is Movie ? "m" : "s") + x.cle)
                         .Where(g => g.Count() > 1))
            {
                foreach (var (t, _) in g)
                {
                    d.Doublons.Add(Element(t, t.Path ?? string.Empty));
                }
            }

            progression?.Report(45);

            // ---------- Films : VF et fichiers lourds ----------
            var films = titres.OfType<Movie>().ToList();
            int n = 0;
            var lourds = new List<ElementSante>();
            foreach (var f in films)
            {
                annulation.ThrowIfCancellationRequested();
                n++;
                if (n % 50 == 0)
                {
                    progression?.Report(45 + (50.0 * n / Math.Max(1, films.Count)));
                }

                IReadOnlyList<MediaStream> flux;
                try
                {
                    flux = f.GetMediaStreams();
                }
                catch
                {
                    continue;
                }

                var audios = flux.Where(x => x.Type == MediaStreamType.Audio).ToList();
                if (audios.Count > 0 && !audios.Any(a => a.Language is "fre" or "fra" or "fr" || (a.Title ?? string.Empty).Contains("fran", StringComparison.OrdinalIgnoreCase)
                    || (a.Title ?? string.Empty).Contains("VF", StringComparison.Ordinal)))
                {
                    d.SansVf.Add(Element(f, string.Join(", ", audios.Select(a => a.Language ?? "?").Distinct())));
                }

                long taille = f.Size ?? 0;
                if (taille <= 0 && !string.IsNullOrEmpty(f.Path) && File.Exists(f.Path))
                {
                    try
                    {
                        taille = new FileInfo(f.Path).Length;
                    }
                    catch
                    {
                        taille = 0;
                    }
                }

                var video = flux.FirstOrDefault(x => x.Type == MediaStreamType.Video);
                var codec = (video?.Codec ?? string.Empty).ToLowerInvariant();
                var heures = (f.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerHour;
                if (taille > 0 && heures > 0.3 && codec is not ("hevc" or "h265" or "av1"))
                {
                    var goParHeure = taille / 1e9 / heures;
                    if (goParHeure > 1.2)
                    {
                        lourds.Add(new ElementSante
                        {
                            Id = f.Id.ToString("N"),
                            Nom = f.Name ?? string.Empty,
                            Type = "Movie",
                            Taille = taille,
                            Detail = (codec.Length > 0 ? codec.ToUpperInvariant() : "?") + " · " + goParHeure.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + " Go/h"
                        });
                    }
                }
            }

            // Gain estimé d'après tes essais HandBrake : environ 1/3 sur les gros fichiers non HEVC
            d.GainEstime = lourds.Sum(x => x.Taille) / 3;
            d.AReencoder = lourds.OrderByDescending(x => x.Taille).Take(Max).ToList();
            d.Compteurs["aReencoder"] = lourds.Count;
            d.Compteurs["sansAffiche"] = d.SansAffiche.Count;
            d.Compteurs["nonIdentifies"] = d.NonIdentifies.Count;
            d.Compteurs["doublons"] = d.Doublons.Count;
            d.Compteurs["sansVf"] = d.SansVf.Count;
            d.SansAffiche = d.SansAffiche.Take(Max).ToList();
            d.NonIdentifies = d.NonIdentifies.Take(Max).ToList();
            d.Doublons = d.Doublons.Take(Max).ToList();
            d.SansVf = d.SansVf.Take(Max).ToList();

            AVenir.Ecrire("sante.json", d);
            progression?.Report(100);
        }
        finally
        {
            EnCours = false;
            _verrou.Release();
        }
    }

    private static ElementSante Element(BaseItem t, string detail) => new()
    {
        Id = t.Id.ToString("N"),
        Nom = t.Name ?? string.Empty,
        Type = t switch { Movie => "Movie", Series => "Series", BoxSet => "BoxSet", Book => "Book", _ => t.GetType().Name },
        Detail = detail
    };
}
