using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Jellyfin.Plugin.WolfFlix.Controllers;

/// <summary>Adresses utilisées par le thème WolfFlix (toutes demandent d'être connecté).</summary>
[ApiController]
[Route("WolfFlix")]
[Authorize]
public class WolfFlixController : ControllerBase
{
    private readonly NotesImdb _notes;
    private readonly ILibraryManager _bibliotheque;
    private readonly AnalyseSousTitres _sousTitres;
    private readonly Avis _avis;
    private readonly IUserManager _utilisateurs;
    private readonly AVenir _aVenir;
    private readonly Suivi _suivi;

    public WolfFlixController(NotesImdb notes, ILibraryManager bibliotheque, AnalyseSousTitres sousTitres, Avis avis, IUserManager utilisateurs, AVenir aVenir, Suivi suivi)
    {
        _suivi = suivi;
        _aVenir = aVenir;
        _notes = notes;
        _bibliotheque = bibliotheque;
        _sousTitres = sousTitres;
        _avis = avis;
        _utilisateurs = utilisateurs;
    }

    private bool EstAdmin => User.IsInRole("Administrator");

    private Guid? Moi
    {
        get
        {
            var v = User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
            return Guid.TryParse(v, out var g) ? g : null;
        }
    }

    /// <summary>Présence et état de l'extension (le thème s'en sert pour savoir si elle est installée).</summary>
    [HttpGet("Etat")]
    public ActionResult Etat()
    {
        var d = _notes.Donnees;
        return Ok(new
        {
            version = Plugin.Instance?.Version.ToString() ?? string.Empty,
            notes = new { maj = d.Maj, titres = d.Titres.Count, series = d.Episodes.Count, enCours = _notes.EnCours, etape = _notes.Etape },
            sousTitres = new { analyses = _sousTitres.NbAnalyses, enCours = _sousTitres.EnCours, faits = _sousTitres.Faits, aFaire = _sousTitres.AFaire },
            aVenir = new { maj = _aVenir.Donnees.Maj, aVenir = _aVenir.Donnees.AVenir.Count, manquants = _aVenir.Donnees.Manquants.Count, enCours = _aVenir.EnCours, etape = _aVenir.Etape, tmdb = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbCle) }
        });
    }

    /// <summary>Notes IMDb d'un film ou d'une série (avec ses épisodes), et ses deux classements.</summary>
    [HttpGet("Notes/{id}")]
    public ActionResult Notes([FromRoute] Guid id)
    {
        var item = _bibliotheque.GetItemById(id);
        if (item is Episode ep && ep.Series != null)
        {
            item = ep.Series;
        }
        else if (item is Season sa && sa.Series != null)
        {
            item = sa.Series;
        }

        if (item == null)
        {
            return NotFound();
        }

        var imdb = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
        var d = _notes.Donnees;
        if (string.IsNullOrEmpty(imdb) || !d.Titres.TryGetValue(imdb, out var n))
        {
            return Ok(new { trouve = false, maj = d.Maj });
        }

        // Classement WolfFlix : parmi les titres du même type de la médiathèque
        var memeType = d.Titres.Values.Where(t => t.Type == n.Type && t.Votes > 0).ToList();
        int rangWf = 1 + memeType.Count(t => t.Note > n.Note || (t.Note == n.Note && t.Votes > n.Votes));

        object? episodes = null;
        if (n.Type == "s" && d.Episodes.TryGetValue(imdb, out var eps))
        {
            // Épisodes présents chez toi : leur identifiant Jellyfin pour ouvrir la fiche
            var chezToi = new Dictionary<(int, int), BaseItem>();
            foreach (var e in _bibliotheque.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { item.Id },
                Recursive = true
            }))
            {
                if (e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
                {
                    chezToi[(e.ParentIndexNumber.Value, e.IndexNumber.Value)] = e;
                }
            }

            episodes = eps.Select(e =>
            {
                chezToi.TryGetValue((e.S, e.E), out var local);
                return new { s = e.S, e = e.E, note = e.N, votes = e.V, id = local?.Id.ToString("N"), nom = local?.Name };
            }).ToList();
        }

        return Ok(new
        {
            trouve = true,
            maj = d.Maj,
            type = n.Type == "s" ? "serie" : "film",
            imdb,
            note = n.Note,
            votes = n.Votes,
            rangGlobal = n.Rang,
            totalGlobal = n.Total,
            votesMin = n.Type == "s" ? Plugin.Instance?.Configuration.VotesMinSeries : Plugin.Instance?.Configuration.VotesMinFilms,
            rangWolfFlix = rangWf,
            totalWolfFlix = memeType.Count,
            episodes
        });
    }

    /// <summary>Lance la mise à jour des notes tout de suite (administrateur).</summary>
    [HttpPost("Notes/MettreAJour")]
    public ActionResult MettreAJour()
    {
        if (!EstAdmin)
        {
            return Forbid();
        }

        if (!_notes.EnCours)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _notes.MettreAJour(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // déjà écrit dans le journal de Jellyfin
                }
            });
        }

        return Accepted(new { enCours = true });
    }

    /* ---------- Sous-titres : forcé ou complet ---------- */

    /// <summary>Classement des pistes de sous-titres françaises d'une vidéo. Lance l'analyse si elle n'a jamais été faite.</summary>
    [HttpGet("Pistes/{id}")]
    public ActionResult Pistes([FromRoute] Guid id)
    {
        var item = _bibliotheque.GetItemById(id);
        if (item == null)
        {
            return NotFound();
        }

        var r = _sousTitres.Resultats(item);
        if (r.Count == 0)
        {
            _sousTitres.Demander(item);
        }

        return Ok(r.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => new { classe = kv.Value.Classe, repliques = kv.Value.Repliques }));
    }

    /// <summary>Lance l'analyse de toute la médiathèque (administrateur), 6 h au plus.</summary>
    [HttpPost("Pistes/ToutAnalyser")]
    public ActionResult ToutAnalyser()
    {
        if (!EstAdmin)
        {
            return Forbid();
        }

        if (!_sousTitres.EnCours)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sousTitres.ToutAnalyser(null, TimeSpan.FromHours(6), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // déjà écrit dans le journal
                }
            });
        }

        return Accepted();
    }

    /* ---------- Avis du WolfFlix ---------- */

    /// <summary>Corps d'un avis envoyé par le thème.</summary>
    public sealed class AvisRecu
    {
        public double[] Criteres { get; set; } = Array.Empty<double>();

        public string? Commentaire { get; set; }
    }

    /// <summary>Tous les avis d'un film ou d'une série, avec les moyennes et le classement WolfFlix.</summary>
    [HttpGet("Avis/{id}")]
    public ActionResult LireAvis([FromRoute] Guid id)
    {
        var avis = _avis.De(id);
        var moi = Moi?.ToString("N");
        var liste = avis.Select(kv =>
        {
            Guid.TryParse(kv.Key, out var uid);
            var u = uid == Guid.Empty ? null : _utilisateurs.GetUserById(uid);
            return new
            {
                utilisateur = u?.Username ?? "Ancien utilisateur",
                utilisateurId = kv.Key,
                moi = kv.Key == moi,
                note = Math.Round(kv.Value.Note, 2),
                criteres = kv.Value.Criteres,
                commentaire = kv.Value.Commentaire,
                date = kv.Value.Date
            };
        }).OrderByDescending(a => a.moi).ThenByDescending(a => a.date).ToList();

        double? moyenne = liste.Count > 0 ? Math.Round(avis.Values.Average(a => a.Note), 2) : null;
        var parCritere = Enumerable.Range(0, Avis.NbCriteres)
            .Select(i => liste.Count > 0 ? Math.Round(avis.Values.Average(a => a.Criteres.Length > i ? a.Criteres[i] : 0), 2) : (double?)null).ToArray();

        // Classement parmi les titres notés du même type (films avec films, séries avec séries)
        int? rang = null, total = null;
        var item = _bibliotheque.GetItemById(id);
        if (moyenne.HasValue && item != null)
        {
            var type = item.GetBaseItemKind();
            var memes = _avis.Moyennes().Where(kv => Guid.TryParse(kv.Key, out var g) && _bibliotheque.GetItemById(g)?.GetBaseItemKind() == type).ToList();
            total = memes.Count;
            rang = 1 + memes.Count(kv => kv.Value.Moyenne > moyenne.Value || (kv.Value.Moyenne == moyenne.Value && kv.Value.Nb > liste.Count));
        }

        return Ok(new { moyenne, nb = liste.Count, criteres = parCritere, rang, total, admin = EstAdmin, avis = liste });
    }

    /// <summary>Ajoute ou remplace ton avis.</summary>
    [HttpPost("Avis/{id}")]
    public ActionResult EcrireAvis([FromRoute] Guid id, [FromBody] AvisRecu corps)
    {
        var moi = Moi;
        if (moi == null)
        {
            return Unauthorized();
        }

        var item = _bibliotheque.GetItemById(id);
        if (item == null || (item.GetBaseItemKind() != BaseItemKind.Movie && item.GetBaseItemKind() != BaseItemKind.Series))
        {
            return BadRequest("Les avis sont réservés aux films et aux séries.");
        }

        if (corps.Criteres == null || corps.Criteres.Length != Avis.NbCriteres || corps.Criteres.Any(c => c < 0 || c > 5 || double.IsNaN(c)))
        {
            return BadRequest("Il faut 5 notes de 0 à 5.");
        }

        _avis.Enregistrer(id, moi.Value, corps.Criteres, corps.Commentaire);
        return NoContent();
    }

    /// <summary>Retire ton avis (ou, pour un administrateur, celui de n'importe qui).</summary>
    [HttpDelete("Avis/{id}")]
    public ActionResult SupprimerAvis([FromRoute] Guid id, [FromQuery] string? utilisateur)
    {
        var moi = Moi;
        if (moi == null)
        {
            return Unauthorized();
        }

        var cible = moi.Value;
        if (!string.IsNullOrEmpty(utilisateur) && Guid.TryParse(utilisateur, out var autre) && autre != moi.Value)
        {
            if (!EstAdmin)
            {
                return Forbid();
            }

            cible = autre;
        }

        return _avis.Supprimer(id, cible) ? NoContent() : NotFound();
    }

    /* ---------- À venir / Ce qui manque ---------- */

    /// <summary>Prochaines sorties et titres manquants (les éléments ignorés sont retirés, sauf pour l'administrateur qui les voit marqués).</summary>
    [HttpGet("AVenir")]
    public ActionResult LireAVenir()
    {
        var d = _aVenir.Donnees;
        var ignores = _aVenir.Ignores();
        bool admin = EstAdmin;
        object Liste(List<Entree> l) => l.Where(e => admin || !ignores.Contains(e.Cle))
            .Select(e => new { e.Cle, e.Type, e.Titre, e.Detail, e.Date, e.DateFrance, e.Affiche, e.Tmdb, e.Groupe, e.Lien, ignore = ignores.Contains(e.Cle) }).ToList();
        return Ok(new { maj = d.Maj, enCours = _aVenir.EnCours, etape = _aVenir.Etape, admin, aVenir = Liste(d.AVenir), manquants = Liste(d.Manquants) });
    }

    /// <summary>Ignorer (ou ne plus ignorer) une entrée (administrateur).</summary>
    [HttpPost("AVenir/Ignorer")]
    public ActionResult Ignorer([FromQuery] string cle, [FromQuery] bool oui = true)
    {
        if (!EstAdmin)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(cle))
        {
            return BadRequest();
        }

        _aVenir.Ignorer(cle, oui);
        return NoContent();
    }

    /// <summary>Recalcule « À venir » maintenant (administrateur).</summary>
    [HttpPost("AVenir/MettreAJour")]
    public ActionResult MettreAJourAVenir()
    {
        if (!EstAdmin)
        {
            return Forbid();
        }

        if (!_aVenir.EnCours)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _aVenir.MettreAJour(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // déjà écrit dans le journal
                }
            });
        }

        return Accepted();
    }
    /// <summary>Suivi personnel : historique complet et objectifs de l'utilisateur connecté.</summary>
    [HttpGet("Suivi")]
    public ActionResult LireSuivi()
    {
        if (Moi is not Guid moi)
        {
            return Unauthorized();
        }

        var l = _suivi.Historique(moi);
        return Ok(new
        {
            lectures = l.Select(x => new { i = x.I, d = x.D, t = x.T, n = x.N, s = x.S, p = x.P, x = x.X, m = x.M, o = x.O }),
            objectifs = _suivi.LireObjectifs(moi)
        });
    }

    /// <summary>Objectifs de l'année.</summary>
    [HttpPost("Suivi/Objectifs")]
    public ActionResult EcrireObjectifs([FromBody] Objectifs objectifs)
    {
        if (Moi is not Guid moi)
        {
            return Unauthorized();
        }

        _suivi.EcrireObjectifs(moi, objectifs);
        return Ok(_suivi.LireObjectifs(moi));
    }

    /// <summary>Retire une entrée de son historique.</summary>
    [HttpDelete("Suivi/Lecture")]
    public ActionResult RetirerLecture([FromQuery] string item, [FromQuery] DateTime date)
    {
        if (Moi is not Guid moi)
        {
            return Unauthorized();
        }

        return _suivi.Retirer(moi, item ?? string.Empty, date) ? NoContent() : NotFound();
    }
}
