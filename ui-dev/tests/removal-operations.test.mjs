// 2.10.4 Delete from this server (docs/plans/lumen-delete-2.10.4.md, c0-delete-contract-2.10.4.md): one list, one danger button, one
// dialog, a server batch followed by polling, Stop, re-attach, and the legacy Restore list. The mock serves only the C0 golden files.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, golden, makeClock, JUNK } from './helpers.mjs';

const epoch = Date.parse('2026-10-28T12:00:00Z');
const ld = (name, status = 200) => golden('local-delete.' + name, status);
function state(edit) {
    const s = ld('state');
    s.body.activeBatchId = null;
    if (edit) edit(s.body);
    return s;
}
const replies = (extra = {}) => ({ state: state(), global: ld('off'), start: ld('start', 202), batches: [ld('running')], current: ld('current-none', 404),
    stop: ld('stopping'), restore: golden('removal-operations.restored', 200), setting: golden('removal-operations.setting-on', 200), ...extra });
const scripted = (script, extra = {}) => replies({ script, ...extra });
const mount = (extra = {}) => mountPage({ params: { view: 'overlap', id: 'h-hops' }, now: () => epoch, localDeleteGolden: replies(), features: { deletePollMs: 15 }, settleMs: 180, ...extra });
const panel = (p) => p.page.querySelector('[data-removal-operations]');
const act = (p, name) => panel(p).querySelector('[data-action="removal-' + name + '"]');
const calls = (p, re) => p.mock.calls.filter((c) => re.test(c.method + ' ' + c.path));
const resultText = (p) => panel(p).querySelector('.shoal-rmresults')?.textContent || '';
const rowFor = (p, title) => [...panel(p).querySelectorAll('.shoal-rmresult')].find((li) => li.querySelector('.shoal-rmresult__title').textContent.startsWith(title));
function tick(p, n = 1) {
    const inputs = [...panel(p).querySelectorAll('input[data-candidate]')].slice(0, n);
    for (const input of inputs) {
        input.checked = true;
        input.dispatchEvent(new window.Event('change', { bubbles: true }));
    }
}
const dialog = () => document.querySelector('[role="alertdialog"]');
const dialogButton = (label) => [...dialog().querySelectorAll('button')].find((b) => b.textContent.trim() === label);
async function confirmDelete(p) {
    act(p, 'delete').click();
    await p.wait(20);
    dialogButton('Delete').click();
    await p.wait(30);
}
async function until(p, check, ms = 1500) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
        if (check()) return;
        await p.wait(15);
    }
    assert.fail('timed out waiting');
}
const ended = (p) => () => !!panel(p).querySelector('.shoal-rmresults__sum');

