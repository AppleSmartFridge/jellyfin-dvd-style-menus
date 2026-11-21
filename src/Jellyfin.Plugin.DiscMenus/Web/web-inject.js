/*
 * Disc Menus integration for jellyfin-web (browsers, Jellyfin Media Player / Desktop, and the
 * Android / iOS apps that wrap the web client).
 *
 * While a "Disc Menu (Interactive)" version is playing on this device it:
 *  - adds a "Disc Remote" button to the player's OSD button row, next to audio/subtitles/settings,
 *    which opens an on-screen web remote (the phone remote is offered inside it, not forced);
 *  - optionally (setting "web key capture") sends arrow keys, Enter, digits, F1-F4, PageUp/PageDown,
 *    Home and End straight to the disc menu.
 * Nothing happens during normal playback.
 */
(function () {
    'use strict';
    if (window.__discMenusInjected) {
        return;
    }
    window.__discMenusInjected = true;

    var config = null;
    var session = null;       // current menu session for this device, or null
    var panel = null;         // { el, handle }
    var lastCheck = 0;
    var checking = false;

    function api() { return window.ApiClient; }

    function whenReady(cb) {
        if (api() && api().getUrl && api().accessToken && api().accessToken()) { cb(); return; }
        setTimeout(function () { whenReady(cb); }, 1000);
    }

    function loadAsset(path, isCss) {
        return new Promise(function (resolve, reject) {
            var url = api().getUrl(path);
            var existing = document.querySelector((isCss ? 'link[data-dm="' : 'script[data-dm="') + path + '"]');
            if (existing) { resolve(); return; }
            var tag = document.createElement(isCss ? 'link' : 'script');
            tag.setAttribute('data-dm', path);
            if (isCss) { tag.rel = 'stylesheet'; tag.href = url; } else { tag.src = url; }
            tag.onload = resolve;
            tag.onerror = reject;
            document.head.appendChild(tag);
        });
    }

    function loadRemote() {
        return Promise.all([loadAsset('DiscMenus/Remote/remote.css', true), loadAsset('DiscMenus/Remote/remote.js', false)]);
    }

    function inVideoPlayer() {
        return !!document.querySelector('.videoOsdBottom, .videoPlayerContainer');
    }

    // ------------------------------------------------------------ session detection

    function checkSession(force) {
        var now = Date.now();
        if (checking || (!force && now - lastCheck < 4000)) { return; }
        if (!inVideoPlayer()) {
            if (session) { setSession(null); }
            return;
        }
        checking = true;
        lastCheck = now;
        var deviceId = api().deviceId ? api().deviceId() : '';
        var transport = window.DiscRemote ? window.DiscRemote.jellyfinTransport(api()) : null;
        var req = transport
            ? transport.request('GET', 'DiscMenus/Sessions/ForDevice?deviceId=' + encodeURIComponent(deviceId))
            : loadRemote().then(function () {
                return window.DiscRemote.jellyfinTransport(api()).request('GET', 'DiscMenus/Sessions/ForDevice?deviceId=' + encodeURIComponent(deviceId));
            });
        req.then(function (s) { setSession(s && s.Id ? s : null); })
            .catch(function () { /* keep the last answer */ })
            .then(function () { checking = false; });
    }

    function setSession(s) {
        var changed = (session && session.Id) !== (s && s.Id);
        session = s;
        if (changed) {
            if (!s) { closePanel(); }
            syncButton();
        }
    }

    // ------------------------------------------------------------ OSD button

    function findButtonRow() {
        var anchor = document.querySelector('.videoOsdBottom .btnVideoOsdSettings, .videoOsdBottom .btnSubtitles, .videoOsdBottom .btnAudio, .videoOsdBottom .btnFullscreen');
        return anchor ? { row: anchor.parentNode, before: anchor } : null;
    }

    function syncButton() {
        var existing = document.querySelector('.btnDiscRemote');
        if (!session) {
            if (existing) { existing.remove(); }
            return;
        }
        if (existing) { return; }
        var spot = findButtonRow();
        if (!spot) { return; }
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'btnDiscRemote autoSize paper-icon-button-light';
        btn.title = 'Disc Remote';
        btn.setAttribute('aria-label', 'Disc Remote');
        var icon = document.createElement('span');
        icon.className = 'xlargePaperIconButton material-icons settings_remote';
        icon.setAttribute('aria-hidden', 'true');
        btn.appendChild(icon);
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            togglePanel();
        });
        spot.row.insertBefore(btn, spot.before);
    }

    // ------------------------------------------------------------ web remote panel

    function togglePanel() {
        if (panel) { closePanel(); return; }
        if (!session) { return; }
        loadRemote().then(function () {
            var host = document.createElement('div');
            host.className = 'dm-panel';
            host.setAttribute('role', 'dialog');
            host.setAttribute('aria-label', 'Disc Remote');
            // Keep clicks inside the panel from toggling the player OSD or pausing.
            ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'touchstart', 'touchend', 'dblclick'].forEach(function (t) {
                host.addEventListener(t, function (e) { e.stopPropagation(); });
            });
            document.body.appendChild(host);
            var handle = window.DiscRemote.mount(host, window.DiscRemote.jellyfinTransport(api()), {
                sessionId: session.Id,
                compact: true,
                captureKeys: true,
                onClose: closePanel
            });
            panel = { el: host, handle: handle };
        });
    }

    function closePanel() {
        if (!panel) { return; }
        panel.handle.destroy();
        if (panel.el.parentNode) { panel.el.parentNode.removeChild(panel.el); }
        panel = null;
    }

    // ------------------------------------------------------------ optional key capture

    function onKeyDown(ev) {
        if (!config || !config.KeyCapture || !session || panel || ev.defaultPrevented || ev.altKey || ev.ctrlKey || ev.metaKey) { return; }
        var t = ev.target;
        if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) { return; }
        var map = (window.DiscRemote && window.DiscRemote.keymap) || {};
        var key = map[ev.key] || (/^[0-9]$/.test(ev.key) ? ev.key : null);
        if (!key) { return; }
        ev.preventDefault();
        ev.stopImmediatePropagation();
        window.DiscRemote.jellyfinTransport(api()).request('POST', 'DiscMenus/Sessions/' + session.Id + '/Key/' + key).catch(function () { lastCheck = 0; });
    }

    // ------------------------------------------------------------ start

    whenReady(function () {
        api().fetch({ type: 'GET', url: api().getUrl('DiscMenus/ClientConfig'), headers: {} }, false)
            .then(function (r) { return r && r.json ? r.json() : r; })
            .then(function (c) {
                config = c || {};
                if (!config.WebIntegration) { return; }
                if (config.KeyCapture) { loadRemote(); }
                window.addEventListener('keydown', onKeyDown, true);
                var observer = new MutationObserver(function () {
                    checkSession(false);
                    if (session) { syncButton(); }
                });
                observer.observe(document.body, { childList: true, subtree: true });
                setInterval(function () { checkSession(false); }, 5000);
                document.addEventListener('viewshow', function () { lastCheck = 0; checkSession(true); });
            })
            .catch(function () { /* plugin unreachable: do nothing */ });
    });
})();
