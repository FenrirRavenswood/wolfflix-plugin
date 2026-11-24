using Jellyfin.Plugin.WolfFlix.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WolfFlix.ScheduledTasks;

/// <summary>Tâche planifiée : notes IMDb de la médiathèque, chaque nuit.</summary>
public class NotesImdbTache : IScheduledTask
{
    private readonly NotesImdb _notes;

    public NotesImdbTache(NotesImdb notes)
    {
        _notes = notes;
    }

    public string Name => "Notes IMDb";

    public string Key => "WolfFlixNotesImdb";

    public string Description => "Récupère chaque jour les notes IMDb des films, séries et épisodes de la médiathèque, et leur place dans les classements IMDb.";

    public string Category => "WolfFlix";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _notes.MettreAJour(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks }
        };
    }
}
