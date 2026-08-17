# Changelog

Patch notes for the DoesTheDogDie Jellyfin plugin.

The section body for the released version is published verbatim to the plugin
catalog by `.github/workflows/release.yml`, so this file is what users read in
**Dashboard → Plugins → Catalog**. Write it for them, not for developers.

Headings must be `## v<4-part-version>` to be picked up.

## v0.2.1.0

Two fixes, both of which could stop the plugin working for you.

- **"Save phobias" now actually scans your library.** The scan that runs after
  you save your phobia list was being cancelled the moment the save request
  finished, so it only ever got through an item or two before stopping
  silently. It now runs to completion, which means badges start filling in
  right after you save instead of waiting for the weekly task — which is off
  by default, so for most installs nothing was filling them in at all.
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
