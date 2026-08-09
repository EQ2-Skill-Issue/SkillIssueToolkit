// Shared across all overlay pages.

// Connects to the plugin's WebSocket and routes each message to the right handler by type.
// handlers is e.g. { encounterSnapshot: render } or { triggerFired: addToast } - a page only
// supplies handlers for the message types it cares about; anything else that arrives (other
// overlays share this same connection) is silently ignored.
function connectOverlay(handlers) {
    function connect() {
        const ws = new WebSocket(`ws://${location.host}/ws`);
        ws.onmessage = (event) => {
            const msg = JSON.parse(event.data);
            const handler = handlers[msg.type];
            if (handler) handler(msg.data);
        };
        ws.onclose = () => setTimeout(connect, 1000); // auto-reconnect if ACT restarts
    }
    connect();
}

// Reports #panel's rendered width/height to the WPF host so the window (and WebView2's own
// bounds) match real content instead of a fixed worst-case size - a fixed size means
// WebView2 claims mouse input across its whole rectangle even where nothing renders,
// blocking clicks on the game underneath. Call once on load; watchContentSize keeps it
// accurate after that.
function reportContentSize() {
    if (window.chrome && window.chrome.webview) {
        const rect = document.getElementById('panel').getBoundingClientRect();
        window.chrome.webview.postMessage(JSON.stringify({ type: 'contentSize', width: rect.width, height: rect.height }));
    }
}

// Wires a ResizeObserver on #panel so size is reported whenever it changes - layout, fonts
// loading, row/toast count, zoom level. The first call after page load can race the WPF
// host still finishing setup and lose the message; ResizeObserver keeps firing on any size
// change so a later call lands even if an earlier one didn't.
function watchContentSize() {
    new ResizeObserver(() => reportContentSize()).observe(document.getElementById('panel'));
}

// Called by the WPF host via ExecuteScriptAsync to change zoom level. CSS zoom (not
// transform: scale) reflows layout as if every dimension were multiplied, so the
// ResizeObserver above picks up the change and reports the new size through the normal
// path. Zoom is non-standard CSS, but safe here since this only ever runs inside WebView2
// (Chromium).
window.setOverlayZoom = function (factor) {
    document.body.style.zoom = factor;
};

// Drives #customTooltip (from common.css) for any element with a data-tooltip attribute.
// Position is viewport-aware: content is confined to WebView2's own bounds and these
// windows are tightly fit to content, so naive below-right placement gets clipped often.
// Flips to whichever side has room instead. Call once per page.
function initTooltips() {
    const tooltipEl = document.getElementById('customTooltip');
    if (!tooltipEl) return;

    function positionTooltip(clientX, clientY) {
        const vw = window.innerWidth;
        const vh = window.innerHeight;
        const tw = tooltipEl.offsetWidth;
        const th = tooltipEl.offsetHeight;
        const gap = 10;

        let left = clientX + gap;
        let top = clientY + gap;

        if (left + tw > vw) left = clientX - tw - gap;   // flip to the left of the cursor
        if (top + th > vh) top = clientY - th - gap;      // flip above the cursor

        left = Math.max(2, Math.min(left, vw - tw - 2));
        top = Math.max(2, Math.min(top, vh - th - 2));

        tooltipEl.style.left = left + 'px';
        tooltipEl.style.top = top + 'px';
    }

    document.addEventListener('mouseover', (e) => {
        const target = e.target.closest('[data-tooltip]');
        if (target) {
            tooltipEl.textContent = target.dataset.tooltip;
            tooltipEl.style.display = 'block';
            positionTooltip(e.clientX, e.clientY);
        }
    });

    document.addEventListener('mousemove', (e) => {
        if (tooltipEl.style.display === 'block') positionTooltip(e.clientX, e.clientY);
    });

    document.addEventListener('mouseout', (e) => {
        const target = e.target.closest('[data-tooltip]');
        const goingTo = e.relatedTarget ? e.relatedTarget.closest('[data-tooltip]') : null;
        if (target && target !== goingTo) tooltipEl.style.display = 'none';
    });
}