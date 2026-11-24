using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WolfFlix.Controllers;

/// <summary>Adresses utilisées par le thème WolfFlix (toutes demandent d'être connecté).</summary>
[ApiController]
[Route("WolfFlix")]
[Authorize]
public class WolfFlixController : ControllerBase
{
    private readonly NotesImdb _notes;
    private readonly ILibraryManager _bibliotheque;

    public WolfFlixController(NotesImdb notes, ILibraryManager bibliotheque)
    {
        _notes = notes;
        _bibliotheque = bibliotheque;
    }

    private bool EstAdmin => User.IsInRole("Administrator");

    /// <summary>Présence et état de l'extension (le thème s'en sert pour savoir si elle est installée).</summary>
    [HttpGet("Etat")]
    public ActionResult Etat()
    {
        var d = _notes.Donnees;
        return Ok(new
        {
            version = Plugin.Instance?.Version.ToString() ?? string.Empty,
            notes = new { maj = d.Maj, titres = d.Titres.Count, series = d.Episodes.Count, enCours = _notes.EnCours, etape = _notes.Etape }
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

        var imdb = item.GetProviderId(MetadataProvider.Imdb);
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
}
