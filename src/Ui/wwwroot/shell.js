// Sets the page language (the launcher's host page is static).
export function setLang(code) {
    document.documentElement.lang = code;
}

// Tells the shell when the page is scrolled past a threshold (quick-nav); throttled like the old interface (25 ms).
let handler;

export function watchScroll(ref, threshold) {
    let past = false, wait = false;
    handler = () => {
        if (wait) return;
        wait = true;
        setTimeout(() => {
            wait = false;
            const now = window.scrollY > threshold;
            if (now !== past) { past = now; ref.invokeMethodAsync('Scrolled', now); }
        }, 25);
    };
    window.addEventListener('scroll', handler, { passive: true });
}

export function unwatchScroll() {
    window.removeEventListener('scroll', handler);
    document.removeEventListener('keyup', keys);
    document.removeEventListener('click', launch, true);
}

// "Hunt now" (a link with data-launch="<link of the launcher>", href /download): on Windows it opens the launcher's link and stays on the page.
// A page can't ask whether the link is registered, so if neither the window blurred nor the page hid within 2 s (the browser's prompt or the
// launcher's window took the focus), the launcher is not installed and the page goes to the href. Elsewhere, and with a modified click, the href works as usual.
// Capture phase: Blazor's own link handling must see a click that was already handled.
function launch(e) {
    const a = e.target.closest?.('a[data-launch]');
    if (!a || e.button || e.ctrlKey || e.shiftKey || e.altKey || e.metaKey || !/Windows NT/.test(navigator.userAgent)) return;
    e.preventDefault();
    let opened = false;
    const seen = () => { opened = true; };
    window.addEventListener('blur', seen);
    document.addEventListener('visibilitychange', seen);
    // in a hidden frame: Firefox shows an error page when a top-level address has no application, a frame stays silent
    const frame = Object.assign(document.createElement('iframe'), { hidden: true, src: a.dataset.launch });
    const here = location.href;
    document.body.append(frame);
    setTimeout(() => {
        frame.remove();
        window.removeEventListener('blur', seen);
        document.removeEventListener('visibilitychange', seen);
        if (!opened && location.href === here) location.assign(a.href);
    }, 2000);
}

export function watchLaunch() {
    document.addEventListener('click', launch, true);
}

// Ctrl+D in the launcher window opens or closes the developer tools, as the old launcher did (on key up); calls ref.DevTools().
let keys;

export function watchKeys(ref) {
    keys = (e) => { if (e.ctrlKey && e.key.toLowerCase() === 'd') ref.invokeMethodAsync('DevTools'); };
    document.addEventListener('keyup', keys);
}

// Calls ref.Moved() once, at the next mouse move; returns what cancels the wait.
export function onceMouse(ref) {
    const h = () => { document.removeEventListener('mousemove', h); ref.invokeMethodAsync('Moved'); };
    document.addEventListener('mousemove', h);
    return () => document.removeEventListener('mousemove', h);
}

// Calls ref.Dismiss() once, at the next click anywhere (capture phase: the click still does what it was for); a ref that is gone by then is ignored.
export function onceClick(ref) {
    document.addEventListener('click', () => ref.invokeMethodAsync('Dismiss').catch(() => { }), { capture: true, once: true });
}

// Opens or closes the modal host's <dialog>. Modal: the page behind is inert and focus stays inside. Escape does nothing, as in the old app
// (a dialog that wants it handles the key itself); a browser that closes the dialog anyway after repeated Escape gets it opened again.
// Called after every render of the host: once the content is there and nothing inside has focus, the [autofocus] element gets it (no ring).
// closeOnBackdrop: a click on the dialog itself (its backdrop, not its content) calls ref.Dismiss().
export function dialog(el, want, modal, ref, closeOnBackdrop) {
    const show = () => (el.dataset.modal ? el.showModal() : el.show());
    el.oncancel = (e) => e.preventDefault();
    el.onclick = (e) => { if (closeOnBackdrop && e.target === el) ref.invokeMethodAsync('Dismiss'); };
    el.onclose = () => { if (el.dataset.want) show(); };
    if (el.open && (el.dataset.modal === '1') !== modal) { delete el.dataset.want; el.close(); }
    el.dataset.modal = modal ? '1' : '';
    if (!want) { delete el.dataset.want; if (el.open) el.close(); return; }
    el.dataset.want = '1';
    if (!el.open) show();
    if (!el.contains(document.activeElement) || document.activeElement === el) el.querySelector('[autofocus]')?.focus({ focusVisible: false });
}

// Scrolls the pack of the missions page (data-pack) into view, once it is open.
export function scrollToPack(id) {
    requestAnimationFrame(() => document.querySelector(`[data-pack="${id}"]`)?.scrollIntoView({ block: 'start' }));
}

// Saves a file made in the app through the browser (a stream from the server side).
export async function save(name, stream) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([await stream.arrayBuffer()]));
    a.download = name;
    a.click();
    URL.revokeObjectURL(a.href);
}

// Closes the popover with the given id (the friends list after "Send message").
export function hidePopover(id) {
    document.getElementById(id)?.hidePopover();
}

