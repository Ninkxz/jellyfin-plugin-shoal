// 2.10.4 fixes from docs/reviews/lumen-review-delete-2.10.4.md (federation repo): F1 to F7. F8 (the long Checking hint) is a lab check;
// its unit test lives in removal-operations.test.mjs. The mock serves only the C0 golden files.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, golden } from './helpers.mjs';

const epoch = Date.parse('2026-10-28T12:00:00Z');
const ld = (name, status = 200) => golden('local-delete.' + name, status);
function replies(extra = {}, edit) {
    const state = ld('state');
    state.body.activeBatchId = null;
    if (edit) edit(state.body);
    return { state, global: ld('off'), start: ld('start', 202), batches: [ld('running')], current: ld('current-none', 404),
        restore: golden('removal-operations.restored', 200), setting: golden('removal-operations.setting-on', 200), ...extra };
}
const mount = (extra = {}) => mountPage({ params: { view: 'overlap', id: 'h-hops' }, now: () => epoch, localDeleteGolden: replies(), localRemovalPreviewGolden: golden('local-removal-preview.get', 200),
    features: { deletePollMs: 15 }, settleMs: 180, ...extra });
const panel = (p) => p.page.querySelector('[data-removal-operations]');
const act = (p, name) => panel(p).querySelector('[data-action="removal-' + name + '"]');
const row = (p, title) => [...panel(p).querySelectorAll('.shoal-rmrow')].find((li) => li.dataset.title === title);
const box = (p, title) => row(p, title).querySelector('input[data-candidate]');
function tickAll(p) {
    for (const input of panel(p).querySelectorAll('input[data-candidate]')) {
        if (input.disabled) continue;
        input.checked = true;
        input.dispatchEvent(new window.Event('change', { bubbles: true }));
    }
}
const dialog = () => document.querySelector('[role="alertdialog"]');
const dialogButton = (label) => [...dialog().querySelectorAll('button')].find((b) => b.textContent.trim() === label);
async function until(p, check, ms = 1500) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
        if (check()) return;
        await p.wait(15);
    }
    assert.fail('timed out waiting');
}
const ended = (p) => () => !!panel(p).querySelector('.shoal-rmresults__sum');
async function runDelete(p) {
    act(p, 'delete').click();
    await p.wait(20);
    dialogButton('Delete').click();
}
/** Every text the page's live region speaks, in order (ctx.announce writes it and clears it later). */
function listen(p) {
    const said = [];
    const live = p.page.querySelector('[data-slot="live"]');
    new window.MutationObserver(() => { const text = live.textContent.trim(); if (text && said[said.length - 1] !== text) said.push(text); })
        .observe(live, { childList: true, characterData: true, subtree: true });
    return said;
}

test('F1: the overlap report shows the delete section only; the old read-only preview block is gone from it', async () => {
    const p = await mount();
    try {
        const v = p.page.querySelector('[data-shoal-view="overlap"]');
        assert.ok(v.querySelector('[data-removal-operations]'));
        assert.equal(v.querySelector('[data-local-removal-preview]'), null);
        assert.doesNotMatch(v.textContent, /Local copy removal preview|Space reclaimed by this preview|not copies that are safe to remove/);
        assert.equal(p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview')).length, 0, 'nothing loads it');
    } finally { p.close(); }
});

test('F2: a Not sure movie is unticked and locked with a note; it is never sent again from the list', async () => {
    const p = await mount({ localDeleteGolden: replies({ script: { Sintel: { notConfirmed: true } } }) });
    try {
        tickAll(p);
        // Size order: Big Buck Bunny first, then Sintel; make Sintel run first so Big Buck Bunny is Not tried.
        const sort = panel(p).querySelector('select[data-removal-filter]');
        sort.value = 'smallest';
        sort.dispatchEvent(new window.Event('change', { bubbles: true }));
        await runDelete(p);
        await until(p, ended(p));
        assert.match(panel(p).querySelector('.shoal-rmresults__sum').textContent, /1 not sure/);
        assert.ok(row(p, 'Sintel'), 'the server still lists it');
        assert.equal(box(p, 'Sintel').checked, false, 'unticked');
        assert.equal(box(p, 'Sintel').disabled, true, 'locked');
        assert.match(row(p, 'Sintel').textContent, /Check this movie in Jellyfin first\./);
        assert.equal(box(p, 'Big Buck Bunny').checked, true, 'Not tried stays ticked');
        assert.equal(act(p, 'delete').disabled, false);
        // Select the shown never ticks it again.
        const all = panel(p).querySelector('[data-action="removal-select-all"]');
        all.checked = false;
        all.dispatchEvent(new window.Event('change', { bubbles: true }));
        all.checked = true;
        all.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.equal(box(p, 'Sintel').checked, false);
        act(p, 'delete').click();
        await p.wait(20);
        assert.match(document.getElementById(dialog().getAttribute('aria-describedby')).textContent, /^1 movie, /);
        dialogButton('Delete').click();
        await p.wait(30);
        const posts = p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'LocalRemoval/Deletions');
        assert.equal(posts.length, 2);
        assert.equal(posts[0].body.candidateIds.length, 2, 'the first run sent both');
        assert.equal(posts[1].body.candidateIds.length, 1, 'the retry sends only the Not tried movie');
        await until(p, ended(p));
    } finally { p.close(); }
});

