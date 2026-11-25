const prefix = "dotmarc-list:";
const lifetime = 30 * 60 * 1000;
let current = null;
let snapshot = null;
let restoredScroll = 0;

function storage() {
    return window.sessionStorage;
}

function clearForSignOut(event) {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const path = new URL(form.action, location.href).pathname;
    if (path !== "/signout" && !path.startsWith("/demo/sign-in/")) return;
    const store = storage();
    for (const key of Object.keys(store)) if (key.startsWith(prefix)) store.removeItem(key);
    current = null;
    snapshot = null;
}

document.addEventListener("submit", clearForSignOut, true);

function save() {
    if (!current || !snapshot) return;
    storage().setItem(prefix + current.token, JSON.stringify({
        version: 1, user: current.user, route: current.route, url: current.url,
        time: Date.now(), state: snapshot, scroll: window.scrollY
    }));
}

function openDomain(event) {
    const link = event.target.closest("a[data-domain-link]");
    if (!current || !link || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    save();
    const origin = { version: 1, token: current.token, user: current.user, route: current.route, url: location.pathname + location.search };
    event.preventDefault();
    event.stopImmediatePropagation();
    Blazor.navigateTo(link.href, { historyEntryState: JSON.stringify({ domainListOrigin: origin }) });
}

export function enter(route, user) {
    leave();
    const store = storage();
    if (store.getItem(prefix + "user") !== user) {
        for (const key of Object.keys(store)) if (key.startsWith(prefix)) store.removeItem(key);
        store.setItem(prefix + "user", user ?? "");
    }
    const marker = history.state?.userState;
    let token = null;
    if (marker) {
        try { token = JSON.parse(marker).domainListEntry; }
        catch { throw new Error("Unreadable list history marker."); }
    }
    let state = null;
    if (token) {
        const json = store.getItem(prefix + token);
        if (json) {
            const entry = JSON.parse(json);
            store.removeItem(prefix + token);
            if (entry.version === 1 && entry.user === user && entry.route === route && entry.url === location.pathname + location.search
                && Date.now() - entry.time < lifetime && Date.now() >= entry.time) {
                state = entry.state;
                restoredScroll = entry.scroll;
            }
        }
    }
    token = crypto.randomUUID();
    history.replaceState({ ...history.state, userState: JSON.stringify({ domainListEntry: token }) }, "");
    current = { route, user, token, url: location.pathname + location.search };
    document.addEventListener("click", openDomain, true);
    window.addEventListener("pagehide", save);
    return state;
}

export function update(state) {
    snapshot = state;
    if (current) {
        current.url = location.pathname + location.search;
        history.replaceState({ ...history.state, userState: JSON.stringify({ domainListEntry: current.token }) }, "");
    }
    save();
}

export function restore() {
    window.scrollTo(0, Math.min(restoredScroll, Math.max(0, document.documentElement.scrollHeight - innerHeight)));
}

export function top() {
    window.scrollTo(0, 0);
}

export function focusAddDomain() {
    const form = document.getElementById("add-domain");
    const input = form?.querySelector("input");
    if (!form || !input || input.disabled) throw new Error("The Add domain form is not ready.");
    form.scrollIntoView({ block: "center", behavior: "instant" });
    input.focus({ preventScroll: true });
}

export function leave() {
    if (current && location.pathname === current.route) save();
    document.removeEventListener("click", openDomain, true);
    window.removeEventListener("pagehide", save);
    current = null;
    snapshot = null;
}

export function back() {
    const marker = history.state?.userState;
    if (marker) {
        const origin = JSON.parse(marker).domainListOrigin;
        if (origin && origin.version === 1 && ["/domains", "/dashboard"].includes(origin.route)
            && origin.user === storage().getItem(prefix + "user")) {
            const json = storage().getItem(prefix + origin.token);
            if (json) {
                const entry = JSON.parse(json);
                if (entry.user === origin.user && entry.route === origin.route && entry.url === origin.url
                    && Date.now() - entry.time < lifetime && Date.now() >= entry.time) {
                    history.back();
                    return;
                }
            }
        }
    }
    Blazor.navigateTo("/domains");
}
