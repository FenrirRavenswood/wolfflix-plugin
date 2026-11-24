using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WolfFlix.ScheduledTasks;

/// <summary>Tâche planifiée : « À venir » et « Ce qui manque », chaque nuit.</summary>
public class AVenirTache : IScheduledTask
{
    private readonly AVenir _aVenir;

    public AVenirTache(AVenir aVenir)
    {
        _aVenir = aVenir;
    }

    public string Name => "À venir et Ce qui manque";

    public string Key => "WolfFlixAVenir";

    public string Description => "Compare les sagas de films et les séries de la médiathèque à TMDB : prochaines sorties (dates françaises quand elles existent) et titres manquants.";

    public string Category => "WolfFlix";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _aVenir.MettreAJour(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }
        };
    }
}
