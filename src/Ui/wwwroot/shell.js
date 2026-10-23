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
