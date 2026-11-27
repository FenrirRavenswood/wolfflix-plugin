using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>Une lecture : qui, quoi, sur quel appareil, et comment (lecture directe ou transcodage, et pourquoi).</summary>
public sealed class LectureJournal
{
    public DateTime Debut { get; set; }

    public DateTime Fin { get; set; }

    public string Utilisateur { get; set; } = string.Empty;

    public string Appareil { get; set; } = string.Empty;

    public string Client { get; set; } = string.Empty;

    public string Ip { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Titre { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>DirectPlay, DirectStream ou Transcode.</summary>
    public string Methode { get; set; } = string.Empty;

    public List<string> Raisons { get; set; } = new();

    public string Video { get; set; } = string.Empty;

    public string Audio { get; set; } = string.Empty;

    public string SousTitres { get; set; } = string.Empty;

    public string Materiel { get; set; } = string.Empty;

    public int? Debit { get; set; }

    public int Minutes { get; set; }

    public bool Fini { get; set; }
}

/// <summary>
/// Journal d'activité (administrateur) : chaque lecture vidéo ou audio est notée à la fin,
/// avec la raison exacte d'un éventuel transcodage (codec, débit, sous-titres incrustés…).
/// </summary>
public sealed class Journal : IHostedService
{
    private const int Garder = 3000;
    private readonly ISessionManager _sessions;
    private readonly ILogger<Journal> _log;
    private readonly Dictionary<string, LectureJournal> _enCours = new();
    private readonly object _verrou = new();

    public Journal(ISessionManager sessions, ILogger<Journal> log)
    {
        _sessions = sessions;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += Debut;
        _sessions.PlaybackProgress += Progression;
        _sessions.PlaybackStopped += Arret;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= Debut;
        _sessions.PlaybackProgress -= Progression;
        _sessions.PlaybackStopped -= Arret;
        return Task.CompletedTask;
    }

    public List<LectureJournal> Lire()
    {
        lock (_verrou)
        {
            return AVenir.Lire<List<LectureJournal>>("journal.json") ?? new List<LectureJournal>();
        }
    }

    private static string Cle(PlaybackProgressEventArgs e)
        => !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId : (e.Session?.Id ?? string.Empty) + "|" + (e.Item?.Id.ToString("N") ?? string.Empty);

    private void Debut(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item is null || e.Session is null)
            {
                return;
            }

            var l = new LectureJournal
            {
                Debut = DateTime.UtcNow,
                Utilisateur = e.Session.UserName ?? string.Empty,
                Appareil = e.DeviceName ?? e.Session.DeviceName ?? string.Empty,
                Client = e.ClientName ?? e.Session.Client ?? string.Empty,
                Ip = e.Session.RemoteEndPoint ?? string.Empty,
                ItemId = e.Item.Id.ToString("N"),
                Titre = Titre(e.Item),
                Type = e.Item.GetType().Name
            };
            Completer(l, e);
            lock (_verrou)
            {
                _enCours[Cle(e)] = l;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Journal : début de lecture non noté");
        }
    }

    private void Progression(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            LectureJournal? l;
            lock (_verrou)
            {
                _enCours.TryGetValue(Cle(e), out l);
            }

            if (l == null)
            {
                Debut(sender, e);
                return;
            }

            Completer(l, e);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Journal : progression non notée");
        }
    }

    private void Arret(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            LectureJournal? l;
            var cle = Cle(e);
            lock (_verrou)
            {
                _enCours.TryGetValue(cle, out l);
                _enCours.Remove(cle);
            }

            if (l == null)
            {
                return;
            }

            Completer(l, e);
            l.Fin = DateTime.UtcNow;
            l.Fini = e.PlayedToCompletion;
            l.Minutes = (int)Math.Round((l.Fin - l.Debut).TotalMinutes);
            if (l.Minutes < 1 && l.Methode != "Transcode")
            {
                return; // simple aperçu : pas noté
            }

            lock (_verrou)
            {
                var liste = AVenir.Lire<List<LectureJournal>>("journal.json") ?? new List<LectureJournal>();
                liste.Insert(0, l);
                if (liste.Count > Garder)
                {
                    liste.RemoveRange(Garder, liste.Count - Garder);
                }

                AVenir.Ecrire("journal.json", liste);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Journal : fin de lecture non notée");
        }
    }

    private static string Titre(BaseItem i) => i switch
    {
        Episode ep => (ep.SeriesName ?? string.Empty) + " — " +
                      (ep.ParentIndexNumber is int s ? "S" + s.ToString("00", CultureInfo.InvariantCulture) : string.Empty) +
                      (ep.IndexNumber is int n ? "E" + n.ToString("00", CultureInfo.InvariantCulture) : string.Empty) + " " + (ep.Name ?? string.Empty),
        _ => (i.Name ?? string.Empty) + (i.ProductionYear is int a ? " (" + a.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty)
    };

