// 2.10.3 T2: Library management (?view=manage&tab=overlap|remove&id=<handle>), docs/plans/atlas-ui-rework-2.10.3.md and lumen-ui-rework-2.10.3.md section 1.
// The screen is a shell: two real tabs holding the existing preview and removal panels, unchanged.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, golden, visibleText } from './helpers.mjs';

const epoch = Date.parse('2026-10-25T12:00:00Z');
// 2.10.4: the Remove tab holds Delete from this server; the mock serves the C0 goldens (local-delete.*).
const fixture = (name) => golden('local-delete.' + name, 200);
const removal = () => {
    const state = fixture('state');
    state.body.activeBatchId = null;
    return { state, global: fixture('off'), start: golden('local-delete.start', 202), current: golden('local-delete.current-none', 404),
        restore: golden('removal-operations.restored', 200), setting: golden('removal-operations.setting-on', 200) };
};
const mount = (params, extra = {}) => mountPage({ params: Object.assign({ view: 'manage' }, params), now: () => epoch, localRemovalPreviewGolden: golden('local-removal-preview.get', 200), localDeleteGolden: removal(), settleMs: 220, ...extra });
const view = (p) => p.page.querySelector('[data-shoal-view="manage"]');
const tabs = (p) => [...view(p).querySelectorAll('[role="tab"]')];
const tab = (p, name) => view(p).querySelector('[role="tab"][data-tab="' + name + '"]');
const panel = (p, name) => view(p).querySelector('[data-slot="tabpanel-' + name + '"]');
const previewCalls = (p) => p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview'));
const removalCalls = (p) => p.mock.calls.filter((c) => c.method === 'GET' && /^Peers\/[^/]+\/LocalRemoval$/.test(c.path));

