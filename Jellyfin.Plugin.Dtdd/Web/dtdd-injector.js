// dtdd-injector.js — the DoesTheDogDie badge + phobia picker for Jellyfin web.
//
// Loaded into Jellyfin's web UI by n00bcodr/Jellyfin-JavaScript-Injector.
// Backend lives in Jellyfin.Plugin.Dtdd; routes are under /DTDD/.
//
// Lifecycle:
//   - Listens for `viewshow` (Jellyfin's SPA route change event)
//   - On item detail page: GET /DTDD/safety/{itemId} → render badge
//   - Gear icon next to the badge opens the picker modal regardless of state
//   - Picker: GET /DTDD/topics (cached for the session), grouped by
//     TopicCategory.name, searchable; on save → PUT /DTDD/prefs then
//     re-fetch + re-render the badge for the current item.
//
// All colors come from CSS theme variables with sensible fallbacks; no
// hardcoded brand colors. The picker uses native <dialog> for modal layout.

(function () {
    'use strict';

    var NS = 'dtdd';
    var log = function () { try { console.log.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} };
    var warn = function () { try { console.warn.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} };

    // -----------------------------------------------------------------------
    // API
    // -----------------------------------------------------------------------

    function apiCall(path, opts) {
        opts = opts || {};
        var method = opts.method || 'GET';

        if (window.ApiClient && typeof window.ApiClient.ajax === 'function' && typeof window.ApiClient.getUrl === 'function') {
            var req = {
                type: method,
                url: window.ApiClient.getUrl(path)
            };
            // Only request JSON parsing when we actually expect a JSON body.
            // PUT /DTDD/prefs returns 204 No Content; setting dataType:'json'
            // makes ApiClient.ajax throw with "unexpected end of data" when it
            // tries to JSON.parse the empty response body.
            if (method === 'GET') {
                req.dataType = 'json';
            }
            if (opts.body !== undefined) {
                req.data = JSON.stringify(opts.body);
                req.contentType = 'application/json';
            }
            return window.ApiClient.ajax(req);
        }

        // Fallback (unlikely to authenticate properly, but keeps the script
        // from throwing in test contexts).
        return fetch('/' + path, {
            method: method,
            headers: { Accept: 'application/json' },
            body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
            credentials: 'same-origin'
        }).then(function (r) {
            if (r.status === 204) return null;
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    // -----------------------------------------------------------------------
    // Styles (injected once per page load)
    // -----------------------------------------------------------------------

    function ensureStyles() {
        if (document.getElementById('dtdd-style')) return;
        var style = document.createElement('style');
        style.id = 'dtdd-style';
        style.textContent = [
            '.dtdd-badge-wrapper { display: inline-flex; align-items: center; gap: 0.25em; margin: 0 0.5em; vertical-align: middle; }',
            '.dtdd-badge { display: inline-flex; align-items: center; padding: 0.15em 0.55em; border-radius: 0.35em; font-size: 0.85em; border: 1px solid currentColor; line-height: 1.3; }',
            '.dtdd-badge.dtdd-clickable { cursor: pointer; }',
            '.dtdd-safe { color: var(--theme-success-color, #43a047); }',
            '.dtdd-not-safe { color: var(--theme-error-color, #e53935); }',
            '.dtdd-unknown { color: var(--theme-text-color, currentColor); opacity: 0.55; }',
            '.dtdd-not-configured { color: var(--theme-accent-color, currentColor); }',
            '.dtdd-gear { font-size: 1em; cursor: pointer; background: transparent; color: inherit; border: none; padding: 0 0.25em; opacity: 0.7; }',
            '.dtdd-gear:hover { opacity: 1; }',
            'dialog.dtdd-picker { background: var(--theme-card-background, #1c1c1c); color: var(--theme-text-color, #fff); border: 1px solid var(--theme-card-border-color, rgba(255,255,255,0.15)); border-radius: 0.5em; padding: 1em 1.2em; width: min(640px, 92vw); max-height: 82vh; box-shadow: 0 8px 32px rgba(0,0,0,0.4); }',
            'dialog.dtdd-picker::backdrop { background: rgba(0,0,0,0.55); }',
            '.dtdd-picker-header { display: flex; align-items: baseline; justify-content: space-between; margin: 0 0 0.5em 0; }',
            '.dtdd-picker-header h2 { margin: 0; font-size: 1.15em; }',
            '.dtdd-picker-count { opacity: 0.65; font-size: 0.85em; }',
            '.dtdd-picker-search { width: 100%; padding: 0.5em 0.6em; margin: 0 0 0.5em 0; background: transparent; color: inherit; border: 1px solid currentColor; border-radius: 0.3em; box-sizing: border-box; font: inherit; }',
            '.dtdd-picker-list { max-height: 52vh; overflow-y: auto; padding-right: 0.25em; }',
            '.dtdd-picker-empty { padding: 0.75em 0; opacity: 0.7; text-align: center; }',
            '.dtdd-picker-category { margin: 0.65em 0 0.2em 0; font-weight: 600; opacity: 0.85; font-size: 0.9em; }',
            '.dtdd-picker-topic { display: flex; align-items: center; gap: 0.45em; padding: 0.2em 0; font-size: 0.95em; }',
            '.dtdd-picker-footer { display: flex; gap: 0.5em; justify-content: flex-end; margin-top: 0.85em; padding-top: 0.65em; border-top: 1px solid var(--theme-card-border-color, rgba(255,255,255,0.1)); }',
            '.dtdd-picker-button { padding: 0.45em 1.1em; border-radius: 0.3em; cursor: pointer; font: inherit; border: 1px solid currentColor; background: transparent; color: inherit; }',
            '.dtdd-picker-button.dtdd-primary { background: var(--theme-accent-color, currentColor); color: var(--theme-button-text-color, #fff); border-color: transparent; }'
        ].join('\n');
        document.head.appendChild(style);
    }

    // -----------------------------------------------------------------------
    // View detection
    // -----------------------------------------------------------------------

    var ITEM_ID_RE = /[?&]id=([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}|[0-9a-f]{32})/i;
    var lastItemId = null;
    var topicsCache = null;

    function extractItemId() {
        var hash = window.location.hash || '';
        var m = hash.match(ITEM_ID_RE);
        return m ? m[1].replace(/-/g, '') : null;
    }

    function findContainer() {
        // Jellyfin's item detail page uses several possible metadata containers
        // across versions. Try the most-specific first.
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
        if (!container) {
            // Not an item detail page (or layout we don't recognise); silent skip.
            return;
        }

        await renderBadge(itemId, container);
    }

    // -----------------------------------------------------------------------
    // Badge rendering
    // -----------------------------------------------------------------------

    async function renderBadge(itemId, container) {
        // Clear any previous badge in this container
        var existing = container.querySelectorAll('.dtdd-badge-wrapper');
        for (var i = 0; i < existing.length; i++) existing[i].remove();

        var safety;
        try {
            safety = await apiCall('DTDD/safety/' + itemId);
        } catch (err) {
            warn('safety fetch failed for', itemId, err);
            return;
        }

        var wrapper = document.createElement('span');
        wrapper.className = 'dtdd-badge-wrapper';

        var badge = document.createElement('span');
        badge.className = 'dtdd-badge';

        switch (safety.state) {
            case 'safe':
                badge.classList.add('dtdd-safe');
                badge.textContent = 'DTDD: Safe';
                break;
            case 'not_safe':
                badge.classList.add('dtdd-not-safe');
                var n = (safety.matchedPhobias || []).length;
                badge.textContent = 'DTDD: Not Safe (' + n + ' match' + (n === 1 ? '' : 'es') + ')';
                if (safety.matchedPhobias && safety.matchedPhobias.length) {
                    badge.title = safety.matchedPhobias.map(function (p) { return p.name; }).join(', ');
                }
                break;
            case 'unknown':
                badge.classList.add('dtdd-unknown');
                badge.textContent = 'DTDD: Not in database';
                break;
            case 'not_configured':
                badge.classList.add('dtdd-not-configured', 'dtdd-clickable');
                badge.textContent = 'Configure your phobia list';
                badge.title = 'Open the DoesTheDogDie picker';
                badge.addEventListener('click', function () { openPicker(itemId); });
                break;
            default:
                badge.classList.add('dtdd-unknown');
                badge.textContent = 'DTDD: ' + safety.state;
        }

        wrapper.appendChild(badge);

        var gear = document.createElement('button');
        gear.type = 'button';
        gear.className = 'dtdd-gear';
        gear.setAttribute('aria-label', 'Configure DoesTheDogDie phobia list');
        gear.title = 'Configure DoesTheDogDie phobia list';
        gear.textContent = '⚙';
        gear.addEventListener('click', function () { openPicker(itemId); });
        wrapper.appendChild(gear);

        container.appendChild(wrapper);
    }

    // -----------------------------------------------------------------------
    // Picker modal
    // -----------------------------------------------------------------------

    async function fetchTopics(forceRefresh) {
        if (topicsCache && !forceRefresh) return topicsCache;
        try {
            topicsCache = await apiCall('DTDD/topics') || [];
        } catch (err) {
            warn('topics fetch failed', err);
            topicsCache = [];
        }
        return topicsCache;
    }

    async function fetchPrefs() {
        try {
            var p = await apiCall('DTDD/prefs');
            return (p && Array.isArray(p.phobiaTopicIds)) ? p.phobiaTopicIds : [];
        } catch (err) {
            warn('prefs fetch failed', err);
            return [];
        }
    }

    function groupByCategory(topics) {
        var byCat = {};
        for (var i = 0; i < topics.length; i++) {
            var t = topics[i];
            var cat = (t.TopicCategory && t.TopicCategory.name) || 'Uncategorized';
            if (!byCat[cat]) byCat[cat] = [];
            byCat[cat].push(t);
        }
        return byCat;
    }

    async function openPicker(itemId) {
        var topics = await fetchTopics(false);
        var selectedIds = await fetchPrefs();
        var selected = {};
        for (var i = 0; i < selectedIds.length; i++) selected[selectedIds[i]] = true;

        var dialog = document.createElement('dialog');
        dialog.className = 'dtdd-picker';

        var header = document.createElement('div');
        header.className = 'dtdd-picker-header';
        header.innerHTML = '<h2>Configure your phobia list</h2><span class="dtdd-picker-count"></span>';
        dialog.appendChild(header);
        var countEl = header.querySelector('.dtdd-picker-count');

        var search = document.createElement('input');
        search.type = 'text';
        search.placeholder = 'Search topics...';
        search.className = 'dtdd-picker-search';
        dialog.appendChild(search);

        var list = document.createElement('div');
        list.className = 'dtdd-picker-list';
        dialog.appendChild(list);

        function updateCount() {
            var n = Object.keys(selected).filter(function (k) { return selected[k]; }).length;
            countEl.textContent = n + ' selected';
        }

        function renderList(filter) {
            list.innerHTML = '';
            if (!topics.length) {
                var empty = document.createElement('div');
                empty.className = 'dtdd-picker-empty';
                empty.textContent = 'No topics in catalog yet. The seed task may not have completed — try again in a few minutes, or open a few movies to populate it organically.';
                list.appendChild(empty);
                return;
            }
            var f = (filter || '').toLowerCase().trim();
            var byCat = groupByCategory(topics);
            var catNames = Object.keys(byCat).sort();
            var anyRendered = false;
            for (var i = 0; i < catNames.length; i++) {
                var cat = catNames[i];
                var topicsInCat = byCat[cat];
                var filtered = f
                    ? topicsInCat.filter(function (t) { return (t.name + ' ' + (t.description || '')).toLowerCase().indexOf(f) >= 0; })
                    : topicsInCat;
                if (!filtered.length) continue;
                anyRendered = true;
                filtered.sort(function (a, b) { return a.name.localeCompare(b.name); });

                var catEl = document.createElement('div');
                catEl.className = 'dtdd-picker-category';
                catEl.textContent = cat;
                list.appendChild(catEl);

                for (var j = 0; j < filtered.length; j++) {
                    (function (topic) {
                        var label = document.createElement('label');
                        label.className = 'dtdd-picker-topic';
                        var cb = document.createElement('input');
                        cb.type = 'checkbox';
                        cb.checked = !!selected[topic.id];
                        cb.addEventListener('change', function () {
                            selected[topic.id] = cb.checked;
                            updateCount();
                        });
                        label.appendChild(cb);
                        var span = document.createElement('span');
                        span.textContent = topic.name;
                        label.appendChild(span);
                        if (topic.description) label.title = topic.description;
                        list.appendChild(label);
                    })(filtered[j]);
                }
            }
            if (!anyRendered) {
                var none = document.createElement('div');
                none.className = 'dtdd-picker-empty';
                none.textContent = 'No matches for "' + (filter || '') + '"';
                list.appendChild(none);
            }
        }

        updateCount();
        renderList('');
        search.addEventListener('input', function (e) { renderList(e.target.value); });

        var footer = document.createElement('div');
        footer.className = 'dtdd-picker-footer';

        var cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.textContent = 'Cancel';
        cancelBtn.className = 'dtdd-picker-button';
        cancelBtn.addEventListener('click', function () { dialog.close(); });

        var saveBtn = document.createElement('button');
        saveBtn.type = 'button';
        saveBtn.textContent = 'Save';
        saveBtn.className = 'dtdd-picker-button dtdd-primary';
        saveBtn.addEventListener('click', async function () {
            var ids = Object.keys(selected).filter(function (k) { return selected[k]; }).map(function (k) { return Number(k); });
            if (ids.length === 0) {
                var ok = window.confirm(
                    'Saving will disable filtering — every item will show "Safe".\n\n' +
                    'Save anyway, or cancel and pick at least one topic?'
                );
                if (!ok) return;
            }
            saveBtn.disabled = true;
            try {
                await apiCall('DTDD/prefs', { method: 'PUT', body: { phobiaTopicIds: ids } });
                dialog.close();
                lastItemId = null; // invalidate so renderBadge runs again
                var container = findContainer();
                if (container) await renderBadge(itemId, container);
            } catch (err) {
                warn('prefs save failed', err);
                window.alert('Save failed: ' + (err && err.message ? err.message : err));
                saveBtn.disabled = false;
            }
        });

        footer.appendChild(cancelBtn);
        footer.appendChild(saveBtn);
        dialog.appendChild(footer);

        document.body.appendChild(dialog);
        dialog.addEventListener('close', function () { dialog.remove(); });
        if (typeof dialog.showModal === 'function') {
            dialog.showModal();
        } else {
            // Very old browsers — fall back to visible block
            dialog.setAttribute('open', '');
        }
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------

    function init() {
        ensureStyles();
        document.addEventListener('viewshow', onViewShow);
        // Process the current view in case viewshow fired before we attached.
        if (extractItemId()) onViewShow();
        log('injector active');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
