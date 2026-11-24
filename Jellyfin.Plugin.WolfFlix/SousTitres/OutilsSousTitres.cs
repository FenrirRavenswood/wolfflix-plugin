using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WolfFlix.SousTitres;

/// <summary>Ce qu'on garde d'un résultat pour pouvoir le télécharger ensuite (encodé dans l'identifiant).</summary>
public sealed class Jeton
{
    public string U { get; set; } = string.Empty;

    public int? S { get; set; }

    public int? E { get; set; }

    public bool F { get; set; }

    public bool H { get; set; }

    public string L { get; set; } = "fre";
}

/// <summary>Outils communs aux sources de sous-titres WolfFlix.</summary>
internal static partial class OutilsSousTitres
{
    private static readonly string[] Extensions = { ".srt", ".ass", ".ssa", ".vtt", ".sub" };

    public static string Encoder(Jeton j)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(j)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Jeton Decoder(string id)
    {
        var b = id.Replace('-', '+').Replace('_', '/');
        b += new string('=', (4 - (b.Length % 4)) % 4);
        return JsonSerializer.Deserialize<Jeton>(Convert.FromBase64String(b)) ?? new Jeton();
    }

    /// <summary>Langue sur deux lettres (« fr »).</summary>
    public static string DeuxLettres(SubtitleSearchRequest r)
    {
        if (!string.IsNullOrEmpty(r.TwoLetterISOLanguageName))
        {
            return r.TwoLetterISOLanguageName.ToLowerInvariant();
        }

        return (r.Language ?? "fre").ToLowerInvariant() switch
        {
            "fre" or "fra" => "fr",
            "eng" => "en",
            _ => "fr"
        };
    }

    /// <summary>Identifiants IMDb / TMDB de la série d'un épisode (ceux de la requête sont ceux de l'épisode).</summary>
    public static (string Imdb, string Tmdb, string Nom) Serie(ILibraryManager bibliotheque, SubtitleSearchRequest r)
    {
        try
        {
            if (!string.IsNullOrEmpty(r.MediaPath) && bibliotheque.FindByPath(r.MediaPath, false) is Episode ep && ep.Series is { } s)
            {
                return (s.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb) ?? string.Empty, s.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) ?? string.Empty, s.Name ?? r.SeriesName ?? string.Empty);
            }
        }
        catch
        {
            // On se contentera du nom de la série
        }

        return (string.Empty, string.Empty, r.SeriesName ?? string.Empty);
    }

    public static bool ParleDeForce(string texte)
        => Regex.IsMatch(texte ?? string.Empty, @"\b(forced|forc[ée]s?|foreign)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Sort le bon fichier d'un zip (ou le fichier tel quel).
    /// Pack de saison : on cherche l'épisode demandé (S01E05, 1x05, E05…).
    /// </summary>
    public static SubtitleResponse? Extraire(byte[] octets, Jeton j)
    {
        byte[] contenu;
        string ext;
        if (octets.Length > 4 && octets[0] == 'P' && octets[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(octets), ZipArchiveMode.Read);
            var fichiers = zip.Entries
                .Where(e => Extensions.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()) && e.Length > 0)
                .ToList();
            if (fichiers.Count == 0)
            {
                return null;
            }

            var choisi = fichiers.Count == 1 ? fichiers[0] : Choisir(fichiers, j);
            if (choisi is null)
            {
                return null;
            }

            using var m = new MemoryStream();
            using (var f = choisi.Open())
            {
                f.CopyTo(m);
            }

            contenu = m.ToArray();
            ext = Path.GetExtension(choisi.FullName).TrimStart('.').ToLowerInvariant();
        }
        else
        {
            contenu = octets;
            var debut = Encoding.UTF8.GetString(octets, 0, Math.Min(octets.Length, 200));
            ext = debut.Contains("[Script Info]", StringComparison.Ordinal) ? "ass" : debut.StartsWith("WEBVTT", StringComparison.Ordinal) ? "vtt" : "srt";
        }

        return new SubtitleResponse
        {
            Format = ext,
            Language = j.L,
            IsForced = j.F,
            IsHearingImpaired = j.H,
            Stream = new MemoryStream(contenu)
        };
    }

    private static ZipArchiveEntry? Choisir(List<ZipArchiveEntry> fichiers, Jeton j)
    {
        // Préférence aux .srt
        var srt = fichiers.Where(f => f.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (srt.Count > 0)
        {
            fichiers = srt;
        }

        if (j.E is int e)
        {
            var s = j.S ?? -1;
            var trouves = fichiers.Where(f =>
            {
                var n = Path.GetFileName(f.FullName);
                foreach (Match m in EpisodeRegex().Matches(n))
                {
                    var ms = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value) : -1;
                    var me = int.Parse(m.Groups["e"].Value);
                    if (me == e && (ms == -1 || s == -1 || ms == s))
                    {
                        return true;
                    }
                }

                return false;
            }).ToList();
            if (trouves.Count > 0)
            {
                // Sans « forced » si on veut le complet, et inversement
                return trouves.FirstOrDefault(f => ParleDeForce(f.FullName) == j.F) ?? trouves[0];
            }

            // Les fichiers portent des numéros d'autres épisodes : pas le bon pack
            if (fichiers.Any(f => EpisodeRegex().IsMatch(Path.GetFileName(f.FullName))) || fichiers.Count > 3)
            {
                return null;
            }
        }

        return fichiers.FirstOrDefault(f => ParleDeForce(f.FullName) == j.F) ?? fichiers.OrderByDescending(f => f.Length).First();
    }

    [GeneratedRegex(@"(?:[Ss](?<s>\d{1,2})[ ._-]?[Ee](?<e>\d{1,3}))|(?:\b(?<s>\d{1,2})x(?<e>\d{2,3})\b)|(?:\b[Ee][Pp]?(?<e>\d{1,3})\b)")]
    private static partial Regex EpisodeRegex();
}
