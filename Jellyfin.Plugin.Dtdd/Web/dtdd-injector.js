// dtdd-injector.js — the DoesTheDogDie badge for Jellyfin item detail pages.
//
// Loaded into Jellyfin's web UI by n00bcodr/Jellyfin-JavaScript-Injector.
// Renders ONLY the Safe / Not Safe / Unknown / Configure badge on item
// detail pages. The phobia picker lives in the plugin's config page
// (accessible via the Jellyfin sidebar entry, per-user).
//
// not_configured CTA badge is clickable — navigates to the config page so
// the user can pick topics; the rest of the badges are informational.

(function () {
    'use strict';

    var NS = 'dtdd';
    function log() { try { console.log.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} }
    function warn() { try { console.warn.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} }

    var CONFIG_PAGE_HASH = '#!/configurationpage?name=DoesTheDogDie';

    // ----- API helper (GET-only — no PUT/DELETE from the badge anymore) -----

    function api(path) {
        if (window.ApiClient && typeof window.ApiClient.ajax === 'function' && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.ajax({
                type: 'GET',
                url: window.ApiClient.getUrl(path),
                dataType: 'json'
            });
        }
        return fetch('/' + path, { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
            .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); });
    }

    // ----- Styles -----

    function ensureStyles() {
        if (document.getElementById('dtdd-style')) return;
        var style = document.createElement('style');
        style.id = 'dtdd-style';
        style.textContent = [
            '.dtdd-badge-wrapper { display: inline-flex; align-items: center; margin: 0 0.5em; vertical-align: middle; }',
            '.dtdd-badge { display: inline-flex; align-items: center; padding: 0.15em 0.55em; border-radius: 0.35em; font-size: 0.85em; border: 1px solid currentColor; line-height: 1.3; }',
            '.dtdd-badge.dtdd-clickable { cursor: pointer; text-decoration: none; }',
            '.dtdd-badge.dtdd-clickable:hover { opacity: 0.85; }',
            '.dtdd-safe { color: var(--theme-success-color, #43a047); }',
            '.dtdd-not-safe { color: var(--theme-error-color, #e53935); }',
            '.dtdd-unknown { color: var(--theme-text-color, currentColor); opacity: 0.55; }',
            '.dtdd-not-configured { color: var(--theme-accent-color, currentColor); }'
        ].join('\n');
        document.head.appendChild(style);
    }

    // ----- Detection + render -----

    var ITEM_ID_RE = /[?&]id=([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}|[0-9a-f]{32})/i;
    var lastItemId = null;

    function extractItemId() {
        var hash = window.location.hash || '';
        var m = hash.match(ITEM_ID_RE);
        return m ? m[1].replace(/-/g, '') : null;
    }

    function findContainer() {
        return document.querySelector('.itemMiscInfo-primary')
            || document.querySelector('.mainDetailButtons')
            || document.querySelector('.itemBackdrop + .detailPageContent .itemMiscInfo');
    }

    async function waitForContainer(timeoutMs) {
        var deadline = Date.now() + (timeoutMs || 3000);
        while (Date.now() < deadline) {
            var c = findContainer();
            if (c && c.isConnected) return c;
            await new Promise(function (r) { setTimeout(r, 100); });
        }
        return null;
    }

    async function onViewShow() {
        var itemId = extractItemId();
        if (!itemId) return;
        if (itemId === lastItemId) return;
        lastItemId = itemId;

        var container = await waitForContainer();
        if (!container) return;

        await renderBadge(itemId, container);
    }

    async function renderBadge(itemId, container) {
        var existing = container.querySelectorAll('.dtdd-badge-wrapper');
        for (var i = 0; i < existing.length; i++) existing[i].remove();

        var safety;
        try {
            safety = await api('DTDD/safety/' + itemId);
        } catch (err) {
            warn('safety fetch failed for', itemId, err);
            return;
        }

        var wrapper = document.createElement('span');
        wrapper.className = 'dtdd-badge-wrapper';

        var badge;
        switch (safety.state) {
            case 'safe':
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-safe';
                badge.textContent = 'DTDD: Safe';
                break;

            case 'not_safe':
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-not-safe';
                var n = (safety.matchedPhobias || []).length;
                badge.textContent = 'DTDD: Not Safe (' + n + ' match' + (n === 1 ? '' : 'es') + ')';
                if (safety.matchedPhobias && safety.matchedPhobias.length) {
                    badge.title = safety.matchedPhobias.map(function (p) { return p.name; }).join(', ');
                }
                break;

            case 'unknown':
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-unknown';
                badge.textContent = 'DTDD: Not in database';
                break;

            case 'not_configured':
                // Click navigates to the config page where the picker lives.
                // No inline picker on detail pages.
                badge = document.createElement('a');
                badge.className = 'dtdd-badge dtdd-not-configured dtdd-clickable';
                badge.textContent = 'Configure your phobia list';
                badge.title = 'Opens the DoesTheDogDie plugin page';
                badge.href = CONFIG_PAGE_HASH;
                break;

            default:
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-unknown';
                badge.textContent = 'DTDD: ' + safety.state;
        }

        wrapper.appendChild(badge);
        container.appendChild(wrapper);
    }

    // ----- Entry point -----

    function init() {
        ensureStyles();
        document.addEventListener('viewshow', onViewShow);
        if (extractItemId()) onViewShow();
        log('injector active (badge only — picker lives in config page)');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
