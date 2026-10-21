// Test helpers: run the real shoal.html + shoal.js under jsdom against the mock server.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { JSDOM, VirtualConsole } from 'jsdom';
import { createMock } from '../mock/mock-server.js';
import { createMockClient } from '../mock/mock-client.js';

const here = path.dirname(fileURLToPath(import.meta.url));
export const cfgDir = path.resolve(here, '../../src/Jellyfin.Plugin.Shoal/Configuration');
export const html = fs.readFileSync(path.join(cfgDir, 'shoal.html'), 'utf8');
export const css = fs.readFileSync(path.join(cfgDir, 'shoal.css'), 'utf8');
export const js = fs.readFileSync(path.join(cfgDir, 'shoal.js'), 'utf8');

/** A golden reply from tests/contracts (shared with the backend tests), as { status, body } for a mock option such as remoteLibrariesGolden. */
export function golden(name, status) {
    return { status, body: JSON.parse(fs.readFileSync(path.resolve(here, '../../tests/contracts', name + '.json'), 'utf8')) };
}

let shoalModule = null;
let dom = null;

/** One jsdom window per test process; every mounted page lives in it. */
export function ensureDom() {
    if (dom) {
        return dom;
    }

    const vc = new VirtualConsole();
    vc.on('jsdomError', (e) => {
        if (!/Not implemented/.test(String(e.message))) {
            console.error(e.message);
        }
    });
    dom = new JSDOM('<!doctype html><html lang="en" data-theme="dark"><body><div class="mainAnimatedPages"></div></body></html>', { url: 'http://localhost/', pretendToBeVisual: true, virtualConsole: vc });
    const w = dom.window;
    for (const key of ['window', 'document', 'Node', 'Element', 'HTMLElement', 'Event', 'CustomEvent', 'MutationObserver', 'DocumentFragment', 'getComputedStyle', 'requestAnimationFrame', 'localStorage']) {
        Object.defineProperty(globalThis, key, { value: key === 'window' ? w : w[key], configurable: true, writable: true });
    }

    w.scrollTo = () => {};
    if (!w.matchMedia) {
        w.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
    }

    return dom;
}

export async function loadShoal() {
    ensureDom();
    if (!shoalModule) {
        shoalModule = await import(pathToFileURL(path.join(cfgDir, 'shoal.js')).href + '?t=' + Date.now());
    }

    return shoalModule;
}

/** Text a user could see: skips [hidden] subtrees, templates, svg and the icon sprite. */
export function visibleText(node) {
    const parts = [];
    const walk = (n) => {
        if (n.nodeType === 3) {
            parts.push(n.textContent);
            return;
        }

        if (n.nodeType !== 1) {
            return;
        }

        const tag = n.tagName.toLowerCase();
        if (n.hasAttribute('hidden') || tag === 'template' || tag === 'svg' || tag === 'script' || tag === 'style') {
            return;
        }

        for (const c of n.childNodes) {
            walk(c);
        }

        if (/^(p|div|li|h1|h2|h3|dt|dd|section|article|tr|br)$/.test(tag)) {
            parts.push(' ');
        }
    };
    walk(node);
    return parts.join('').replace(/\s+/g, ' ').trim();
}

/** A clock the test moves by hand (the mock server and the page read the same one). */
/** Text that must never reach a screen: a stringified null/undefined/NaN or an unfilled {placeholder} (Lumen M3). */
export const JUNK = /null|undefined|NaN|{[a-zA-Z]+}/;

export function makeClock(start) {
    let t = start || Date.now();
    return { now: () => t, advance(ms) { t += ms; } };
}

/**
 * Mounts one page instance and waits for the first load. Returns { page, mock, handle, wait, close, text }.
 * opts: scenario, params (view, id, ...), mock options (pairing, probeError, ...), now.
 */
export async function mountPage(opts) {
    const o = Object.assign({ scenario: 'default', params: {}, now: () => Date.now() }, opts || {});
    const mod = await loadShoal();
    const d = ensureDom();
    const { scenario, params, now, settleMs, features, nulls, emptyBodies, garbage, installed, latest, noCatalog, ...mockOptions } = o;
    const mock = createMock({ scenario, now, pairing: 'ok', probeError: '', delayMs: 0, ...mockOptions });
    const client = createMockClient(mock, { nulls, emptyBodies, garbage, installed, latest, noCatalog });
    const holder = d.window.document.createElement('div');
    holder.innerHTML = html;
    const page = holder.firstElementChild;
    d.window.document.querySelector('.mainAnimatedPages').append(page);
    d.window.ApiClient = client;
    const navigations = [];
    const handle = mod.mount(page, o.params, {
        api: mod.createApi(client), client, dashboard: { navigate: async (p) => { navigations.push(p); } }, now: o.now, locale: 'en-GB',
        storage: { get: () => null, set: () => {} }, features
    });
    page.dispatchEvent(new d.window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: false } }));
    const wait = async (ms) => new Promise((r) => setTimeout(r, ms || 30));
    await wait(o.settleMs || 120);
    return {
        page, mock, handle, wait, mod, navigations,
        text: () => visibleText(page),
        close() {
            page.dispatchEvent(new d.window.CustomEvent('viewhide', { bubbles: true }));
            page.dispatchEvent(new d.window.CustomEvent('viewdestroy', { bubbles: true }));
            page.remove();
        }
    };
}

/**
 * A clock that runs from noon today (local), so relative times ("2 hours ago") do not turn into "yesterday at ..." when the tests run just after midnight.
 * Pass it as mountPage({ now: middayNow() }).
 */
export function middayNow() {
    const noon = new Date(Date.now());
    noon.setHours(12, 0, 0, 0);
    const offset = Date.now() - noon.getTime();
    return () => Date.now() - offset;
}
