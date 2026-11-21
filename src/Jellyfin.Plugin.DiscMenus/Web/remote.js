/*
 * Disc Menus remote UI. Shared by:
 *  - the phone remote page (/DiscMenus/Remote, paired with a QR code or link)
 *  - the "Disc Remote" page in the Jellyfin menu (web, desktop and web-wrapper apps)
 *  - the floating web remote opened from the video OSD button
 *
 * DiscRemote.mount(container, transport, options)
 *   transport.request(method, path, body) -> Promise<any>   (path like "DiscMenus/Sessions")
 *   transport.eventsUrl(path) -> string                      (absolute URL for EventSource)
 *   transport.createPairing() -> Promise<{RemoteUrl, QrUrl, Expires}> | undefined
 *   options.sessionId    fixed session (OSD panel); otherwise the user picks one
 *   options.captureKeys  arrow keys / Enter on the page go to the disc
 *   options.compact      smaller layout for the floating panel
 *   options.onClose      show a close button that calls this
 */
(function () {
    'use strict';
    if (window.DiscRemote) {
        return;
    }

    function el(tag, attrs, children) {
        var e = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                if (k === 'class') { e.className = attrs[k]; }
                else if (k === 'text') { e.textContent = attrs[k]; }
                else if (k.indexOf('on') === 0) { e.addEventListener(k.substring(2), attrs[k]); }
                else { e.setAttribute(k, attrs[k]); }
            });
        }
        (children || []).forEach(function (c) { if (c) { e.appendChild(c); } });
        return e;
    }

    function buzz() {
        try { if (navigator.vibrate) { navigator.vibrate(8); } } catch (e) { /* ignore */ }
    }

    var KEYMAP = {
        ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
        Enter: 'enter', NumpadEnter: 'enter',
        F1: 'red', F2: 'green', F3: 'yellow', F4: 'blue',
        PageUp: 'prev', PageDown: 'next',
        Home: 'menu', End: 'popup'
    };

    function mount(container, transport, options) {
        options = options || {};
        var state = { sessionId: options.sessionId || null, sessions: [], es: null, disc: {}, pollTimer: null, touch: false, destroyed: false };

        var root = el('div', { class: 'dm-remote' + (options.compact ? ' dm-compact' : '') });
        container.appendChild(root);

        // Header: session picker + status
        var picker = el('select', { class: 'dm-picker', 'aria-label': 'Disc session' });
        picker.addEventListener('change', function () { selectSession(picker.value); });
        var status = el('div', { class: 'dm-status', role: 'status', 'aria-live': 'polite', text: 'Looking for disc menu sessions…' });
        var headerChildren = [el('div', { class: 'dm-title', text: 'Disc Remote' })];
        if (options.onClose) {
            headerChildren.push(el('button', { class: 'dm-btn dm-close', 'aria-label': 'Close remote', text: '✕', onclick: options.onClose }));
        }
        root.appendChild(el('div', { class: 'dm-header' }, headerChildren));
        if (!options.sessionId) { root.appendChild(picker); }
        root.appendChild(status);

        function keyButton(label, key, cls, aria) {
            return el('button', {
                class: 'dm-btn ' + (cls || ''),
                'aria-label': aria || label,
                'data-key': key,
                text: label,
                onclick: function () { sendKey(key); }
            });
        }

        // D-pad
        var dpad = el('div', { class: 'dm-dpad' }, [
            el('span'), keyButton('▲', 'up', 'dm-arrow', 'Up'), el('span'),
            keyButton('◀', 'left', 'dm-arrow', 'Left'), keyButton('OK', 'enter', 'dm-ok', 'Select'), keyButton('▶', 'right', 'dm-arrow', 'Right'),
            el('span'), keyButton('▼', 'down', 'dm-arrow', 'Down'), el('span')
        ]);
        root.appendChild(dpad);

        root.appendChild(el('div', { class: 'dm-row' }, [
            keyButton('Top Menu', 'menu', 'dm-wide'),
            keyButton('Pop-up', 'popup', 'dm-wide'),
            keyButton('Back', 'back', 'dm-wide')
        ]));

        root.appendChild(el('div', { class: 'dm-row dm-colors' }, [
            keyButton('', 'red', 'dm-red', 'Red'),
            keyButton('', 'green', 'dm-green', 'Green'),
            keyButton('', 'yellow', 'dm-yellow', 'Yellow'),
            keyButton('', 'blue', 'dm-blue', 'Blue')
        ]));

        root.appendChild(el('div', { class: 'dm-row' }, [
            keyButton('⏮', 'prev', '', 'Previous chapter'),
            keyButton('⏯', 'pause', '', 'Play or pause'),
            keyButton('⏹', 'stop', '', 'Stop (back to menu)'),
            keyButton('⏭', 'next', '', 'Next chapter')
        ]));

        // Number pad (collapsed by default)
        var digits = el('div', { class: 'dm-digits' });
        ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'].forEach(function (d) { digits.appendChild(keyButton(d, d, 'dm-digit')); });
        var more = el('details', { class: 'dm-more' }, [el('summary', { text: 'Number pad' }), digits]);
        root.appendChild(more);

        // Audio / subtitle pickers
        var audioSel = el('select', { class: 'dm-select', 'aria-label': 'Audio track' });
        var subSel = el('select', { class: 'dm-select', 'aria-label': 'Subtitles' });
        audioSel.addEventListener('change', function () { command('audio', parseInt(audioSel.value, 10)); });
        subSel.addEventListener('change', function () { command('subtitle', parseInt(subSel.value, 10)); });
        root.appendChild(el('div', { class: 'dm-row dm-tracks' }, [
            el('label', {}, [el('span', { text: 'Audio' }), audioSel]),
            el('label', {}, [el('span', { text: 'Subtitles' }), subSel])
        ]));

        // Touchpad for BD-J / pop-up pointer menus
        var pad = el('div', { class: 'dm-touchpad', role: 'button', 'aria-label': 'Touchpad: tap where you would click on the TV picture' }, [
            el('span', { text: 'Tap here where you would click on the picture (for menus that use a pointer)' })
        ]);
        pad.addEventListener('click', function (ev) {
            var r = pad.getBoundingClientRect();
            pointer((ev.clientX - r.left) / r.width, (ev.clientY - r.top) / r.height, true);
        });
        var padWrap = el('details', { class: 'dm-more' }, [el('summary', { text: 'Touchpad' }), pad]);
        root.appendChild(padWrap);

        // Phone remote (QR + link)
        if (transport.createPairing) {
            var qrBox = el('div', { class: 'dm-qr' });
            var phone = el('details', { class: 'dm-more' }, [el('summary', { text: 'Use your phone instead' }), qrBox]);
            phone.addEventListener('toggle', function () {
                if (phone.open && !qrBox.hasChildNodes()) { renderPairing(qrBox); }
            });
            root.appendChild(phone);
        }

        function renderPairing(box) {
            box.textContent = 'Making a pairing link…';
            transport.createPairing().then(function (p) {
                box.textContent = '';
                box.appendChild(el('img', { src: p.QrUrl, alt: 'QR code for the phone remote', width: '200', height: '200' }));
                box.appendChild(el('p', { class: 'dm-hint', text: 'Scan with your phone, or open this link on it:' }));
                box.appendChild(el('a', { href: p.RemoteUrl, target: '_blank', rel: 'noopener', class: 'dm-link', text: p.RemoteUrl }));
                box.appendChild(el('p', { class: 'dm-hint', text: 'The link works once and expires in 10 minutes.' }));
                box.appendChild(el('button', { class: 'dm-btn dm-wide', text: 'New link', onclick: function () { renderPairing(box); } }));
            }).catch(function () {
                box.textContent = 'Could not create a pairing link.';
            });
        }

        function setStatus(text) { status.textContent = text; }

        function sendKey(key) {
            if (!state.sessionId) { setStatus('No disc menu is playing.'); return; }
            buzz();
            transport.request('POST', 'DiscMenus/Sessions/' + state.sessionId + '/Key/' + key).catch(onError);
        }

        function command(cmd, value) {
            if (!state.sessionId || isNaN(value)) { return; }
            transport.request('POST', 'DiscMenus/Sessions/' + state.sessionId + '/Command', { command: cmd, value: value }).catch(onError);
        }

        function pointer(x, y, click) {
            if (!state.sessionId) { return; }
            buzz();
            transport.request('POST', 'DiscMenus/Sessions/' + state.sessionId + '/Mouse', { x: x, y: y, click: click }).catch(onError);
        }

        function onError(err) {
            if (err && err.status === 401) { setStatus('Not signed in. Pair again from the Disc Menus page.'); }
            else if (err && err.status === 404) { setStatus('That disc session has ended.'); state.sessionId = options.sessionId ? state.sessionId : null; refresh(); }
            else { setStatus('Could not reach the server.'); }
        }

        function describe(d) {
            if (!d) { return ''; }
            var parts = [];
            if (d.error) { parts.push('Error: ' + d.error); }
            if (d.popup) { parts.push('Pop-up menu'); } else if (d.menu) { parts.push('Menu'); }
            if (typeof d.title === 'number') { parts.push(d.title === 0 ? 'Top menu title' : (d.title === 65535 ? 'First play' : 'Title ' + d.title)); }
            if (typeof d.chapter === 'number' && d.chapter > 0) { parts.push('Chapter ' + d.chapter); }
            if (d.still) { parts.push('Still'); }
            if (d.paused) { parts.push('Paused'); }
            if (d.bdj) { parts.push('BD-J'); }
            return parts.join(' · ');
        }

        function updateTracks(d) {
            fillSelect(audioSel, (d.audio_streams || []).map(function (s, i) {
                return { value: i + 1, label: (i + 1) + '. ' + (s.lang || 'und') + (s.codec ? ' (' + s.codec + ')' : '') };
            }), d.audio, false);
            fillSelect(subSel, (d.pg_streams || []).map(function (s, i) {
                return { value: i + 1, label: (i + 1) + '. ' + (s.lang || 'und') };
            }), d.pg_enabled ? d.pg : 0, true);
        }

        function fillSelect(sel, items, current, withOff) {
            var key = JSON.stringify(items) + '|' + withOff;
            if (sel.getAttribute('data-k') !== key) {
                sel.textContent = '';
                if (withOff) { sel.appendChild(el('option', { value: '0', text: 'Off' })); }
                items.forEach(function (it) { sel.appendChild(el('option', { value: String(it.value), text: it.label })); });
                if (!items.length && !withOff) { sel.appendChild(el('option', { value: '', text: '—' })); }
                sel.setAttribute('data-k', key);
            }
            if (current !== undefined && current !== null) { sel.value = String(current); }
        }

        function selectSession(id) {
            if (state.es) { state.es.close(); state.es = null; }
            state.sessionId = id || null;
            root.classList.toggle('dm-disabled', !state.sessionId);
            if (!state.sessionId) { setStatus('No disc menu is playing. Start “Disc Menu (Interactive)” from a Blu-ray\'s version list.'); return; }
            var s = state.sessions.filter(function (x) { return x.Id === id; })[0];
            setStatus((s ? s.ItemName + ' — ' : '') + 'connected');
            if (!transport.eventsUrl) { return; } // state arrives through polling instead
            try {
                state.es = new EventSource(transport.eventsUrl('DiscMenus/Sessions/' + id + '/Events'));
                state.es.onmessage = function (m) {
                    try { applyState(s ? s.ItemName : 'Disc', JSON.parse(m.data)); } catch (e) { /* ignore */ }
                };
                state.es.onerror = function () { /* EventSource retries by itself */ };
            } catch (e) { /* SSE not available: buttons still work */ }
        }

        function applyState(name, disc) {
            state.disc = disc || {};
            var label = describe(state.disc);
            setStatus(name + (label ? ' — ' + label : ''));
            updateTracks(state.disc);
        }

        // Without server-sent events, read the state from the session list.
        function pollState() {
            if (state.destroyed || transport.eventsUrl || !state.sessionId) { return; }
            transport.request('GET', 'DiscMenus/Sessions').then(function (list) {
                var s = (list || []).filter(function (x) { return x.Id === state.sessionId; })[0];
                if (s) { applyState(s.ItemName, s.State); }
                else if (options.sessionId) { setStatus('That disc session has ended.'); }
            }).catch(function () { /* keep trying */ });
        }

        function refresh() {
            if (state.destroyed || options.sessionId) { return; }
            transport.request('GET', 'DiscMenus/Sessions').then(function (list) {
                state.sessions = list || [];
                var current = picker.value;
                picker.textContent = '';
                if (!state.sessions.length) {
                    picker.appendChild(el('option', { value: '', text: 'No disc menu playing' }));
                } else {
                    state.sessions.forEach(function (s) {
                        picker.appendChild(el('option', { value: s.Id, text: s.ItemName + ' (' + s.MenuKind + ')' }));
                    });
                }
                var still = state.sessions.some(function (s) { return s.Id === state.sessionId; });
                var pick = still ? state.sessionId : (state.sessions[0] ? state.sessions[0].Id : '');
                picker.value = pick;
                if (pick !== state.sessionId || (!pick && current !== '')) { selectSession(pick); }
                else if (!pick) { selectSession(''); }
            }).catch(onError);
        }

        function onKey(ev) {
            if (!state.sessionId || ev.defaultPrevented) { return; }
            var t = ev.target;
            if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) { return; }
            var key = KEYMAP[ev.key] || (/^[0-9]$/.test(ev.key) ? ev.key : null);
            if (!key) { return; }
            ev.preventDefault();
            ev.stopPropagation();
            sendKey(key);
        }

        if (options.captureKeys) { document.addEventListener('keydown', onKey, true); }

        if (options.sessionId) { selectSession(options.sessionId); }
        else {
            refresh();
            state.pollTimer = setInterval(refresh, 5000);
        }

        state.stateTimer = transport.eventsUrl ? null : setInterval(pollState, 2000);
        pollState();

        return {
            destroy: function () {
                state.destroyed = true;
                if (state.es) { state.es.close(); }
                if (state.pollTimer) { clearInterval(state.pollTimer); }
                if (state.stateTimer) { clearInterval(state.stateTimer); }
                if (options.captureKeys) { document.removeEventListener('keydown', onKey, true); }
                if (root.parentNode) { root.parentNode.removeChild(root); }
            },
            sendKey: sendKey,
            pointer: pointer
        };
    }

    /**
     * Transport for pages running inside jellyfin-web (signed in through ApiClient). It uses the
     * client's own request method so authentication works on every server version, and polls state
     * instead of using server-sent events (EventSource cannot send the auth header).
     */
    function jellyfinTransport(apiClient) {
        function request(method, path, body) {
            var req = { type: method, url: apiClient.getUrl(path), headers: {} };
            if (body) {
                req.data = JSON.stringify(body);
                req.contentType = 'application/json';
            }
            return apiClient.fetch(req, true).then(function (r) {
                if (!r || typeof r.status !== 'number') { return r; }
                if (!r.ok) { var e = new Error('HTTP ' + r.status); e.status = r.status; throw e; }
                var ct = (r.headers && r.headers.get('content-type')) || '';
                return ct.indexOf('json') >= 0 ? r.json() : null;
            }, function (r) {
                var e = new Error('request failed');
                e.status = r && r.status;
                throw e;
            });
        }
        return {
            request: request,
            createPairing: function () { return request('POST', 'DiscMenus/Pairing'); }
        };
    }

    window.DiscRemote = { mount: mount, keymap: KEYMAP, jellyfinTransport: jellyfinTransport };
})();
