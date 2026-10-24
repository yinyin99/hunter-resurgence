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