test('one switch, no lists as tabs, no Recoverable or Deleted pools and no second switch', async () => {
    const p = await mount();
    try {
        const box = panel(p);
        assert.equal(box.querySelector('h2, .shoal-section__title')?.textContent, 'Delete local movies');
        assert.equal(box.querySelectorAll('input[data-action="removal-enable"]').length, 1);
        assert.match(box.textContent, /Allow Shoal to delete local movies/);
        assert.match(box.textContent, /Deletes the movie and its folder from this server's disk after Shoal confirms your friend has the identical file\./);
        assert.equal(box.querySelectorAll('[role="tablist"], [role="tab"], [data-pool]').length, 0);
        assert.doesNotMatch(box.textContent, /Recoverable|Delete forever|Move to recoverable|Danger zone|recovery folder/);
        assert.equal(box.querySelector('[data-action="purge-enable"]'), null);
    } finally { p.close(); }
});

test('switch off: the list stays, rows and the bar button are disabled and point at the hint; no writes', async () => {
    const p = await mount({ localDeleteGolden: replies({ state: state((b) => { b.enabled = false; }) }) });
    try {
        const box = panel(p);
        const hint = box.querySelector('.shoal-rmhint');
        assert.equal(hint.hidden, false);
        assert.equal(hint.textContent, 'Turn on Allow Shoal to delete local movies above.');
        const rows = box.querySelectorAll('input[data-candidate]');
        assert.equal(rows.length, 2);
        for (const input of rows) {
            assert.equal(input.disabled, true);
            assert.equal(input.getAttribute('aria-describedby'), hint.id);
        }
        assert.equal(act(p, 'delete').disabled, true);
        assert.equal(act(p, 'delete').getAttribute('aria-describedby'), hint.id);
        assert.doesNotMatch(box.textContent, /Nothing overlaps/);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally { p.close(); }
});

test('turning the switch on sends one PUT and reads the list again', async () => {
    const p = await mount({ localDeleteGolden: replies({ state: state((b) => { b.enabled = false; }) }) });
    try {
        const enable = act(p, 'enable');
        enable.checked = true;
        enable.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(60);
        assert.deepEqual(calls(p, /^PUT LocalRemoval\/Enabled$/).map((c) => c.body), [{ enabled: true }]);
        assert.equal(act(p, 'enable').checked, true);
        assert.equal(panel(p).querySelector('.shoal-rmhint').hidden, true);
        assert.equal(panel(p).querySelector('input[data-candidate]').disabled, false);
    } finally { p.close(); }
});

test('empty with the switch on says nothing overlaps', async () => {
    const p = await mount({ localDeleteGolden: replies({ state: state((b) => { b.candidates = []; }) }) });
    try {
        assert.match(panel(p).textContent, /Nothing overlaps with this friend yet\./);
    } finally { p.close(); }
});

test('the sticky bar counts the ticked movies; the dialog asks one plain question and Cancel sends nothing', async () => {
    const p = await mount();
    try {
        const bar = panel(p).querySelector('.shoal-rmbar');
        assert.equal(bar.hidden, true);
        tick(p, 2);
        assert.equal(bar.hidden, false);
        assert.equal(bar.getAttribute('role'), 'region');
        assert.equal(bar.getAttribute('aria-label'), 'Bulk actions');
        assert.match(bar.querySelector('.shoal-rmbar__count').textContent, /^2 selected, /);
        assert.equal(act(p, 'delete').textContent.trim(), 'Delete from this server');
        assert.ok(act(p, 'delete').classList.contains('shoal-btn--dangerline'));
        act(p, 'delete').click();
        await p.wait(20);
        const dlg = dialog();
        assert.ok(dlg, 'an alertdialog');
        assert.equal(document.getElementById(dlg.getAttribute('aria-labelledby')).textContent, 'Delete from this server?');
        const body = document.getElementById(dlg.getAttribute('aria-describedby')).textContent;
        assert.match(body, /^2 movies, .+\./);
        assert.match(body, /Each movie and its whole folder are deleted from this server's disk, like Jellyfin's Delete\. Your friend's copy will play instead\. This can't be undone\./);
        const labels = [...dlg.querySelectorAll('button')].map((b) => b.textContent.trim()).filter((x) => x === 'Cancel' || x === 'Delete');
        assert.deepEqual(labels, ['Cancel', 'Delete'], 'Cancel first in the DOM');
        assert.equal(document.activeElement, dialogButton('Cancel'));
        assert.equal(dlg.querySelectorAll('input').length, 0, 'no typing, no ticks');
        dialogButton('Cancel').click();
        await p.wait(30);
        assert.equal(calls(p, /^POST /).length, 0);
    } finally { p.close(); }
});

test('one movie reads "1 movie, size."', async () => {
    const p = await mount();
    try {
        tick(p, 1);
        act(p, 'delete').click();
        await p.wait(20);
        assert.match(document.getElementById(dialog().getAttribute('aria-describedby')).textContent, /^1 movie, [^,]+\./);
        dialogButton('Cancel').click();
        await p.wait(20);
    } finally { p.close(); }
});

test('Delete starts one server batch, shows Checking N of M, then the results and a fresh list', async () => {
    const p = await mount({ localDeleteGolden: scripted({}) });
    try {
        tick(p, 2);
        act(p, 'delete').click();
        await p.wait(20);
        dialogButton('Delete').click();
        await p.wait(5);
        const post = calls(p, /^POST LocalRemoval\/Deletions$/);
        assert.equal(post.length, 1);
        assert.equal(post[0].body.candidateIds.length, 2);
        assert.ok(post[0].body.candidateIds.every((x) => /^[a-f0-9]{32}$/.test(x)));
        await until(p, () => /^Checking 1 of 2: /.test(panel(p).querySelector('.shoal-rmprogress__text').textContent));
        assert.equal(panel(p).querySelector('.shoal-rmprogress').hidden, false);
        assert.equal(act(p, 'stop').textContent.trim(), 'Stop after this one');
        assert.equal(act(p, 'stop').getAttribute('title'), 'The movie in progress always finishes.');
        assert.equal(act(p, 'enable').disabled, true, 'the switch waits while a batch runs');
        assert.ok([...panel(p).querySelectorAll('input[data-candidate]')].every((i) => i.disabled));
        await until(p, () => /^Deleting /.test(panel(p).querySelector('.shoal-rmprogress__text').textContent));
        await until(p, () => /Finishing up: adding your friend's copies/.test(panel(p).querySelector('.shoal-rmprogress__text').textContent));
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmprogress').hidden, true);
        assert.equal(panel(p).querySelector('.shoal-rmresults__sum').textContent, '2 deleted');
        assert.match(rowFor(p, 'Sintel').textContent, /Deleted\s*Sintel \(2010\)\s*Playing from your friend now\. Watch history kept\./);
        assert.equal(panel(p).querySelectorAll('input[data-candidate]').length, 0, 'deleted movies left the list');
        assert.equal(panel(p).querySelector('.shoal-rmbar').hidden, true, 'done ticks clear');
        assert.equal(p.page.querySelector('[data-slot="live"]').textContent, '2 deleted');
        assert.doesNotMatch(resultText(p), JUNK);
        act(p, 'dismiss').click();
        assert.equal(panel(p).querySelector('.shoal-rmresults').hidden, true);
    } finally { p.close(); }
});

test('item results: refused, pending copy, and the removalChanged words', async () => {
    const p = await mount({ localDeleteGolden: scripted({ Sintel: { refuse: 'removalChanged' }, 'Big Buck Bunny': { pending: true } }) });
    try {
        tick(p, 2);
        await confirmDelete(p);
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmresults__sum').textContent, '1 deleted, 1 refused');
        assert.match(rowFor(p, 'Sintel').textContent, /Refused\s*Sintel \(2010\)\s*Something changed, or someone is watching it\. Nothing was deleted\./);
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Deleted\s*Big Buck Bunny\s*Your friend's copy will show up after the next sync\. Watch history wasn't carried over\./);
        assert.equal(rowFor(p, 'Big Buck Bunny').querySelector('.shoal-chip--warn, .shoal-chip--bad'), null, 'pending is not a failure');
        assert.equal(panel(p).querySelectorAll('input[data-candidate]').length, 1, 'the refused movie stays in the list');
        assert.equal(panel(p).querySelector('input[data-candidate]').checked, true, 'and stays ticked to send again');
    } finally { p.close(); }
});

test('a notConfirmed movie stops the batch; the rest are Not tried because of it', async () => {
    const p = await mount({ localDeleteGolden: scripted({ Sintel: { notConfirmed: true } }) });
    try {
        tick(p, 2);
        await confirmDelete(p);
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmresults__sum').textContent, '1 not sure, 1 not attempted');
        assert.match(rowFor(p, 'Sintel').textContent, /Not sure\s*Sintel \(2010\)\s*Shoal couldn't confirm whether this was deleted\. Check this movie in Jellyfin before trying again\./);
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Not tried\s*Big Buck Bunny\s*Stopped: couldn't confirm the previous movie\./);
        assert.equal(panel(p).querySelector('.shoal-rmresults__cause').textContent, "Stopped: couldn't confirm the previous movie.");
    } finally { p.close(); }
});

test('a friend that is not answering stops the batch and the summary names the cause once', async () => {
    const p = await mount({ localDeleteGolden: scripted({ Sintel: { refuse: 'originUnavailable' } }) });
    try {
        tick(p, 2);
        await confirmDelete(p);
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmresults__sum').textContent, '1 refused, 1 not attempted');
        assert.match(rowFor(p, 'Sintel').textContent, /Your friend's server isn't answering or can't confirm the copy\. It may be offline or need Shoal 2\.10\.4\./);
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Stopped: your friend's server isn't answering/);
        assert.match(panel(p).querySelector('.shoal-rmresults__cause').textContent, /^Stopped: your friend's server isn't answering/);
        assert.doesNotMatch(resultText(p), /originUnavailable/, 'never the code');
    } finally { p.close(); }
});

test('Stop after this one: one POST, the button waits, the rest are Not tried', async () => {
    const p = await mount({ localDeleteGolden: scripted({ Sintel: { slow: 4 } }) });
    try {
        tick(p, 2);
        await confirmDelete(p);
        await until(p, () => !!act(p, 'stop') && !act(p, 'stop').disabled && !panel(p).querySelector('.shoal-rmprogress').hidden);
        act(p, 'stop').click();
        await p.wait(5);
        assert.equal(act(p, 'stop').disabled, true);
        assert.equal(act(p, 'stop').textContent.trim(), 'Stopping after this one...');
        await until(p, ended(p));
        assert.equal(calls(p, /^POST LocalRemoval\/Deletions\/[0-9a-f]{32}\/Stop$/).length, 1);
        assert.match(rowFor(p, 'Sintel').textContent, /Deleted/, 'the movie in progress finished');
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Not tried\s*Big Buck Bunny\s*Stopped: you asked to stop\./);
    } finally { p.close(); }
});

test('a checking movie held by a busy friend shows Waiting for the friend server, then Checking, then the results', async () => {
    const p = await mount({ localDeleteGolden: scripted({ 'Big Buck Bunny': { rateLimited: 6, slow: 6 } }) });
    try {
        tick(p, 1);
        await confirmDelete(p);
        const text = () => panel(p).querySelector('.shoal-rmprogress__text').textContent;
        await until(p, () => text() === "Waiting for your friend's server: Big Buck Bunny");
        assert.doesNotMatch(text(), /Checking/);
        assert.equal(panel(p).querySelector('.shoal-rmprogress__text').getAttribute('aria-live'), null, 'still not a live region, like the other progress states');
        await until(p, () => /^Checking 1 of 1: Big Buck Bunny$/.test(text()));
        await until(p, ended(p));
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Deleted/);
    } finally { p.close(); }
});

test('a batch that stops because the friend is busy says so in the summary and on the row', async () => {
    const p = await mount({ localDeleteGolden: scripted({ Sintel: { refuse: 'originRateLimited' } }) });
    try {
        tick(p, 2);
        await confirmDelete(p);
        await until(p, ended(p));
        assert.equal(panel(p).querySelector('.shoal-rmresults__cause').textContent, "Stopped: your friend's server is busy. Try again in a minute.");
        assert.match(rowFor(p, 'Sintel').textContent, /Your friend's server is busy\. Try again in a minute\./);
        assert.doesNotMatch(resultText(p), /originRateLimited/);
    } finally { p.close(); }
});

test('a long Checking says it can take a while after 2 minutes on one movie', async () => {
    const clock = makeClock(epoch);
    const p = await mount({ now: clock.now, localDeleteGolden: scripted({ 'Big Buck Bunny': { slow: 200 } }), features: { deletePollMs: 15 } });
    try {
        tick(p, 1);
        await confirmDelete(p);
        await until(p, () => /^Checking 1 of 1: Big Buck Bunny$/.test(panel(p).querySelector('.shoal-rmprogress__text').textContent));
        clock.advance(121000);
        await until(p, () => /\(large file, this can take a while\)$/.test(panel(p).querySelector('.shoal-rmprogress__text').textContent));
    } finally { p.close(); }
});

test('page load re-attaches to a running batch through Current', async () => {
    const p = await mount({ localDeleteGolden: replies({ state: state((b) => { b.activeBatchId = 'f'.repeat(1) + 'f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f'; }), current: ld('running') }) });
    try {
        assert.equal(calls(p, /^GET LocalRemoval\/Deletions\/Current$/).length, 1);
        assert.equal(panel(p).querySelector('.shoal-rmprogress__text').textContent, 'Checking 3 of 4: Tears of Steel (2010)');
        assert.match(panel(p).textContent, /Picked up where you left off\./);
        assert.match(rowFor(p, 'Tears of Steel').textContent, /Working\.\.\./);
        assert.match(rowFor(p, 'Elephants Dream').textContent, /Waiting/);
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Your friend's file isn't identical, so nothing was deleted\./);
        assert.equal(act(p, 'enable').disabled, true);
        await p.wait(60);
        assert.ok(calls(p, /^GET LocalRemoval\/Deletions\/f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6$/).length >= 1, 'then it polls the batch');
    } finally { p.close(); }
});

test('settling and stopping goldens read as Finishing up and Stopping', async () => {
    const p = await mount({ localDeleteGolden: replies({ current: ld('settling'), batches: [ld('settling')] }) });
    try {
        assert.equal(panel(p).querySelector('.shoal-rmprogress__text').textContent, "Finishing up: adding your friend's copies");
        assert.equal(act(p, 'stop').disabled, true);
    } finally { p.close(); }
    const q = await mount({ localDeleteGolden: replies({ current: ld('stopping'), batches: [ld('stopping')] }) });
    try {
        assert.equal(q.page.querySelector('[data-removal-operations] .shoal-rmprogress__text').textContent, 'Deleting 1 of 2: Sintel (2010)');
        assert.equal(act(q, 'stop').textContent.trim(), 'Stopping after this one...');
        assert.equal(act(q, 'stop').disabled, true);
    } finally { q.close(); }
});

test('a finished batch from Current shows every end state once; Dismiss is remembered', async () => {
    const saved = new Map();
    const storage = { get: (k) => saved.get(k) ?? null, set: (k, v) => saved.set(k, v) };
    const p = await mount({ storage, localDeleteGolden: replies({ current: ld('finished') }) });
    try {
        assert.equal(panel(p).querySelector('.shoal-rmprogress').hidden, true);
        assert.equal(panel(p).querySelector('.shoal-rmresults__sum').textContent, '2 deleted, 1 refused, 1 not sure, 1 not attempted');
        assert.match(rowFor(p, 'Sintel').textContent, /Playing from your friend now\. Watch history kept\./);
        assert.match(rowFor(p, 'Big Buck Bunny').textContent, /Your friend's copy will show up after the next sync\. Watch history wasn't carried over\./);
        assert.match(rowFor(p, 'Tears of Steel').textContent, /This movie shares its folder with other media, so Shoal won't delete it\. Delete it in Jellyfin\./);
        assert.match(rowFor(p, 'Elephants Dream').textContent, /Not sure/);
        assert.match(rowFor(p, 'Cosmos Laundromat').textContent, /Stopped: couldn't confirm the previous movie\./);
        for (const li of panel(p).querySelectorAll('.shoal-rmresult')) assert.ok(li.querySelector('.shoal-chip svg, .shoal-chip use') || li.querySelector('.shoal-chip'), 'icon and word, never colour alone');
        assert.equal(calls(p, /^GET LocalRemoval\/Deletions\/[0-9a-f]{32}$/).length, 0, 'an ended batch is not polled');
        act(p, 'dismiss').click();
    } finally { p.close(); }
    const q = await mount({ storage, localDeleteGolden: replies({ current: ld('finished') }) });
    try {
        assert.equal(q.page.querySelector('[data-removal-operations] .shoal-rmresults').hidden, true);
    } finally { q.close(); }
});

test('a start refused by the server deletes nothing and says why in words', async () => {
    const p = await mount({ refuse: [{ re: /^POST LocalRemoval\/Deletions$/, status: 409, error: 'busy' }] });
    try {
        tick(p, 1);
        await confirmDelete(p);
        await p.wait(40);
        assert.match(panel(p).textContent, /Nothing was deleted: another Shoal job is running\. Try again soon\./);
        assert.equal(panel(p).querySelector('.shoal-rmprogress').hidden, true);
    } finally { p.close(); }
});

test('a batch the server lost (restart) stops the polling and says check by hand', async () => {
    const p = await mount({ localDeleteGolden: replies({ current: ld('running'), batches: [ld('current-none', 404)] }) });
    try {
        await p.wait(80);
        assert.match(panel(p).textContent, /The server no longer knows this run/);
        assert.equal(panel(p).querySelector('.shoal-rmprogress').hidden, true);
        const polls = calls(p, /^GET LocalRemoval\/Deletions\/[0-9a-f]{32}$/).length;
        await p.wait(60);
        assert.equal(calls(p, /^GET LocalRemoval\/Deletions\/[0-9a-f]{32}$/).length, polls);
    } finally { p.close(); }
});

test('every reason word has plain copy; an unknown word never shows its code', async () => {
    const { STRINGS } = await import('./helpers.mjs').then((h) => h.loadShoal());
    for (const word of ['candidateChanged', 'contentNotEquivalent', 'historyChanged', 'accessNotShared', 'removalChanged', 'folderNotDedicated', 'deleteNotPermitted', 'deleteFailed', 'unsupportedItem', 'notConfirmed']) {
        assert.ok(STRINGS['delReason_' + word], word);
    }
    for (const word of ['originUnavailable', 'originRateLimited', 'busy', 'disabled', 'administratorRequired', 'libraryBusy']) assert.ok(STRINGS['delStop_' + word], word);
    assert.match(STRINGS.delReason_accessNotShared, /^A person on this server can't see your friend's copy.*Share it with them first\.$/);
    assert.match(STRINGS.delReason_removalChanged, /Something changed, or someone is watching it/);
    const finished = ld('finished');
    finished.body.items[2].reason = 'somethingNew';
    const p = await mount({ localDeleteGolden: replies({ current: finished }) });
    try {
        assert.match(rowFor(p, 'Tears of Steel').textContent, /Something went wrong\. Nothing was deleted\./);
        assert.doesNotMatch(panel(p).textContent, /somethingNew/);
    } finally { p.close(); }
});

test('held by an earlier Shoal version: Restore only, one at a time, a read-only attention row, hidden when empty', async () => {
    const p = await mount();
    try {
        const legacy = panel(p).querySelector('[data-removal-legacy]');
        assert.equal(legacy.hidden, false);
        assert.match(legacy.textContent, /^Held by an earlier Shoal version\s*Restore puts the file back in your library\. Then you can delete it like any other movie\./);
        assert.equal(legacy.querySelectorAll('input').length, 0, 'no checkboxes, no bulk');
        const restore = legacy.querySelectorAll('[data-action="removal-restore"]');
        assert.equal(restore.length, 1);
        assert.equal(restore[0].textContent.trim(), 'Restore');
        assert.match(legacy.textContent, /Elephants Dream\s*Needs attention\. Open Shoal's log or ask for help\./);
        restore[0].click();
        await p.wait(60);
        assert.deepEqual(calls(p, /^POST /).map((c) => c.path), ['LocalRemoval/Jobs/c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3/Restore']);
        assert.match(panel(p).textContent, /Restored\. It is back in your library\./);
        assert.equal(panel(p).querySelectorAll('[data-removal-legacy] [data-action="removal-restore"]').length, 0);
    } finally { p.close(); }
    const q = await mount({ localDeleteGolden: replies({ state: state((b) => { b.legacyJobs = []; }) }) });
    try {
        assert.equal(q.page.querySelector('[data-removal-legacy]').hidden, true);
    } finally { q.close(); }
});

test('Restore works with the switch off', async () => {
    const p = await mount({ localDeleteGolden: replies({ state: state((b) => { b.enabled = false; }) }) });
    try {
        const restore = panel(p).querySelector('[data-removal-legacy] [data-action="removal-restore"]');
        assert.equal(restore.disabled, false);
    } finally { p.close(); }
});

test('Settings shows the switch and the held list from Status, with no movie list', async () => {
    const off = ld('off');
    off.body.legacyJobs = ld('state').body.legacyJobs;
    const p = await mountPage({ params: { view: 'settings' }, now: () => epoch, localDeleteGolden: replies({ global: off }), features: { deletePollMs: 15 }, settleMs: 220 });
    try {
        const box = p.page.querySelector('[data-removal-operations]');
        assert.ok(box);
        assert.ok(p.mock.calls.some((c) => c.path === 'LocalRemoval/Status'));
        assert.equal(box.querySelectorAll('input[data-candidate]').length, 0);
        assert.equal(box.querySelector('[data-action="removal-enable"]').checked, false);
        assert.equal(box.querySelectorAll('[data-removal-legacy] [data-action="removal-restore"]').length, 1);
        assert.equal(box.querySelector('[data-action="removal-delete"]').disabled, true);
    } finally { p.close(); }
});

test('an older server without the routes shows that the feature is unavailable and offers no delete', async () => {
    const p = await mount({ localDeleteGolden: null });
    try {
        assert.match(panel(p).textContent, /This server does not offer Delete from this server yet\./);
        assert.equal(panel(p).querySelectorAll('input[data-candidate]').length, 0);
        assert.equal(act(p, 'enable').disabled, true);
    } finally { p.close(); }
});
