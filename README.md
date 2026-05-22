# jellyfin-plugin-dtdd

DoesTheDogDie.com content warnings for Jellyfin, with per-user phobia filtering.

Surfaces a **Safe** / **Not Safe** badge on the item detail page, computed per Jellyfin user from the user's configured phobia topic list. Strict threshold: any single YES vote on any of the user's selected phobias = Not Safe.

## Status

Pre-release scaffold. No installable build yet — Phases 2–4 are still in flight.

- Plugin GUID: `4479e434-651e-48f7-a2ee-bec0bdadec5e`
- Target Jellyfin ABI: `10.11.0.0`
- .NET target: `net9.0`

## Prerequisites

This plugin requires the [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) plugin to render the item-page badge. Without it the backend API still works but no UI is rendered.

Install order:

1. JavaScript Injector
2. This plugin

## Install (placeholder — wired up in Phase 4)

Once releases are live, add the manifest URL to Jellyfin's plugin catalog (Dashboard → Plugins → Catalog → ⚙ → ➕):

```text
https://zigerus.github.io/jellyfin-plugin-dtdd/manifest.json
```

## Configuration (placeholder — wired up in Phase 3)

1. Get a DoesTheDogDie API key at <https://www.doesthedogdie.com> (free account → request API access).
2. Dashboard → Plugins → DoesTheDogDie → paste API key → save.
3. On any item detail page, click "Configure your phobia list" → pick topics → save. Each Jellyfin user has their own phobia list.

## Screenshots

_Placeholder — to be added after Phase 3 lands the UI._

## Development

### Build

```bash
dotnet build
```

Output DLL: `Jellyfin.Plugin.Dtdd/bin/Debug/net9.0/Jellyfin.Plugin.Dtdd.dll`

### Sideload onto a running Jellyfin server

While Phase 4 (release pipeline + manifest hosting) is deferred, the development cycle is: build → package → drop the DLL into Jellyfin's plugins dir → restart Jellyfin → verify in Dashboard → Plugins.

The Servarr host (192.168.50.129) runs `lscr.io/linuxserver/jellyfin:latest` with a bind mount at `/home/zigerus/appdata/jellyfin` → `/config`. So Jellyfin reads plugins from `/home/zigerus/appdata/jellyfin/plugins/` on the host filesystem.

**One-shot sideload via the helper script:**

```bash
./scripts/sideload.sh
```

The script:

1. Runs `dotnet build -c Release`.
2. Reads `<Version>` from `Directory.Build.props`.
3. Bundles the DLL + a generated `meta.json` into `Jellyfin.Plugin.Dtdd_<version>.zip`.
4. `scp`'s the zip to `servarr:/tmp/`.
5. Unpacks into `/home/zigerus/appdata/jellyfin/plugins/DoesTheDogDie_<version>/`.
6. **Stops and prompts for explicit confirmation** before restarting Jellyfin (production restart is destructive per the strict-gate rule in [zigerusgames/CLAUDE.md](../zigerusgames/CLAUDE.md)).
7. On confirm: `ssh servarr docker restart jellyfin`.

**Manual sideload** (if the script doesn't fit a case):

```bash
# On HP Mini
dotnet build -c Release
VERSION="$(grep -oP '<Version>\K[^<]+' Directory.Build.props)"
mkdir -p /tmp/dtdd-pkg
cp Jellyfin.Plugin.Dtdd/bin/Release/net9.0/Jellyfin.Plugin.Dtdd.dll /tmp/dtdd-pkg/
# write meta.json (see scripts/sideload.sh for the canonical template)
cd /tmp/dtdd-pkg && zip "Jellyfin.Plugin.Dtdd_$VERSION.zip" Jellyfin.Plugin.Dtdd.dll meta.json
scp "Jellyfin.Plugin.Dtdd_$VERSION.zip" servarr:/tmp/

# On Servarr (matches existing parent-dir ownership zigerus:zigerus; container can read it)
ssh servarr "mkdir -p /home/zigerus/appdata/jellyfin/plugins/DoesTheDogDie_$VERSION && cd /home/zigerus/appdata/jellyfin/plugins/DoesTheDogDie_$VERSION && unzip -o /tmp/Jellyfin.Plugin.Dtdd_$VERSION.zip && rm /tmp/Jellyfin.Plugin.Dtdd_$VERSION.zip"

# Confirm before restarting — this WILL interrupt Jellyfin streams
ssh servarr 'docker restart jellyfin'
```

**Verify the install:** Jellyfin Dashboard → Plugins. `DoesTheDogDie 0.1.0.0` should appear. Jellyfin logs (`docker logs jellyfin --tail 100`) will show plugin load on startup; look for the GUID `4479e434-651e-48f7-a2ee-bec0bdadec5e`.

**Cleanup an old version:** Jellyfin loads every subdirectory under `plugins/`, so when a new version comes in, remove the previous folder to avoid duplicate-instance warnings:

```bash
ssh servarr 'rm -rf /home/zigerus/appdata/jellyfin/plugins/DoesTheDogDie_0.0.x'
```

## Known limitations (v1)

- **The DTDD badge in the external-IDs row is populated by a side-effect of `/safety` GET calls and the prefetch task** rather than by Jellyfin's normal metadata-refresh cycle. The plugin writes `ProviderIds["Dtdd"]` once per item the first time a safety lookup or prefetch resolves it. This is a v1 shortcut — v1.x will migrate to a true `IRemoteMetadataProvider<Movie>` / `IRemoteMetadataProvider<Series>` pair so the ID gets written during Jellyfin's normal "Refresh metadata" flow. The shortcut is gated: a ProviderId already on an item (correct or stale) is never overwritten.
- **Prefetch is OFF by default.** Until you enable it on the plugin config page, badges only appear after a user opens an item's detail page (which triggers a safety lookup) — and only for that item. Bulk warming requires flipping the prefetch toggle.
- **Cache is keyed by TMDB ID.** Items in your library without a TMDB ProviderId (rare for movies/TV from standard scanners, common for home videos) still get a live DTDD lookup but don't get cached. Subsequent calls re-hit DTDD.
- **DTDD has no episode-level data** (per-title only). The plugin only operates on Movies and Series; Season and Episode items are ignored.
- **PUT /DTDD/prefs trusts the client.** The picker UI sources topic IDs from `GET /DTDD/topics`, so the only way to send a nonsense ID is direct API access — and unrecognised IDs simply never match anything in the safety lookup (harmless). A 500-element length cap bounds memory from a buggy or malicious caller. See the controller's `PutPrefs` doc comment for rationale.

## Roadmap (post-v1)

- v2: "Why?" modal on the Not Safe badge — clicking the badge opens a modal listing each matched phobia with its top community comment and total comment count. The data is already in the v1 API response; just needs the UI.
- v2: Episode-level data is not available from DoesTheDogDie (per-title only). The comment preview will stay at show/movie granularity.
- v1.x: ship `IRemoteMetadataProvider<Movie>` / `IRemoteMetadataProvider<Series>` so the Dtdd ProviderId gets written during normal metadata refresh and the v1 backfill-on-read shortcut can be removed.

## License

MIT. See [LICENSE](LICENSE).
