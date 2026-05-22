namespace Jellyfin.Plugin.Dtdd.Services;

public class DtddClient
{
    // Phase 2: HttpClient wrapper with X-API-KEY header, retry on transient failures,
    // respect any rate-limit headers DTDD returns. IMDB-keyed /dddsearch lookups,
    // /media/{id} fetches.
}
