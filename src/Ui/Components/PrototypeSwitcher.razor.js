// PROTOTYPE: ← and → flip the variant, unless a field has the focus.
let handler;
export function bind(ref) {
    unbind();
    handler = e => {
        if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.target.closest("input, textarea, select, [contenteditable]")) return;
        if (e.key === "ArrowLeft") ref.invokeMethodAsync("Step", -1);
        else if (e.key === "ArrowRight") ref.invokeMethodAsync("Step", 1);
    };
    addEventListener("keydown", handler);
}
export function unbind() { if (handler) removeEventListener("keydown", handler); handler = undefined; }
