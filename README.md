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

```
https://zigerus.github.io/jellyfin-plugin-dtdd/manifest.json
```

## Configuration (placeholder — wired up in Phase 3)

1. Get a DoesTheDogDie API key at https://www.doesthedogdie.com (free account → request API access).
2. Dashboard → Plugins → DoesTheDogDie → paste API key → save.
3. On any item detail page, click "Configure your phobia list" → pick topics → save. Each Jellyfin user has their own phobia list.

## Screenshots

_Placeholder — to be added after Phase 3 lands the UI._

## Development

```bash
dotnet build
```

Output DLL: `Jellyfin.Plugin.Dtdd/bin/Debug/net9.0/Jellyfin.Plugin.Dtdd.dll`

## Roadmap (post-v1)

- v2: "Why?" modal on the Not Safe badge — clicking the badge opens a modal listing each matched phobia with its top community comment and total comment count. The data is already in the v1 API response; just needs the UI.
- v2: Episode-level data is not available from DoesTheDogDie (per-title only). The comment preview will stay at show/movie granularity.

## License

MIT. See [LICENSE](LICENSE).
