// 2.10.3 fixes from docs/reviews/lumen-review-ui-rework-2.10.3.md (federation repo): F2, F3, F6, F8, F10. F1 and F4 live in local-removal-preview.test.mjs.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, golden, visibleText } from './helpers.mjs';

const epoch = Date.parse('2026-10-25T12:00:00Z');
// 2.10.4: the Remove tab is Delete from this server; the mock serves the C0 goldens (local-delete.*).
const fixture = (name) => golden('local-delete.' + name, 200);
function replies(state = fixture('state')) {
    state.body.activeBatchId = null;
    return { state, global: fixture('off'), start: golden('local-delete.start', 202), current: golden('local-delete.current-none', 404),
        restore: golden('removal-operations.restored', 200), setting: golden('removal-operations.setting-on', 200) };
}
function threeCopies() {
    const list = fixture('state');
    const base = list.body.candidates[0];
    list.body.candidates = [
        { ...base, title: 'Sintel', year: 2010, bytes: 3000 },
        { ...base, candidateId: '2'.repeat(32), title: 'Big Buck Bunny', year: 2008, bytes: 9000 },
        { ...base, candidateId: '3'.repeat(32), title: 'Tears of Steel', year: null, bytes: 1000 }
    ];
    return list;
}
const mount = (params, extra = {}) => mountPage({ params: Object.assign({ view: 'manage', id: 'h-hops', tab: 'remove' }, params), now: () => epoch, localDeleteGolden: replies(threeCopies()), settleMs: 220, ...extra });
const rm = (p) => p.page.querySelector('[data-removal-operations]');
const titles = (p) => [...rm(p).querySelectorAll('.shoal-rmrow')].filter((li) => !li.hidden).map((li) => li.querySelector('.shoal-check__label').textContent);

// 2.10.3 U2 retired F2: there is no per-row Check any more (no hint, no disabled buttons). The rows carry only a checkbox.
test('U2: rows have no Check buttons and no hint; the filter row and select-all stay usable', async () => {
    const p = await mount({});
    try {
        assert.equal(rm(p).querySelectorAll('[data-action="removal-check"]').length, 0);
        assert.equal(rm(p).querySelector('.shoal-rmhint').hidden, true, 'the off hint shows only with the switch off');
        assert.ok([...rm(p).querySelectorAll('.shoal-rmrow')].every((li) => li.querySelectorAll('button').length === 0));
        assert.doesNotMatch(rm(p).textContent, /Tick a title to check it|Check this copy/);
    } finally {
        p.close();
    }
});

test('F3: the Remove tab has a summary line, the Filter titles and Sort controls, and they only hide or reorder rows', async () => {
    const p = await mount({});
    try {
        assert.match(rm(p).querySelector('.shoal-rmsummary').textContent, /^3 movies, .+$/);
        // Size, largest first.
        assert.deepEqual(titles(p), ['Big Buck Bunny (2008)', 'Sintel (2010)', 'Tears of Steel']);
        const input = rm(p).querySelector('input[data-removal-filter]');
        const sort = rm(p).querySelector('select[data-removal-filter]');
        assert.equal(rm(p).querySelector('label[for="' + input.id + '"]').textContent, 'Filter titles');
        input.value = 'el';
        input.dispatchEvent(new window.Event('input', { bubbles: true }));
        assert.deepEqual(titles(p), ['Sintel (2010)', 'Tears of Steel']);
        assert.equal(p.page.querySelector('[data-slot="live"]').textContent, '2 movies match.');
        sort.value = 'title';
        sort.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.deepEqual(titles(p), ['Sintel (2010)', 'Tears of Steel']);
        sort.value = 'smallest';
        sort.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.deepEqual(titles(p), ['Tears of Steel', 'Sintel (2010)']);
        // Ticking a hidden-by-filter row is impossible; ticking a shown one leaves the filter controls usable.
        const row = [...rm(p).querySelectorAll('.shoal-rmrow')].find((li) => !li.hidden);
        const box = row.querySelector('input[data-candidate]');
        box.checked = true;
        box.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.match(rm(p).querySelector('.shoal-rmbar').textContent, /^1 selected, /);
        assert.equal(input.disabled, false);
        assert.equal(sort.disabled, false);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally {
        p.close();
    }
});

test('F3: with removal off the filter stays usable ; one copy shows no filter', async () => {
    const off = fixture('state');
    off.body.enabled = false;
    off.body.candidates = off.body.candidates.slice(0, 1);
    const p = await mount({}, { localDeleteGolden: replies(off) });
    try {
        assert.equal(rm(p).querySelector('.shoal-rmsummary').textContent.startsWith('1 movie, '), true);
        assert.equal(rm(p).querySelector('.shoal-rmtop .shoal-lrtools').hidden, true);
    } finally {
        p.close();
    }
});

test('F6: Delete from this server has the danger outline, Restore does not', async () => {
    const p = await mount({});
    try {
        const review = rm(p).querySelector('[data-action="removal-delete"]');
        const restore = rm(p).querySelector('[data-action="removal-restore"]');
        assert.ok(review.classList.contains('shoal-btn--dangerline'));
        assert.equal(restore.classList.contains('shoal-btn--dangerline'), false);
    } finally {
        p.close();
    }
});

test('F8: Overview shows the Watching tag once, by the Showing select, and the bar is a named group', async () => {
    const p = await mountPage({ params: { view: 'overview' }, settleMs: 300 });
    try {
        const bar = p.page.querySelector('[data-section="peerbar"]');
        assert.equal(bar.getAttribute('role'), 'group');
        assert.equal(bar.getAttribute('aria-label'), 'Server shown');
        assert.ok(bar.querySelector('.shoal-tag--in'));
        assert.equal(p.page.querySelector('#' + p.page.querySelector('[data-section="overlap"]').getAttribute('aria-labelledby')).querySelector('.shoal-tag'), null);
        assert.equal(visibleText(p.page.querySelector('[data-slot="overlaptitle"]').parentElement), 'Library overlap with Fernwood');
    } finally {
        p.close();
    }
});

test('F10: changing tab or server writes tab and id into the address without a new history entry', async () => {
    window.location.hash = '#/configurationpage?name=Shoal&view=manage&id=h-hops';
    const p = await mount({ tab: undefined });
    try {
        const before = window.history.length;
        p.page.querySelector('[role="tab"][data-tab="remove"]').click();
        assert.match(window.location.hash, /view=manage/);
        assert.match(window.location.hash, /id=h-hops/);
        assert.match(window.location.hash, /tab=remove/);
        const select = p.page.querySelector('[data-slot="manageselect"]');
        select.value = 'h-fern';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.match(window.location.hash, /id=h-fern/);
        assert.match(window.location.hash, /tab=remove/);
        assert.equal(window.history.length, before);
    } finally {
        p.close();
        window.location.hash = '';
    }
});
