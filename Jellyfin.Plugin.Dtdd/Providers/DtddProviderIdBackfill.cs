using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Providers;

/// <summary>
/// Single source of truth for writing the resolved DTDD media ID into a Jellyfin
/// item's <c>ProviderIds</c> dictionary. Used by both the controller (lazy backfill
/// on /safety hit) and the prefetch task (eager backfill during scheduled scan).
///
/// <para>
/// v1 shortcut: we backfill via direct ProviderIds + UpdateToRepositoryAsync rather
/// than shipping a full <c>IRemoteMetadataProvider&lt;Movie&gt;</c> /
/// <c>IRemoteMetadataProvider&lt;Series&gt;</c> pair (theflanman's plugin ships those —
/// roughly 4 extra provider files plus their wiring). Migrating to true metadata
/// providers is a v1.x cleanup so DTDD IDs get written during Jellyfin's normal
/// metadata-refresh cycle instead of as a side-effect of a read endpoint. Tracking
/// issue: see README "Known limitations".
/// </para>
///
/// <para>
/// Strict gate: only writes when the ProviderId is not already set. Never overwrites
/// an existing value, even if it differs from the resolved one — manually-curated
/// metadata wins. Returns true when a write happened; exceptions are caught and
/// logged at Debug (a missing badge is acceptable, a broken caller path is not).
/// </para>
/// </summary>
internal static class DtddProviderIdBackfill
{
    public static async Task<bool> TryBackfillAsync(
        BaseItem item,
        int dtddId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existing = item.GetProviderId(DtddConstants.ProviderId);
        if (!string.IsNullOrEmpty(existing))
        {
            // Strict gate: any pre-existing value (correct or stale) wins. v1.x with
            // IRemoteMetadataProvider can revisit this — until then we accept that a
            // stale ProviderId stays stale until manually cleared from the item edit UI.
            return false;
        }

        try
        {
            item.SetProviderId(DtddConstants.ProviderId, dtddId.ToString(CultureInfo.InvariantCulture));
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist Dtdd ProviderId for {Title}; badge will retry on next pass", item.Name);
            return false;
        }
    }
}
