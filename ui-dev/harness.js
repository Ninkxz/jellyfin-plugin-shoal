/*
 * Development harness: loads the real shoal.html / shoal.js / shoal.css the way Jellyfin does
 * (configurationpage?name=Shoal[.js|.css]) and gives them a fake ApiClient backed by mock/mock-server.js.
 *
 * Query: ?theme=&scenario=&view=&id=&dir=&pairing=&probe=
 */
import { createMock, SCENARIOS } from '/ui-dev/mock/mock-server.js';
import { createMockClient } from '/ui-dev/mock/mock-client.js';

const q = new URLSearchParams(location.search);
const theme = q.get('theme') || 'dark';
const scenario = q.get('scenario') || 'default';
document.documentElement.dataset.theme = theme;

const ctl = document.getElementById('ctl');
const scenarioSelect = ctl.elements.scenario;
for (const s of SCENARIOS) {
    const o = document.createElement('option');
    o.textContent = s;
    scenarioSelect.append(o);
}

for (const [k, v] of q.entries()) {
    if (ctl.elements[k]) {
        ctl.elements[k].value = v;
    }
}

ctl.elements.theme.value = theme;
scenarioSelect.value = scenario;

const mock = createMock({ scenario, pairing: q.get('pairing') || 'ok', probeError: q.get('probe') || '' });
const client = createMockClient(mock);
window.__mock = mock;
window.ApiClient = client;

const main = document.getElementById('main');
let current = null;
let htmlCache = null;

async function pageHtml() {
    if (!htmlCache) {
        htmlCache = await (await fetch('/configurationpage?name=Shoal', { cache: 'no-store' })).text();
    }

    return htmlCache;
}

async function show(params) {
    if (current) {
        current.dispatchEvent(new CustomEvent('viewhide', { bubbles: true }));
        current.dispatchEvent(new CustomEvent('viewdestroy', { bubbles: true }));
        current.remove();
    }

    const holder = document.createElement('div');
    holder.innerHTML = await pageHtml();
    const page = holder.firstElementChild;
    main.append(page);
    current = page;
    const mod = await import('/configurationpage?name=Shoal.js');
    mod.mount(page, params, {
        api: mod.createApi(client), client, dashboard: window.Dashboard, now: () => Date.now(), locale: document.documentElement.lang || undefined,
        storage: { get: (k) => { try { return localStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* ignore */ } } },
        features: { appsWork: q.get('apps') === '1' }
    });
    page.dispatchEvent(new CustomEvent('viewshow', { bubbles: true, detail: { isRestored: false, params } }));
    window.__page = page;
}

function paramsFromLocation() {
    const p = {};
    for (const key of ['view', 'id', 'dir', 'from', 'focus', 'again', 'fresh']) {
        if (q.get(key)) {
            p[key] = q.get(key);
        }
    }

    return p;
}

window.Dashboard = {
    navigate(path) {
        const u = new URL(path, location.href);
        const next = new URLSearchParams(location.search);
        for (const key of ['view', 'id', 'dir', 'from', 'focus', 'again', 'fresh']) {
            next.delete(key);
        }

        for (const [k, v] of u.searchParams.entries()) {
            if (k !== 'name') {
                next.set(k, v);
            }
        }

        history.pushState({}, '', location.pathname + '?' + next.toString());
        for (const key of Array.from(q.keys())) {
            if (['view', 'id', 'dir', 'from', 'focus', 'again', 'fresh'].includes(key)) {
                q.delete(key);
            }
        }

        for (const [k, v] of next.entries()) {
            q.set(k, v);
        }

        return show(paramsFromLocation());
    }
};

window.addEventListener('popstate', () => {
    const now = new URLSearchParams(location.search);
    for (const key of Array.from(q.keys())) {
        q.delete(key);
    }

    for (const [k, v] of now.entries()) {
        q.set(k, v);
    }

    show(paramsFromLocation());
});

show(paramsFromLocation());
