namespace Jellyfin.Plugin.Dtdd.Services;

public class UserPreferenceStore
{
    // Phase 2: JSON file in plugin data directory, mutex-locked read-modify-write.
    // Schema: { "<userGuid>": { "phobiaTopicIds": [int, ...] } }
    // Methods: Get(userGuid), Put(userGuid, prefs), GetAll().
}
