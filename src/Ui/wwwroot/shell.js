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
}

// Calls ref.Moved() once, at the next mouse move; returns what cancels the wait.
export function onceMouse(ref) {
    const h = () => { document.removeEventListener('mousemove', h); ref.invokeMethodAsync('Moved'); };
    document.addEventListener('mousemove', h);
    return () => document.removeEventListener('mousemove', h);
}