test('F2: with only the Not sure movie ticked, Delete is off', async () => {
    const p = await mount({ localDeleteGolden: replies({ script: { Sintel: { notConfirmed: true } } }) });
    try {
        box(p, 'Sintel').checked = true;
        box(p, 'Sintel').dispatchEvent(new window.Event('change', { bubbles: true }));
        await runDelete(p);
        await until(p, ended(p));
        assert.equal(box(p, 'Sintel').checked, false);
        assert.equal(act(p, 'delete').disabled, true);
        assert.equal(panel(p).querySelector('.shoal-rmbar').hidden, true);
    } finally { p.close(); }
});

test('F3: the share line is true for everyone: no claim that the library is on a network share', async () => {
    const p = await mount();
    try {
        assert.doesNotMatch(panel(p).textContent, /on a network share/);
        assert.match(panel(p).querySelector('.shoal-rmshare').textContent, /^Big movies can take several minutes to check\. Both servers need Shoal 2\.10\.4\.$/);
    } finally { p.close(); }
});

test('F5: while a batch runs one line says it keeps going, and the list is marked locked; both clear at the end', async () => {
    const p = await mount({ localDeleteGolden: replies({ script: { Sintel: { slow: 6 } } }) });
    try {
        tickAll(p);
        const lock = panel(p).querySelector('.shoal-rmlock');
        assert.equal(lock.hidden, true);
        await runDelete(p);
        await until(p, () => !lock.hidden);
        assert.equal(lock.textContent, 'Deleting... you can leave this page, it keeps going.');
        assert.ok(panel(p).querySelector('.shoal-rmcands').classList.contains('is-locked'));
        await until(p, ended(p));
        assert.equal(lock.hidden, true);
        assert.equal(panel(p).querySelector('.shoal-rmcands').classList.contains('is-locked'), false);
    } finally { p.close(); }
});

test('F6: the switch-off hint sits right under the switch help, above Refresh, and names the controls it explains', async () => {
    const p = await mount({ localDeleteGolden: replies({}, (b) => { b.enabled = false; }) });
    try {
        const hint = panel(p).querySelector('.shoal-rmhint');
        const settings = panel(p).querySelector('.shoal-rmsettings');
        assert.ok(settings.contains(hint));
        assert.equal(hint.previousElementSibling, settings.querySelector('.shoal-check'), 'directly under the switch and its help line');
        assert.ok(hint.compareDocumentPosition(act(p, 'refresh')) & window.Node.DOCUMENT_POSITION_FOLLOWING);
        assert.equal(act(p, 'delete').getAttribute('aria-describedby'), hint.id);
        assert.equal(box(p, 'Sintel').getAttribute('aria-describedby'), hint.id);
    } finally { p.close(); }
});

test('F7: the progress line is not a live region; start, each finished movie and the summary are spoken once; the count is quiet while locked', async () => {
    const p = await mount({ localDeleteGolden: replies({ script: { Sintel: { slow: 3 } } }) });
    try {
        const said = listen(p);
        tickAll(p);
        const text = panel(p).querySelector('.shoal-rmprogress__text');
        assert.equal(text.getAttribute('aria-live'), null);
        assert.equal(text.getAttribute('role'), null);
        await runDelete(p);
        await until(p, () => panel(p).querySelector('.shoal-rmbar__count').getAttribute('aria-live') === 'off');
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmbar__count').getAttribute('aria-live'), 'polite');
        const progress = said.filter((s) => /^(Checking|Deleting) \d+ of /.test(s));
        assert.deepEqual(progress, [], 'no poll text is spoken: ' + JSON.stringify(said));
        assert.match(said[0], /^Deleting 2 movies\./);
        assert.ok(said.includes('Deleted: Big Buck Bunny'), JSON.stringify(said));
        assert.ok(said.includes('Deleted: Sintel (2010)'), JSON.stringify(said));
        assert.equal(said[said.length - 1], '2 deleted');
    } finally { p.close(); }
});

test('F4: the switch, row and select-all tap areas are at least 44px tall by rule', async () => {
    const { css } = await import('./helpers.mjs');
    for (const sel of ['.shoalPage .shoal-rmrow > .shoal-check', '.shoalPage .shoal-rmall > .shoal-check', '.shoalPage .shoal-rmsettings .shoal-check']) {
        const rule = css.replace(/\/\*[\s\S]*?\*\//g, '').split('}').find((r) => r.split('{')[0].split(',').map((x) => x.trim()).includes(sel) && /min-height: (4[4-9]|[5-9]\d)px/.test(r));
        assert.ok(rule, sel);
    }
});
