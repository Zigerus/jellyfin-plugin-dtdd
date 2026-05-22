namespace Jellyfin.Plugin.Dtdd.ScheduledTasks;

public class PrefetchWarningsTask
{
    // Phase 2: implement IScheduledTask. Iterates library items with TMDB IDs,
    // fetches+caches missing warnings. Runs only when PrefetchEnabled toggle
    // is on (default false). Politer to DTDD until cache is verified stable.
}
