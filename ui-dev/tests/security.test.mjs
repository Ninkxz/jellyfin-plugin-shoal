// T-SEC-11 / T-PAIR-7 / ui-spec F3, F4, F5, F6: names that arrive from a friend's server (server, library, title,
// device and Home names, fingerprints, user agents) render as text on every screen. No markup is ever built from them,
// no image points anywhere but this server, and nothing secret reaches the DOM.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';
import { XSS } from '../mock/mock-server.js';

const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;

/** The checks every screen must pass with hostile data. */
function assertSafe(p, label) {
    const page = p.page;
    assert.equal(page.querySelectorAll('[onerror], [onload], [onclick]').length, 0, label + ': inline handler');
    for (const node of page.querySelectorAll('*')) {
        for (const attr of node.getAttributeNames()) {
            assert.ok(!/^on/i.test(attr), label + ': attribute ' + attr);
            if (attr === 'style') {
                assert.ok(/^(--shoal-[a-z0-9-]+:\s*[^;]+;?\s*)+$/i.test(node.getAttribute('style')), label + ': style attribute ' + node.getAttribute('style'));
            }
        }

        if (node.tagName === 'SCRIPT' || node.tagName === 'IFRAME' || node.tagName === 'OBJECT' || node.tagName === 'EMBED') {
            assert.fail(label + ': ' + node.tagName);
        }
    }

    // An <img> built from the payload would have src="x".
    assert.equal(page.querySelectorAll('img[src="x"]').length, 0, label + ': payload became an element');
    for (const img of page.querySelectorAll('img')) {
        const src = img.getAttribute('src') || '';
        assert.ok(src === '' || /^\/(Items\/[^/]+\/Images\/|Users\/[^/]+\/Images\/)/.test(src), label + ': image source ' + src);
    }

    for (const a of page.querySelectorAll('a[href]')) {
        const href = a.getAttribute('href');
        assert.ok(/^(#|#\/)/.test(href), label + ': link ' + href);
    }

    const html = page.innerHTML;
    assert.doesNotMatch(html, /shoal1-[0-9a-f]{8}-/i, label + ': invite in the DOM');
    assert.doesNotMatch(html.replace(/id="[^"]*"/g, ''), GUID, label + ': GUID in the DOM');
    assert.doesNotMatch(html, /Bearer |X-Emby-Token|ApiKey=/i, label + ': credential');
}

const SCREENS = [
    { name: 'overview', params: { view: 'overview' } },
    { name: 'libraries', params: { view: 'libraries' } },
    { name: 'settings', params: { view: 'settings' } },
    { name: 'server', params: { view: 'server', id: 'h-hops' } },
    { name: 'friend', params: { view: 'friend', id: 'f-sam' } },
    { name: 'overlap', params: { view: 'overlap', id: 'h-hops' } }
];

for (const s of SCREENS) {
    test('T-SEC-11 ' + s.name + ': hostile names render as text only', async () => {
        const p = await mountPage({ scenario: 'hostile', params: s.params, settleMs: 250 });
        try {
            assertSafe(p, s.name);
            assert.ok(p.text().includes(XSS), s.name + ': the payload should be visible as text');
        } finally {
            p.close();
        }
    });
}

test('T-SEC-11 recovery and key banners stay safe', async () => {
    for (const scenario of ['recovery', 'recoveryNewer', 'keys', 'restored']) {
        const p = await mountPage({ scenario, params: { view: 'overview' }, settleMs: 200 });
        try {
            assertSafe(p, scenario);
            assert.ok(p.page.querySelector('.shoal-banner'), scenario + ': a banner shows');
        } finally {
            p.close();
        }
    }
});

test('image sources are always this server (ui-spec 10, F4)', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 250 });
    try {
        const imgs = [...p.page.querySelectorAll('img')];
        assert.ok(imgs.length > 0);
        for (const img of imgs) {
            assert.match(img.getAttribute('src'), /^\/Items\/item-[a-z0-9-]+\/Images\/(Backdrop|Primary)\?/);
        }
    } finally {
        p.close();
    }
});

test('the admin API responses carry no token, invite or GUID (F5)', async () => {
    const { createMock } = await import('../mock/mock-server.js');
    const mock = createMock({ scenario: 'problems' });
    const paths = ['Status', 'Libraries', 'Settings', 'Hardening', 'State', 'Shares', 'Peers/h-hops', 'Peers/h-hops/Overlap', 'Peers/h-hops/Speed?range=24h', 'Shares/f-sam', 'Shares/f-sam/Approval'];
    for (const path of paths) {
        const res = await mock.handle('GET', path);
        const text = JSON.stringify(res.body);
        assert.doesNotMatch(text, GUID, path);
        assert.doesNotMatch(text, /shoal1-/i, path);
        assert.doesNotMatch(text, /"(token|accessToken|apiKey|secret|password|sas|nonce|homeNonce|commit|deviceId|accessKey)"s*:/i, path);
        // Peer-side ids travel as 32 hex characters without dashes (N format): none may appear, except the local handles and 8-char prefixes.
        assert.doesNotMatch(text, /"[0-9a-f]{33,}"/i, path);
    }
});
