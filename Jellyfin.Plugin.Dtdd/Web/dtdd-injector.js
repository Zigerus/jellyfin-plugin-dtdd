// Phase 3 will replace this stub with the user-facing badge renderer.
//
// Behavior (per spec):
//   - Detect navigation to an item detail page (SPA routing hook)
//   - GET /DTDD/safety/{jellyfinItemId} → server-resolved verdict
//   - Render badge by state:
//       not_configured → "Configure your phobia list" CTA
//       unknown        → "Not in DTDD database" (muted)
//       safe           → "Safe" (green)
//       not_safe       → "Not Safe (N matches)" (red)
//   - Gear icon next to the badge opens the phobia picker modal
//   - Picker fetches GET /DTDD/topics, groups by TopicCategory, searchable,
//     saves via PUT /DTDD/prefs, then re-fetches safety for the current item.
//   - Styling via Jellyfin theme CSS variables only — no hardcoded colors.
console.log('[DTDD] injector loaded (Phase 1 stub)');