// Opens the popover with the given id again if the browser closed it (the friends list after a dialog took the focus).
export function showPopover(id) {
    const el = document.getElementById(id);
    if (el && !el.matches(':popover-open')) el.showPopover();
}

// True the first time this browser asks for the cookie `name`, which it then sets for a year (the backup tool dialog opens once).
export function firstTime(name) {
    if (document.cookie.split('; ').some((c) => c.startsWith(name + '='))) return false;
    document.cookie = `${name}=1; max-age=31536000; path=/; samesite=lax; secure`;
    return true;
}

// Copies text to the clipboard (the Steam launch option line).
export function copy(text) {
    return navigator.clipboard.writeText(text);
}

// Pan and zoom of a map with the zoom buttons (MapPlaceholder): drag (mouse, pen, one finger), wheel around the cursor, arrow keys and +/- while the map has focus.
// The view lives in map.dataset (s: scale 1..max, x/y: pan in percent of the map, applied before the scale; the picture's edge can't pass the map's edge)
// and the plane is moved at once; ref.View(s, x, y) tells the component where a gesture ended (a round trip per pointer move would lag on the website).
export function panZoom(map, ref, max) {
    const plane = map.querySelector('.plane');
    let down, moved, wait;
    const get = () => ({ s: +map.dataset.s, x: +map.dataset.x, y: +map.dataset.y });
    const set = (s, x, y) => {
        s = Math.min(max, Math.max(1, s));
        const limit = 50 * (s - 1) / s;
        x = Math.min(limit, Math.max(-limit, x));
        y = Math.min(limit, Math.max(-limit, y));
        Object.assign(map.dataset, { s, x, y });
        plane.style.transform = `scale(${s}) translate(${x}%, ${y}%)`;
    };
    const sync = (delay) => {
        clearTimeout(wait);
        if (down?.drag) return; // the drag reports the view where it ends
        wait = setTimeout(() => { const v = get(); ref.invokeMethodAsync('View', v.s, v.x, v.y).catch(() => { }); }, delay);
    };

    // A press that moves less than 4 px stays a click (a marker is picked). Past that the map takes the pointer, and the click that ends the drag is swallowed.
    map.addEventListener('pointerdown', (e) => {
        moved = false;
        down = e.isPrimary && !e.button && !e.target.closest('.zoom') ? { id: e.pointerId, x: e.clientX, y: e.clientY, drag: false } : null;
    });
    map.addEventListener('pointermove', (e) => {
        if (!down || e.pointerId !== down.id) return;
        if (!down.drag) {
            if (Math.hypot(e.clientX - down.x, e.clientY - down.y) < 4) return;
            down.drag = moved = true;
            clearTimeout(wait);
            map.setPointerCapture(e.pointerId);
            map.classList.add('dragging');
        }
        const r = map.getBoundingClientRect(), v = get();
        set(v.s, v.x + (e.clientX - down.x) / (r.width * v.s) * 100, v.y + (e.clientY - down.y) / (r.height * v.s) * 100);
        down.x = e.clientX;
        down.y = e.clientY;
    });
    const end = (e) => {
        if (!down || e.pointerId !== down.id) return;
        const dragged = down.drag;
        down = null;
        map.classList.remove('dragging');
        if (!dragged) return;
        sync(0);
        setTimeout(() => { moved = false; }, 0); // a drag that ends without a click must not swallow the next one (a key press on a marker)
    };
    map.addEventListener('pointerup', end);
    map.addEventListener('pointercancel', end);
    map.addEventListener('click', (e) => { if (moved) { moved = false; e.stopPropagation(); e.preventDefault(); } }, true);
    map.addEventListener('dragstart', (e) => e.preventDefault()); // a marker's picture is not dragged away with the pointer

    // Not passive: the page must not scroll while the wheel is on the map.
    map.addEventListener('wheel', (e) => {
        e.preventDefault();
        const r = map.getBoundingClientRect(), v = get();
        const dy = e.deltaY * (e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? 400 : 1);
        const s = Math.min(max, Math.max(1, v.s * Math.exp(-dy * .002)));
        const px = ((e.clientX - r.left) / r.width - .5) * 100, py = ((e.clientY - r.top) / r.height - .5) * 100;
        // the point under the cursor stays under it: its screen place is 50 + s * (its place on the plane - 50 + pan)
        set(s, v.x + px * (1 / s - 1 / v.s), v.y + py * (1 / s - 1 / v.s));
        sync(150);
    }, { passive: false });

    map.addEventListener('keydown', (e) => {
        if (e.ctrlKey || e.altKey || e.metaKey) return;
        const v = get(), step = 10 / v.s;
        const pan = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] }[e.key];
        const zoom = { '+': .5, '=': .5, '-': -.5, '_': -.5 }[e.key];
        if (!pan && !zoom) return;
        e.preventDefault();
        set(v.s + (zoom ?? 0), v.x + (pan?.[0] ?? 0), v.y + (pan?.[1] ?? 0));
        sync(150);
    });
}
