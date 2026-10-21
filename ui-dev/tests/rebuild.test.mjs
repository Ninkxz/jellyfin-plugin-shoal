// Pilot 27 (docs/tests/pilot-27-v2.9.3-ui-repro.md, NOTES-2.9.3 item 4): jellyfin-web restores its cached page and Shoal builds the screen again
// on the same host. Every build must start from the page's own markup, so a return never adds a second set of rows, notes or panels.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

function setHash(query) {
    window.location.hash = '#/configurationpage?name=Shoal&' + query;
}

/** What jellyfin-web does on Back or on a link to a page it has cached: hide, change the address, show the same page element again. */
async function restore(p, query, ms) {
    p.page.dispatchEvent(new window.CustomEvent('viewhide', { bubbles: true }));
    setHash(query);
    p.page.dispatchEvent(new window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: true } }));
    await p.wait(ms || 350);
}

const section = (p, view) => p.page.querySelector('[data-shoal-view="' + view + '"]');
const textCount = (node, needle) => node.textContent.split(needle).length - 1;

function overviewCounts(p) {
    const ov = section(p, 'overview');
    const rows = Array.from(ov.querySelectorAll('[data-slot="cards"] > *'));
    return {
        rows: rows.length,
        handles: new Set(rows.map((r) => r.dataset.handle)).size,
        footnotes: textCount(ov, 'Titles are movies and series'),
        speedPanels: ov.querySelector('[data-slot="speedhost"]').children.length,
        speedTitles: Array.from(ov.querySelectorAll('h2')).filter((h) => h.textContent.trim() === 'Link speed').length,
        versions: ov.querySelectorAll('.shoal-version').length
    };
}

test('Pilot 27: Overview built again on the same page keeps one row per server, one footnote, one speed panel and one poll', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 350 });
    try {
        const first = overviewCounts(p);
        assert.ok(first.rows >= 2, 'the scenario has servers');
        assert.equal(first.rows, first.handles);
        assert.equal(first.footnotes, 1);
        assert.equal(first.speedPanels, 1);
        assert.equal(first.speedTitles, 1);

        // Overview, a friend, back to Overview, three times (Pilot 27 4c/4d), then Back onto the cached Overview (4e).
        for (let i = 0; i < 3; i++) {
            await restore(p, 'view=server&id=h-hops');
            await restore(p, 'view=overview');
            assert.deepEqual(overviewCounts(p), first, 'return ' + (i + 1));
        }

        await restore(p, 'view=overview');
        assert.deepEqual(overviewCounts(p), first, 'restored onto itself');

        // One live poll: a visible page asks Status once, not once per old build.
        const statusCalls = () => p.mock.calls.filter((c) => c.method === 'GET' && c.path === 'Status').length;
        const before = statusCalls();
        document.dispatchEvent(new window.Event('visibilitychange'));
        await p.wait(150);
        assert.equal(statusCalls() - before, 1, 'one poll');
    } finally {
        p.close();
        window.location.hash = '';
    }
});

test('Pilot 27: a hash change from a friend page back to Overview builds the Overview once', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 350 });
    try {
        const first = overviewCounts(p);
        for (const query of ['view=friend&id=f-sam', 'view=overview', 'view=libraries', 'view=overview']) {
            setHash(query);
            window.dispatchEvent(new window.HashChangeEvent('hashchange'));
            await p.wait(300);
        }

        assert.equal(p.page.querySelector('.shoal-root').dataset.view, 'overview');
        assert.deepEqual(overviewCounts(p), first);
    } finally {
        p.close();
        window.location.hash = '';
    }
});

// Every screen that fills its section: the second build on the same host has exactly the nodes the first had.
const SCREENS = [
    ['libraries', 'view=libraries', {}],
    ['settings', 'view=settings', {}],
    ['layouts', 'view=layouts', {}],
    ['server', 'view=server&id=h-hops', { id: 'h-hops' }],
    ['friend', 'view=friend&id=f-sam', { id: 'f-sam' }],
    ['overlap', 'view=overlap&id=h-hops', { id: 'h-hops' }]
];

for (const [view, query, extra] of SCREENS) {
    test('Pilot 27: ' + view + ' built again on the same page adds nothing', async () => {
        const p = await mountPage({ scenario: 'default', params: Object.assign({ view }, extra), settleMs: 400 });
        try {
            const size = () => section(p, view).querySelectorAll('*').length;
            const first = size();
            assert.ok(first > 20, 'the screen rendered');
            await restore(p, 'view=overview');
            await restore(p, query, 400);
            await restore(p, query, 400);
            assert.equal(p.page.querySelector('.shoal-root').dataset.view, view);
            assert.equal(size(), first);
            assert.equal(p.page.querySelectorAll('[data-shoal-view="' + view + '"]').length, 1, 'one section');
        } finally {
            p.close();
            window.location.hash = '';
        }
    });
}