    /// <summary>Méthode de lecture, raisons du transcodage, source → sortie.</summary>
    private static void Completer(LectureJournal l, PlaybackProgressEventArgs e)
    {
        var s = e.Session;
        if (s == null)
        {
            return;
        }

        var methode = s.PlayState == null ? string.Empty : Convert.ToString(s.PlayState.PlayMethod, CultureInfo.InvariantCulture) ?? string.Empty;
        var t = s.TranscodingInfo;
        if (t != null && (!t.IsVideoDirect || !t.IsAudioDirect))
        {
            methode = "Transcode";
        }

        // Une lecture qui a transcodé à un moment reste « transcodée »
        if (l.Methode != "Transcode" && methode.Length > 0)
        {
            l.Methode = methode;
        }

        IReadOnlyList<MediaStream> flux = Array.Empty<MediaStream>();
        try
        {
            flux = e.Item?.GetMediaStreams() ?? flux;
        }
        catch
        {
            // pas de flux connus (livre audio…)
        }

        var v = flux.FirstOrDefault(x => x.Type == MediaStreamType.Video);
        var a = s.PlayState?.AudioStreamIndex is int ia ? flux.FirstOrDefault(x => x.Type == MediaStreamType.Audio && x.Index == ia) : flux.FirstOrDefault(x => x.Type == MediaStreamType.Audio && x.IsDefault) ?? flux.FirstOrDefault(x => x.Type == MediaStreamType.Audio);
        var st = s.PlayState?.SubtitleStreamIndex is int isub && isub >= 0 ? flux.FirstOrDefault(x => x.Type == MediaStreamType.Subtitle && x.Index == isub) : null;

        var srcVideo = v == null ? string.Empty : Join((v.Codec ?? "?").ToUpperInvariant(), v.BitDepth is int bd && bd > 8 ? bd + " bits" : string.Empty,
            v.Height is int h ? h + "p" : string.Empty, v.VideoRangeType.ToString() is var rt && rt != "SDR" && rt != "Unknown" ? rt : string.Empty);
        var srcAudio = a == null ? string.Empty : Join((a.Codec ?? "?").ToUpperInvariant(), a.Channels is int c ? Canaux(c) : string.Empty, a.Language ?? string.Empty);

        if (t != null)
        {
            l.Video = t.IsVideoDirect ? srcVideo + " (direct)" : srcVideo + " → " + Join((t.VideoCodec ?? "?").ToUpperInvariant(), t.Height is int th ? th + "p" : string.Empty);
            l.Audio = t.IsAudioDirect ? srcAudio + " (direct)" : srcAudio + " → " + Join((t.AudioCodec ?? "?").ToUpperInvariant(), t.AudioChannels is int tc ? Canaux(tc) : string.Empty);
            l.Debit = t.Bitrate;
            var hw = Convert.ToString(t.HardwareAccelerationType, CultureInfo.InvariantCulture) ?? string.Empty;
            l.Materiel = !t.IsVideoDirect ? (hw.Length == 0 || hw.Equals("none", StringComparison.OrdinalIgnoreCase) ? "Processeur (sans accélération)" : hw.ToUpperInvariant()) : string.Empty;
            var raisons = t.TranscodeReasons.ToString();
            if (raisons != "0" && raisons.Length > 0)
            {
                foreach (var r in raisons.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!l.Raisons.Contains(r))
                    {
                        l.Raisons.Add(r);
                    }
                }
            }
        }
        else if (l.Video.Length == 0)
        {
            l.Video = srcVideo;
            l.Audio = srcAudio;
        }

        if (st != null)
        {
            var image = (st.Codec ?? string.Empty).Contains("pgs", StringComparison.OrdinalIgnoreCase) || (st.Codec ?? string.Empty).Contains("dvd", StringComparison.OrdinalIgnoreCase)
                        || (st.Codec ?? string.Empty).Contains("vobsub", StringComparison.OrdinalIgnoreCase);
            l.SousTitres = Join((st.Codec ?? "?").ToUpperInvariant(), st.Language ?? string.Empty, st.IsForced ? "forcés" : string.Empty, image && t != null && !t.IsVideoDirect ? "incrustés" : string.Empty);
        }
    }

    private static string Canaux(int c) => c switch { 1 => "mono", 2 => "stéréo", 6 => "5.1", 8 => "7.1", _ => c + " canaux" };

    private static string Join(params string[] morceaux) => string.Join(' ', morceaux.Where(m => !string.IsNullOrWhiteSpace(m)));
}