test('manage opens on Overlap: h1, a real tablist with two tabs, the preview panel in the first, nothing loaded for the second', async () => {
    const p = await mount({ id: 'h-hops' });
    try {
        assert.equal(view(p).hidden, false);
        assert.equal(visibleText(view(p).querySelector('h1')), 'Library management');
        const list = view(p).querySelector('[role="tablist"]');
        assert.equal(list.hidden, false);
        assert.equal(list.getAttribute('aria-label'), 'Library management sections');
        assert.deepEqual(tabs(p).map((b) => [b.textContent, b.getAttribute('aria-selected'), b.tabIndex]), [['Overlap', 'true', 0], ['Remove local copies', 'false', -1]]);
        for (const b of tabs(p)) {
            const target = p.page.querySelector('#' + b.getAttribute('aria-controls'));
            assert.equal(target.getAttribute('role'), 'tabpanel');
            assert.equal(p.page.querySelector('#' + target.getAttribute('aria-labelledby')), b);
        }

        assert.equal(panel(p, 'overlap').hidden, false);
        assert.equal(panel(p, 'remove').hidden, true);
        assert.ok(panel(p, 'overlap').querySelector('[data-local-removal-preview]'));
        assert.equal(panel(p, 'remove').children.length, 0);
        assert.equal(previewCalls(p).length, 1);
        assert.match(previewCalls(p)[0].path, /^Peers\/h-hops\/LocalRemovalPreview\?offset=0&limit=50$/);
        assert.equal(removalCalls(p).length, 0);
        // Back goes to Overview (the sub-screen back bar).
        assert.equal(visibleText(p.page.querySelector('[data-slot="backlabel"]')), 'Overview');
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally {
        p.close();
    }
});

test('the Remove local copies tab mounts the existing removal panel for the same server, once', async () => {
    const p = await mount({ id: 'h-hops' });
    try {
        tab(p, 'remove').click();
        await p.wait(200);
        assert.equal(tab(p, 'remove').getAttribute('aria-selected'), 'true');
        assert.equal(tab(p, 'overlap').getAttribute('aria-selected'), 'false');
        assert.equal(panel(p, 'overlap').hidden, true);
        assert.equal(panel(p, 'remove').hidden, false);
        assert.ok(panel(p, 'remove').querySelector('[data-removal-operations]'));
        assert.deepEqual(removalCalls(p).map((c) => c.path), ['Peers/h-hops/LocalRemoval']);
        // Back to Overlap and again: the panels are kept, not built or loaded again.
        tab(p, 'overlap').click();
        tab(p, 'remove').click();
        await p.wait(100);
        assert.equal(removalCalls(p).length, 1);
        assert.equal(previewCalls(p).length, 1);
        assert.equal(view(p).querySelectorAll('[data-removal-operations]').length, 1);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally {
        p.close();
    }
});

test('tab=remove opens on the Remove tab; an unknown tab falls back to Overlap', async () => {
    const p = await mount({ id: 'h-hops', tab: 'remove' });
    try {
        assert.equal(tab(p, 'remove').getAttribute('aria-selected'), 'true');
        assert.equal(panel(p, 'remove').hidden, false);
        assert.equal(previewCalls(p).length, 0);
        assert.equal(removalCalls(p).length, 1);
    } finally {
        p.close();
    }

    const q = await mount({ id: 'h-hops', tab: 'purge' });
    try {
        assert.equal(tab(q, 'overlap').getAttribute('aria-selected'), 'true');
    } finally {
        q.close();
    }
});

test('arrow keys, Home and End move between tabs and select them', async () => {
    const p = await mount({ id: 'h-hops' });
    try {
        const key = (k) => document.activeElement.dispatchEvent(new window.KeyboardEvent('keydown', { key: k, bubbles: true }));
        tab(p, 'overlap').focus();
        key('ArrowRight');
        assert.equal(document.activeElement, tab(p, 'remove'));
        assert.equal(tab(p, 'remove').getAttribute('aria-selected'), 'true');
        key('ArrowRight');
        assert.equal(document.activeElement, tab(p, 'overlap'));
        key('End');
        assert.equal(document.activeElement, tab(p, 'remove'));
        key('Home');
        assert.equal(document.activeElement, tab(p, 'overlap'));
        assert.equal(panel(p, 'overlap').hidden, false);
    } finally {
        p.close();
    }
});

test('the server comes from the address, else the Overview pick; an unpaired id falls back without a not-found', async () => {
    const p = await mount({ id: 'h-hops' });
    try {
        assert.equal(view(p).querySelector('[data-slot="manageselect"]').value, 'h-hops');
    } finally {
        p.close();
    }

    const storage = { get: (k) => (k === 'shoal.selected' ? 'h-hops' : null), set: () => {} };
    const q = await mount({ id: 'h-gone' }, { storage });
    try {
        assert.equal(view(q).hidden, false);
        assert.equal(view(q).querySelector('[data-slot="manageselect"]').value, 'h-hops');
        assert.match(previewCalls(q)[0].path, /^Peers\/h-hops\//);
    } finally {
        q.close();
    }
});

test('choosing another server rebuilds the open tab for it, stores the pick and announces it', async () => {
    const box = new Map();
    const storage = { get: (k) => box.get(k) ?? null, set: (k, v) => box.set(k, v) };
    const p = await mount({ id: 'h-hops', tab: 'remove' }, { storage });
    try {
        const select = view(p).querySelector('[data-slot="manageselect"]');
        assert.equal(view(p).querySelector('[data-slot="manageselectwrap"]').hidden, false);
        assert.equal(p.page.querySelector('#' + select.getAttribute('aria-labelledby')).textContent, 'Showing:');
        select.value = 'h-fern';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(200);
        assert.deepEqual(removalCalls(p).map((c) => c.path), ['Peers/h-hops/LocalRemoval', 'Peers/h-fern/LocalRemoval']);
        assert.equal(view(p).querySelectorAll('[data-removal-operations]').length, 1);
        assert.equal(box.get('shoal.selected'), 'h-fern');
        assert.equal(p.page.querySelector('[data-slot="live"]').textContent, 'Now showing Fernwood');
        tab(p, 'overlap').click();
        await p.wait(150);
        assert.match(previewCalls(p).at(-1).path, /^Peers\/h-fern\//);
    } finally {
        p.close();
    }
});

test('one server: its name as text; no servers: one line and no tabs', async () => {
    const p = await mount({}, { scenario: 'oneServer' });
    try {
        assert.equal(view(p).querySelector('[data-slot="manageselectwrap"]').hidden, true);
        assert.ok(view(p).querySelector('[data-slot="managename"]').textContent.length > 0);
        assert.equal(view(p).querySelector('[data-slot="managename"]').hidden, false);
    } finally {
        p.close();
    }

    const q = await mount({}, { scenario: 'empty' });
    try {
        assert.equal(view(q).querySelector('[data-slot="managetabs"]').hidden, true);
        assert.equal(view(q).querySelector('[data-slot="managepeer"]').hidden, true);
        assert.match(q.text(), /Pair a friend's server to see its overlap and manage local copies here\./);
        assert.equal(previewCalls(q).length + removalCalls(q).length, 0);
    } finally {
        q.close();
    }
});

// 2.10.4 Lumen F1: deep links to the overlap report get the delete section; the read-only preview stays on the Overlap tab only.
test('the overlap report mounts the delete section for deep links, without the old preview', async () => {
    const p = await mountPage({ params: { view: 'overlap', id: 'h-hops' }, now: () => epoch, localRemovalPreviewGolden: golden('local-removal-preview.get', 200), localDeleteGolden: removal(), settleMs: 220 });
    try {
        assert.equal(p.page.querySelector('[data-shoal-view="overlap"] [data-local-removal-preview]'), null);
        assert.ok(p.page.querySelector('[data-shoal-view="overlap"] [data-removal-operations]'));
    } finally {
        p.close();
    }
});

test('2.10.3 T3: the Libraries header links to Library management', async () => {
    const p = await mountPage({ params: { view: 'libraries' }, settleMs: 250 });
    try {
        const link = p.page.querySelector('[data-shoal-view="libraries"] [data-slot="managelink"]');
        assert.equal(visibleText(link), 'Library management');
        assert.equal(link.dataset.shoalTo, 'manage');
        assert.match(link.getAttribute('href'), /view=manage/);
    } finally {
        p.close();
    }
});

test('2.10.4: with the switch off the Remove tab still opens and says how to turn it on', async () => {
    const replies = removal();
    replies.state.body.enabled = false;
    const p = await mount({ id: 'h-hops', tab: 'remove' }, { localDeleteGolden: replies });
    try {
        assert.equal(panel(p, 'remove').hidden, false);
        const box = panel(p, 'remove').querySelector('[data-removal-operations]');
        assert.ok(box);
        assert.match(box.textContent, /Turn on Allow Shoal to delete local movies above\./);
        assert.ok(box.querySelector('[data-action="removal-enable"]').compareDocumentPosition(box.querySelector('.shoal-rmcands')) & window.Node.DOCUMENT_POSITION_FOLLOWING, 'the switch it names is above');
    } finally {
        p.close();
    }
});

const rm = (p) => panel(p, 'remove').querySelector('[data-removal-operations]');

test('U2: each overlapped movie is one aligned row: checkbox and title, size; ticking several is allowed', async () => {
    const replies = removal();
    const base = replies.state.body.candidates[0];
    replies.state.body.candidates = [base, { ...base, candidateId: '2'.repeat(32), title: 'Spring', year: null, bytes: 4096 }];
    const p = await mount({ id: 'h-hops', tab: 'remove' }, { localDeleteGolden: replies });
    try {
        const rows = [...rm(p).querySelectorAll('.shoal-rmlist > li.shoal-rmrow')];
        assert.equal(rows.length, 2);
        assert.equal(rm(p).querySelector('.shoal-rmlist').getAttribute('aria-label'), 'Overlapped movies');
        assert.ok(rows[0].querySelector('.shoal-rmrow__size').textContent.length > 0);
        const [first, second] = rows;
        assert.equal(first.querySelector('.shoal-check__label').textContent, 'Sintel (2010)');
        assert.equal(second.querySelector('.shoal-check__label').textContent, 'Spring');
        assert.match(first.querySelector('input[data-candidate]').getAttribute('aria-label'), /^Sintel \(2010\) \(.+ locally\)$/);
        assert.ok(rows.every((row) => row.querySelectorAll('button').length === 0), 'no per-row action');
        for (const row of rows) {
            const input = row.querySelector('input[data-candidate]');
            input.checked = true;
            input.dispatchEvent(new window.Event('change', { bubbles: true }));
        }
        assert.deepEqual(rows.map((r) => r.querySelector('input[data-candidate]').checked), [true, true]);
        assert.match(rm(p).querySelector('.shoal-rmbar').textContent, /^2 selected, /);
        assert.equal(rm(p).querySelector('[data-action="removal-select-all"]').checked, true);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally {
        p.close();
    }
});

test('2.10.4: one switch on top, above the list; no second switch, no pools, no danger zone', async () => {
    const p = await mount({ id: 'h-hops', tab: 'remove' });
    try {
        const group = rm(p).querySelector('.shoal-rmsettings');
        assert.ok(group.contains(rm(p).querySelector('[data-action="removal-enable"]')));
        assert.ok(group.compareDocumentPosition(rm(p).querySelector('.shoal-rmcands')) & window.Node.DOCUMENT_POSITION_FOLLOWING, 'switch above the list');
        assert.equal(rm(p).querySelectorAll('[data-action="purge-enable"], [role="tablist"], [data-pool], details, .shoal-rmbadge').length, 0);
        assert.doesNotMatch(rm(p).textContent, /Danger zone|Recoverable|Delete forever|Permanent deletion allowed/);
    } finally {
        p.close();
    }
});

// 2.10.4: the old list names in the address (tab=recoverable, tab=deleted) still open the Remove tab, which writes plain tab=remove back.
test('old addresses tab=recoverable and tab=deleted open the Remove tab', async () => {
    for (const name of ['recoverable', 'deleted']) {
        window.location.hash = '#/configurationpage?name=Shoal&view=manage&id=h-hops&tab=' + name;
        const p = await mount({ id: 'h-hops', tab: name });
        try {
            assert.equal(tab(p, 'remove').getAttribute('aria-selected'), 'true');
            assert.ok(rm(p));
            tab(p, 'overlap').click();
            tab(p, 'remove').click();
            assert.match(window.location.hash, /tab=remove/);
        } finally {
            p.close();
            window.location.hash = '';
        }
    }
});
