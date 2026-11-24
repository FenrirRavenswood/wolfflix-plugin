using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WolfFlix.ScheduledTasks;

/// <summary>Tâche planifiée : analyse forcé / complet des sous-titres français, chaque nuit (3 h au plus).</summary>
public class SousTitresTache : IScheduledTask
{
    private readonly AnalyseSousTitres _analyse;

    public SousTitresTache(AnalyseSousTitres analyse)
    {
        _analyse = analyse;
    }

    public string Name => "Analyse des sous-titres";

    public string Key => "WolfFlixSousTitres";

    public string Description => "Classe les sous-titres français en « forcés » ou « complets » d'après leur nombre de répliques. Commence par les vidéos jamais analysées et s'arrête au bout de 3 h ; la nuit suivante reprend où elle s'est arrêtée.";

    public string Category => "WolfFlix";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _analyse.ToutAnalyser(progress, TimeSpan.FromHours(3), cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }
        };
    }
}
