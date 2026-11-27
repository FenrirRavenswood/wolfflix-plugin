using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WolfFlix.ScheduledTasks;

/// <summary>Tâche planifiée : bilan de santé du serveur (dossiers disparus, place sur les disques…), toutes les 6 heures.</summary>
public class SanteTache : IScheduledTask
{
    private readonly Sante _sante;

    public SanteTache(Sante sante)
    {
        _sante = sante;
    }

    public string Name => "Santé du serveur";

    public string Key => "WolfFlixSante";

    public string Description => "Place sur les disques, dossiers disparus des bibliothèques, affiches manquantes, doublons, films sans VF et fichiers à réencoder en priorité.";

    public string Category => "WolfFlix";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _sante.Calculer(progress, cancellationToken);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }
        };
    }
}
