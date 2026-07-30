// dtdd-injector.js — DoesTheDogDie badge + picker for Jellyfin web.
//
// Loaded by n00bcodr/Jellyfin-JavaScript-Injector. Four responsibilities:
//   1. Render a Safe / Not Safe / Unknown / not_configured badge on item
//      detail pages.
//   2. Inject a "DoesTheDogDie" entry into the user-facing Settings page
//      (so non-admin users can pick their phobias without touching the
//      admin Dashboard).
//   3. Open the phobia picker modal from either (a) the Settings entry,
//      or (b) the not_configured CTA badge.
//   4. Details on demand for Safe / Not Safe badges: hover tooltip lists one
//      matched phobia per line with yes/no vote counts; click (or
//      Enter/Space) opens a small dialog with the same data, styled like the
//      picker (touch/TV users have no hover).
//
// Picker save flow: PUT /DTDD/prefs → POST /DTDD/scan (background library
// warm) → close modal → re-render badge for current item.
//
// All styling via Jellyfin theme CSS variables with sensible fallbacks.

(function () {
    'use strict';

    var NS = 'dtdd';
    var DEBUG = true;
    function log() { try { console.log.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} }
    function warn() { try { console.warn.apply(console, ['[' + NS + ']'].concat([].slice.call(arguments))); } catch (e) {} }

    // -----------------------------------------------------------------------
    // API
    // -----------------------------------------------------------------------

    function api(path, opts) {
        opts = opts || {};
        var method = opts.method || 'GET';
        if (window.ApiClient && typeof window.ApiClient.ajax === 'function' && typeof window.ApiClient.getUrl === 'function') {
            var req = {
                type: method,
                url: window.ApiClient.getUrl(path)
            };
            if (method === 'GET') req.dataType = 'json';
            if (opts.body !== undefined) {
                req.data = JSON.stringify(opts.body);
                req.contentType = 'application/json';
            }
            return window.ApiClient.ajax(req);
        }
        var init = { method: method, headers: { Accept: 'application/json' }, credentials: 'same-origin' };
        if (opts.body !== undefined) {
            init.body = JSON.stringify(opts.body);
            init.headers['Content-Type'] = 'application/json';
        }
        return fetch('/' + path, init).then(function (r) {
            if (r.status === 204) return null;
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    // -----------------------------------------------------------------------
    // Styles
    // -----------------------------------------------------------------------

    function ensureStyles() {
        if (document.getElementById('dtdd-style')) return;
        var style = document.createElement('style');
        style.id = 'dtdd-style';
        style.textContent = [
            '.dtdd-badge-wrapper { display: inline-flex; align-items: center; margin: 0 0.5em 0 0; vertical-align: middle; }',
            '.dtdd-badge { display: inline-flex; align-items: center; padding: 0.15em 0.55em; border-radius: 0.35em; font-size: 0.85em; border: 1px solid currentColor; line-height: 1.3; }',
            // Clickable badges are real <button>s so Jellyfin\'s TV focus
            // manager (arrow-key / D-pad navigation) picks them up; reset the
            // UA button chrome so they render identically to the old spans.
            'button.dtdd-badge { background: transparent; font: inherit; font-size: 0.85em; margin: 0; appearance: none; -webkit-appearance: none; }',
            '.dtdd-badge.dtdd-clickable { cursor: pointer; text-decoration: none; }',
            '.dtdd-badge.dtdd-clickable:hover { opacity: 0.85; }',
            // Remote/keyboard users need to SEE focus; on pointer clicks the
            // details dialog steals focus immediately so this barely flashes.
            '.dtdd-badge.dtdd-clickable:focus { outline: 2px solid currentColor; outline-offset: 2px; }',
            '.dtdd-safe { color: var(--theme-success-color, #43a047); }',
            '.dtdd-not-safe { color: var(--theme-error-color, #e53935); }',
            '.dtdd-unknown { color: var(--theme-text-color, currentColor); opacity: 0.55; }',
            '.dtdd-not-configured { color: var(--theme-accent-color, currentColor); }',
            'dialog.dtdd-picker { background: var(--theme-card-background, #1c1c1c); color: var(--theme-text-color, #fff); border: 1px solid var(--theme-card-border-color, rgba(255,255,255,0.15)); border-radius: 0.5em; padding: 1em 1.2em; width: min(640px, 92vw); max-height: 82vh; box-shadow: 0 8px 32px rgba(0,0,0,0.4); }',
            'dialog.dtdd-picker::backdrop { background: rgba(0,0,0,0.55); }',
            '.dtdd-picker-header { display: flex; align-items: baseline; justify-content: space-between; margin: 0 0 0.5em 0; }',
            '.dtdd-picker-header h2 { margin: 0; font-size: 1.15em; }',
            '.dtdd-picker-count { opacity: 0.65; font-size: 0.85em; }',
            '.dtdd-picker-search { width: 100%; padding: 0.5em 0.6em; margin: 0 0 0.5em 0; background: transparent; color: inherit; border: 1px solid currentColor; border-radius: 0.3em; box-sizing: border-box; font: inherit; }',
            '.dtdd-picker-list { max-height: 48vh; overflow-y: auto; padding-right: 0.25em; }',
            '.dtdd-picker-status { margin-top: 0.5em; min-height: 1.4em; font-size: 0.9em; opacity: 0.8; }',
            '.dtdd-picker-empty { padding: 0.75em 0; opacity: 0.7; text-align: center; }',
            '.dtdd-picker-category { margin: 0.6em 0 0.2em 0; font-weight: 600; opacity: 0.85; font-size: 0.9em; }',
            '.dtdd-picker-topic { display: flex; align-items: center; gap: 0.45em; padding: 0.2em 0; font-size: 0.95em; }',
            '.dtdd-picker-footer { display: flex; gap: 0.5em; justify-content: flex-end; margin-top: 0.75em; padding-top: 0.65em; border-top: 1px solid var(--theme-card-border-color, rgba(255,255,255,0.1)); }',
            // Picker buttons: rely on Jellyfin native classes (emby-button +
            // raised + button-submit) where possible — they pick the right
            // text/background colors from the active theme. We only override
            // a couple of layout fields and the cancel-button variant.
            '.dtdd-picker .dtdd-cancel { background: transparent !important; color: inherit !important; border: 1px solid currentColor !important; }',
            // Details dialog: reuses the dtdd-picker dialog/header/footer styling
            // wholesale (same class), narrowed + given per-phobia vote rows.
            'dialog.dtdd-picker.dtdd-details { width: min(440px, 92vw); }',
            '.dtdd-details-list { max-height: 55vh; overflow-y: auto; }',
            '.dtdd-details-row { display: flex; align-items: baseline; justify-content: space-between; gap: 1em; padding: 0.35em 0; font-size: 0.95em; border-bottom: 1px solid var(--theme-card-border-color, rgba(255,255,255,0.08)); }',
            '.dtdd-details-row:last-child { border-bottom: none; }',
            '.dtdd-details-votes { white-space: nowrap; opacity: 0.9; font-variant-numeric: tabular-nums; }',
            '.dtdd-details-votes .dtdd-yes { color: var(--theme-error-color, #e53935); font-weight: 600; }',
            '.dtdd-details-votes .dtdd-no { color: var(--theme-success-color, #43a047); font-weight: 600; }',
            '.dtdd-details-none { padding: 0.5em 0; opacity: 0.8; }'
            // Settings entry styling: inherit from native emby-button / listItem-border / listItem classes. No custom CSS.
        ].join('\n');
        document.head.appendChild(style);
    }

    // -----------------------------------------------------------------------
    // Badge rendering (item detail pages)
    // -----------------------------------------------------------------------

    var ITEM_ID_RE = /[?&]id=([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}|[0-9a-f]{32})/i;
    var lastRenderedItemId = null;
    var renderInFlight = false;

    function extractItemId() {
        var hash = window.location.hash || '';
        var m = hash.match(ITEM_ID_RE);
        return m ? m[1].replace(/-/g, '') : null;
    }

    // Try a list of selectors in order. First match wins. Used both for
    // detail-page metadata containers and Settings-page list containers
    // (different selectors per call site).
    function firstMatch(selectors) {
        for (var i = 0; i < selectors.length; i++) {
            var el = document.querySelector(selectors[i]);
            if (el && el.isConnected && el.offsetParent !== null) return el;
        }
        return null;
    }

    // Match item-detail metadata row. Modern Jellyfin (10.11+) renders this
    // via React with non-stable class names — we try several and fall back
    // to a heuristic search for an element near the title.
    var DETAIL_CONTAINERS = [
        '.itemMiscInfo-primary',          // legacy Jellyfin web
        '.mainDetailButtons',             // legacy alt
        '.detailPagePrimaryContent .itemMiscInfo',
        '.detail-clamp-text + div',       // newer React layout (guess)
        '#itemDetailPage .itemMiscInfo',
        '.page:not(.hide) .itemMiscInfo'  // catch-all
    ];

    function findDetailContainer() {
        return firstMatch(DETAIL_CONTAINERS);
    }

    function badgeAlreadyPresent(container) {
        return container && container.querySelector('.dtdd-badge-wrapper') !== null;
    }

    async function fetchSafety(itemId) {
        try {
            return await api('DTDD/safety/' + itemId);
        } catch (err) {
            warn('safety fetch failed for', itemId, err);
            return null;
        }
    }

    function buildBadge(safety, itemId) {
        var wrapper = document.createElement('span');
        wrapper.className = 'dtdd-badge-wrapper';

        // Defensive read — server emits camelCase via [JsonPropertyName] but
        // accept PascalCase as a fallback so a future serializer-config drift
        // doesn't surface as "DTDD: undefined" again.
        var state = safety.state || safety.State;
        var matchedPhobias = safety.matchedPhobias || safety.MatchedPhobias || [];

        var badge;
        switch (state) {
            case 'safe':
                badge = document.createElement('button');
                badge.type = 'button';
                badge.className = 'dtdd-badge dtdd-safe';
                badge.textContent = 'DTDD: Safe';
                badge.title = 'None of your tracked phobias have YES votes on this title.\n\nClick for details.';
                makeDetailsClickable(badge, safety);
                break;
            case 'not_safe':
                badge = document.createElement('button');
                badge.type = 'button';
                badge.className = 'dtdd-badge dtdd-not-safe';
                var n = matchedPhobias.length;
                badge.textContent = 'DTDD: Not Safe (' + n + ' match' + (n === 1 ? '' : 'es') + ')';
                if (matchedPhobias.length) {
                    // One phobia per line, with vote counts — a comma-joined
                    // single line was unreadable past two or three matches.
                    badge.title = matchedPhobias.map(phobiaLine).join('\n') + '\n\nClick for details.';
                }
                makeDetailsClickable(badge, safety);
                break;
            case 'unknown':
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-unknown';
                badge.textContent = 'DTDD: Not in database';
                break;
            case 'not_configured':
                // CTA: click opens the picker modal in-place.
                badge = document.createElement('button');
                badge.type = 'button';
                badge.className = 'dtdd-badge dtdd-not-configured dtdd-clickable focusable';
                badge.textContent = 'Configure your phobia list';
                badge.title = 'Opens the DoesTheDogDie picker';
                badge.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    openPicker(itemId);
                });
                break;
            default:
                badge = document.createElement('span');
                badge.className = 'dtdd-badge dtdd-unknown';
                badge.textContent = 'DTDD: ' + (state || 'unknown');
        }

        wrapper.appendChild(badge);
        return wrapper;
    }

    function phobiaVotes(p) {
        return {
            yes: Number(p.yesSum != null ? p.yesSum : p.YesSum) || 0,
            no: Number(p.noSum != null ? p.noSum : p.NoSum) || 0
        };
    }

    function phobiaLine(p) {
        var v = phobiaVotes(p);
        return (p.name || p.Name || '?') + ' — ' + v.yes + ' yes / ' + v.no + ' no';
    }

    // Clickable badges are real <button>s (see buildBadge): natively
    // focusable in browsers AND eligible for Jellyfin's TV focus manager,
    // whose arrow-key navigation only visits real focusable tags or elements
    // carrying its `focusable` class (a tabindex'd span is invisible to it —
    // found the hard way on an LG remote). No manual keydown handler: real
    // buttons synthesize click on Enter/Space, and adding our own would
    // double-open the dialog.
    function makeDetailsClickable(badge, safety) {
        badge.classList.add('dtdd-clickable');
        badge.classList.add('focusable');
        badge.setAttribute('aria-haspopup', 'dialog');
        badge.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openDetails(safety);
        });
    }

    // Mount + modality + TV-back integration for our dialogs. A raw <dialog>
    // doesn't participate in Jellyfin's router history, so the remote's Back
    // button would navigate the app away and strand the open dialog on
    // screen (found on the LG remote). On open we push a same-URL history
    // entry: Back pops that entry, we close the dialog, and the router sees
    // no hash change — the user stays on the page. Closing any other way
    // (Close button, Escape, save flow) consumes the pushed entry with
    // history.back() so the NEXT Back press behaves normally. This mirrors
    // what jellyfin-web's own dialogHelper does. viewhide is the belt to
    // that suspender: programmatic navigation while open (voice command,
    // deep link) closes the dialog rather than letting it outlive its page.
    function presentDialog(dialog) {
        document.body.appendChild(dialog);

        var poppedByHistory = false;

        function onPopstate() {
            poppedByHistory = true;
            if (dialog.open) dialog.close();
        }
        function onViewHide() {
            if (dialog.open) dialog.close();
        }

        history.pushState({ dtddDialog: true }, '', location.href);
        window.addEventListener('popstate', onPopstate);
        document.addEventListener('viewhide', onViewHide);

        dialog.addEventListener('close', function () {
            window.removeEventListener('popstate', onPopstate);
            document.removeEventListener('viewhide', onViewHide);
            dialog.remove();
            // Consume our pushed entry — but only if it's still the top of
            // the stack. If the app navigated while we were open (viewhide
            // path), the router owns the current entry and a blind back()
            // would yank the user off the page they just navigated to.
            if (!poppedByHistory && history.state && history.state.dtddDialog) {
                history.back();
            }
        });

        if (typeof dialog.showModal === 'function') {
            dialog.showModal();
        } else {
            dialog.setAttribute('open', '');
        }
    }

    // Small read-only dialog with one row per matched phobia and its vote
    // counts. Reuses the picker's dialog classes so the styling is identical
    // by construction; exists because hover tooltips don't on touch/TV.
    function openDetails(safety) {
        var state = safety.state || safety.State;
        var matched = safety.matchedPhobias || safety.MatchedPhobias || [];
        var configured = Number(safety.configuredPhobiaCount != null ? safety.configuredPhobiaCount : safety.ConfiguredPhobiaCount) || 0;

        var dialog = document.createElement('dialog');
        dialog.className = 'dtdd-picker dtdd-details';

        var header = document.createElement('div');
        header.className = 'dtdd-picker-header';
        var title = document.createElement('h2');
        title.textContent = state === 'not_safe' ? 'Not Safe — matched phobias' : 'Safe';
        header.appendChild(title);
        var count = document.createElement('span');
        count.className = 'dtdd-picker-count';
        count.textContent = state === 'not_safe'
            ? matched.length + ' of ' + configured + ' tracked'
            : configured + ' tracked';
        header.appendChild(count);
        dialog.appendChild(header);

        var list = document.createElement('div');
        list.className = 'dtdd-details-list';
        if (state === 'not_safe' && matched.length) {
            for (var i = 0; i < matched.length; i++) {
                var p = matched[i];
                var row = document.createElement('div');
                row.className = 'dtdd-details-row';
                var name = document.createElement('span');
                name.textContent = p.name || p.Name || '?';
                row.appendChild(name);
                var votes = document.createElement('span');
                votes.className = 'dtdd-details-votes';
                var v = phobiaVotes(p);
                var yesEl = document.createElement('span');
                yesEl.className = 'dtdd-yes';
                yesEl.textContent = v.yes + ' yes';
                var noEl = document.createElement('span');
                noEl.className = 'dtdd-no';
                noEl.textContent = v.no + ' no';
                votes.appendChild(yesEl);
                votes.appendChild(document.createTextNode(' / '));
                votes.appendChild(noEl);
                row.appendChild(votes);
                list.appendChild(row);
            }
        } else {
            var none = document.createElement('div');
            none.className = 'dtdd-details-none';
            none.textContent = 'None of your ' + configured + ' tracked phobia topic' + (configured === 1 ? '' : 's') + ' have YES votes on this title.';
            list.appendChild(none);
        }
        dialog.appendChild(list);

        var footer = document.createElement('div');
        footer.className = 'dtdd-picker-footer';
        var closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'emby-button dtdd-cancel';
        closeBtn.style.padding = '0.45em 1.1em';
        closeBtn.style.borderRadius = '0.3em';
        closeBtn.textContent = 'Close';
        closeBtn.addEventListener('click', function () { dialog.close(); });
        footer.appendChild(closeBtn);
        dialog.appendChild(footer);

        presentDialog(dialog);
    }

    async function renderBadgeFor(itemId, container) {
        if (!container) return;
        if (renderInFlight) return; // MutationObserver may fire many times during React re-renders
        renderInFlight = true;
        try {
            // Clear any previous badge (avoid duplicates if we re-render).
            var existing = container.querySelectorAll('.dtdd-badge-wrapper');
            for (var i = 0; i < existing.length; i++) existing[i].remove();

            var safety = await fetchSafety(itemId);
            if (!safety) return;
            var wrapper = buildBadge(safety, itemId);
            container.appendChild(wrapper);
            lastRenderedItemId = itemId;
            if (DEBUG) log('badge rendered: state=' + safety.state + ' item=' + itemId + ' container=' + (container.className || container.tagName));
        } finally {
            renderInFlight = false;
        }
    }

    // Continuous detail-page watcher: when on a detail page, ensure the
    // badge appears AND stays present even if React re-renders the row.
    // Uses MutationObserver over document.body for resilience.
    var detailObserver = null;

    function startDetailWatcher() {
        if (detailObserver) return;
        detailObserver = new MutationObserver(function () {
            var itemId = extractItemId();
            if (!itemId) return;
            var container = findDetailContainer();
            if (!container) return;
            if (badgeAlreadyPresent(container) && itemId === lastRenderedItemId) return;
            // Either no badge yet, or item changed — (re-)render.
            renderBadgeFor(itemId, container);
        });
        detailObserver.observe(document.body, { childList: true, subtree: true });
        if (DEBUG) log('detail watcher armed');
    }

    function onViewShow() {
        var itemId = extractItemId();
        if (DEBUG) log('viewshow hash=' + (window.location.hash || '').slice(0, 80) + ' itemId=' + itemId);
        if (itemId) {
            // Reset so the badge re-renders on item change.
            if (itemId !== lastRenderedItemId) lastRenderedItemId = null;
            // Kick a synchronous attempt; the MutationObserver will retry as DOM populates.
            var container = findDetailContainer();
            if (container && !badgeAlreadyPresent(container)) {
                renderBadgeFor(itemId, container);
            }
        }
        // Settings page injection always re-checks.
        injectSettingsEntry();
    }

    // -----------------------------------------------------------------------
    // Settings menu injection
    // -----------------------------------------------------------------------

    // Detect Jellyfin's user-settings page only — confirmed via DOM probe:
    // the URL hash is "#/mypreferencesmenu" and the page id is
    // "myPreferencesMenuPage" (a child of .verticalSection.
    // verticalSection-extrabottompadding holds the Profile / Quick Connect /
    // ... list).
    function isOnSettingsMenuPage() {
        var hash = (window.location.hash || '').toLowerCase();
        return hash.indexOf('mypreferencesmenu') >= 0;
    }

    function injectSettingsEntry() {
        if (!isOnSettingsMenuPage()) return;
        if (document.querySelector('.dtdd-settings-entry')) return; // already injected

        // The first .verticalSection.verticalSection-extrabottompadding on
        // #myPreferencesMenuPage is the user prefs section (above the User /
        // Sign Out section). Append our entry there so it sits with Profile /
        // Quick Connect / Display / etc.
        var section = document.querySelector('#myPreferencesMenuPage:not(.hide) .verticalSection.verticalSection-extrabottompadding');
        if (!section) {
            if (DEBUG) log('settings page detected but section not present yet (will retry on next mutation)');
            return;
        }

        // Match Jellyfin's exact pattern observed via DOM probe:
        //   <a class="emby-button lnkXxxPreferences listItem-border"
        //      href="..." style="display: block; margin: 0px; padding: 0px;">
        //     <div class="listItem">
        //       <span class="material-icons listItemIcon listItemIcon-transparent <iconName>" aria-hidden="true"></span>
        //       <div class="listItemBody">
        //         <div class="listItemBodyText"><label></div>
        //       </div>
        //     </div>
        //   </a>
        //
        // The icon-name class (e.g., "person", "tv", "home") is what Jellyfin
        // uses to pick the glyph — we ALSO include the icon name as inner
        // text so Material Icons ligature handles it even if Jellyfin's CSS
        // doesn't recognise our class name.
        var entry = document.createElement('a');
        entry.className = 'emby-button listItem-border dtdd-settings-entry';
        entry.href = '#';
        entry.setAttribute('style', 'display: block; margin: 0px; padding: 0px;');
        // The icon-name class drives the glyph via Jellyfin CSS
        // (.material-icons.warning::before { content: "warning" }). We do NOT
        // also put "warning" as inner text — that would render a second icon
        // via the Material Icons ligature.
        entry.innerHTML = [
            '<div class="listItem">',
            '  <span class="material-icons listItemIcon listItemIcon-transparent warning" aria-hidden="true"></span>',
            '  <div class="listItemBody">',
            '    <div class="listItemBodyText">DoesTheDogDie</div>',
            '  </div>',
            '</div>'
        ].join('');
        entry.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openPicker(null);
        });

        section.appendChild(entry);
        if (DEBUG) log('settings entry injected');
    }

    // -----------------------------------------------------------------------
    // Picker modal
    // -----------------------------------------------------------------------

    var topicsCache = null;

    async function fetchTopics(force) {
        if (topicsCache && topicsCache.length > 0 && !force) return topicsCache;
        try {
            var result = await api('DTDD/topics');
            topicsCache = Array.isArray(result) ? result : [];
        } catch (err) {
            warn('topics fetch failed', err);
            topicsCache = [];
        }
        return topicsCache;
    }

    async function fetchPrefs() {
        try {
            var p = await api('DTDD/prefs');
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

    async function openPicker(currentItemId) {
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

        var status = document.createElement('div');
        status.className = 'dtdd-picker-status';
        dialog.appendChild(status);

        function setStatus(msg) { status.textContent = msg || ''; }

        function updateCount() {
            var n = 0;
            for (var k in selected) if (selected[k]) n++;
            countEl.textContent = n + ' selected';
        }

        function renderList(filter) {
            list.innerHTML = '';
            if (!topics.length) {
                var empty = document.createElement('div');
                empty.className = 'dtdd-picker-empty';
                empty.textContent = 'No topics in the catalog yet. The plugin will populate them from DTDD shortly.';
                list.appendChild(empty);
                return;
            }
            var f = (filter || '').toLowerCase().trim();
            var byCat = groupByCategory(topics);
            var cats = Object.keys(byCat).sort();
            var anyRendered = false;
            for (var i = 0; i < cats.length; i++) {
                var cat = cats[i];
                var inCat = byCat[cat];
                var filtered = f
                    ? inCat.filter(function (t) { return (t.name + ' ' + (t.description || '')).toLowerCase().indexOf(f) >= 0; })
                    : inCat;
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

        // Cancel: outlined / transparent. .dtdd-cancel keeps it visually
        // distinct from the raised primary Save button.
        var cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.className = 'emby-button dtdd-cancel';
        cancelBtn.style.padding = '0.45em 1.1em';
        cancelBtn.style.borderRadius = '0.3em';
        cancelBtn.textContent = 'Cancel';
        cancelBtn.addEventListener('click', function () { dialog.close(); });

        // Save: use Jellyfin's native primary-button class set so the active
        // theme picks the correct text-on-accent colour pair. emby-button
        // + raised + button-submit is the same trio Jellyfin's own config
        // pages use for the green/blue Save buttons.
        var saveBtn = document.createElement('button');
        saveBtn.type = 'button';
        saveBtn.className = 'raised button-submit emby-button';
        saveBtn.style.padding = '0.45em 1.1em';
        saveBtn.style.borderRadius = '0.3em';
        saveBtn.innerHTML = '<span>Save phobias</span>';
        saveBtn.addEventListener('click', async function () {
            var ids = [];
            for (var k in selected) if (selected[k]) ids.push(Number(k));
            if (ids.length === 0) {
                if (!window.confirm('Saving with no topics selected disables filtering — every item shows "Safe". Save anyway?')) {
                    return;
                }
            }
            saveBtn.disabled = true;
            setStatus('Saving prefs…');
            try {
                await api('DTDD/prefs', { method: 'PUT', body: { phobiaTopicIds: ids } });
            } catch (err) {
                setStatus('Save failed: ' + (err && err.message ? err.message : err));
                saveBtn.disabled = false;
                return;
            }
            setStatus('Saved. Library will be scanned by the weekly task (or by the admin manually triggering "Prefetch DoesTheDogDie warnings" in Dashboard → Scheduled Tasks).');
            // Re-render badge for the item the user was on, so they see the
            // updated verdict for that title immediately.
            lastRenderedItemId = null;
            var container = findDetailContainer();
            if (container && currentItemId) {
                await renderBadgeFor(currentItemId, container);
            }
            setTimeout(function () { dialog.close(); }, 1500);
        });

        footer.appendChild(cancelBtn);
        footer.appendChild(saveBtn);
        dialog.appendChild(footer);

        presentDialog(dialog);
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------

    function init() {
        ensureStyles();
        document.addEventListener('viewshow', onViewShow);
        // Initial pass — viewshow may have already fired before we attached.
        onViewShow();
        startDetailWatcher();
        log('injector active (badge + Settings entry + picker modal)');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
