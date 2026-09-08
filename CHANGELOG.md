# Changelog

Patch notes for the DoesTheDogDie Jellyfin plugin.

The section body for the released version is published verbatim to the plugin
catalog by `.github/workflows/release.yml`, so this file is what users read in
**Dashboard → Plugins → Catalog**. Write it for them, not for developers.

Headings must be `## v<4-part-version>` to be picked up.

## v0.3.0.0

- **Works on Jellyfin 12.** Jellyfin 12 moved to .NET 10 and changed several
  plugin interfaces, so plugins built for 10.11 do not load on it. Every
  release now ships two builds — one for Jellyfin 12 and one for Jellyfin
  10.11 — and the catalog offers your server the right one automatically.
  Nothing else changes: your DoesTheDogDie API key, cached verdicts, and each
  user's phobia list are all kept across the upgrade.
- **Still requires JavaScript Injector.** On Jellyfin 12 you need JavaScript
  Injector 4.0.0.0 or newer installed from its Jellyfin 12 repository
  (`https://raw.githubusercontent.com/n00bcodr/jellyfin-plugins/main/12/manifest.json`);
  the badge does not render without it.

## v0.2.1.0

Two fixes, both of which could stop the plugin working for you.

- **Fixed the background library scan stopping almost as soon as it started.** The
  scan behind the plugin's scan endpoint and the "Prefetch DoesTheDogDie
  warnings" task was tied to the HTTP request that triggered it, so it was
  cancelled the moment that request completed and only ever processed an item
  or two. It now runs to completion. Note that saving your phobia list does not
  start a scan: badges fill in as you open items, or in bulk via the weekly
  task (off by default; an admin can enable it or run it once from Dashboard →
  Scheduled Tasks).
- **Fixed installing on Jellyfin 10.11.0 through 10.11.7.** The plugin
  advertised itself as compatible with Jellyfin 10.11.0 and up, but was built
  against 10.11.8, so the catalog offered it to servers where it then failed
  to load. It is now built against 10.11.0 and works across the whole 10.11.x
  line. If the plugin previously refused to load for you, this release fixes
  it — no configuration changes needed, your cache and phobia lists are kept.

## v0.2.0.0

- **Badges now explain themselves.** Hovering a verdict badge lists each
  matched phobia on its own line with the community's yes/no vote counts, and
  selecting the badge opens the same breakdown as a dialog — useful on touch
  screens and TVs, where there is no hover.
- **Works with a TV remote.** Verdict badges are now reachable with the
  directional pad, and the remote's Back button closes plugin dialogs instead
  of navigating away from the item page and stranding them on screen.
- **Finds far more of your library.** Title lookups moved to DoesTheDogDie's
  v3 API and now try exact IMDB and TMDB matches before falling back to a
  title search, which resolves many titles the old fuzzy search missed —
  anime series especially.
- **The phobia picker is fully categorised.** The topic list is now seeded in
  one pass from the full DoesTheDogDie catalog, so every topic appears under
  its proper category instead of collecting in "Uncategorized" over time.

## v0.1.0.0

Initial release.

- Per-user **Safe** / **Not Safe** / **Not in database** badge on movie and TV
  detail pages, computed from each Jellyfin user's own phobia topic list.
- Per-user phobia picker under user Settings, so household members configure
  their own list without needing admin access.
- Local cache of DoesTheDogDie lookups with a configurable TTL, plus an
  optional weekly prefetch task for warming the whole library.
- DoesTheDogDie link in Jellyfin's external-IDs row for resolved titles.
