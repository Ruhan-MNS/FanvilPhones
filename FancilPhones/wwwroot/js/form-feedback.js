// Conventional authentication POSTs leave the current document; give them feedback too.
const pendingForms = new Map();
document.addEventListener("submit", event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-loading-label") || event.defaultPrevented) return;
    if (pendingForms.has(form)) { event.preventDefault(); return; }
    const button = form.querySelector('button[type="submit"]');
    if (!button) return;
    pendingForms.set(form, { button, nodes: [...button.childNodes], disabled: button.disabled });
    const spinner = document.createElement("span");
    spinner.className = "busy-spinner";
    spinner.setAttribute("aria-hidden", "true");
    const text = document.createElement("span");
    text.textContent = form.dataset.loadingLabel;
    button.replaceChildren(spinner, text);
    button.disabled = true;
    button.classList.add("is-busy");
    button.setAttribute("aria-busy", "true");
    document.documentElement.classList.add("is-submitting");
});
window.addEventListener("pageshow", () => {
    for (const { button, nodes, disabled } of pendingForms.values()) {
        button.replaceChildren(...nodes);
        button.disabled = disabled;
        button.classList.remove("is-busy");
        button.removeAttribute("aria-busy");
    }
    pendingForms.clear();
    document.documentElement.classList.remove("is-submitting");
});
