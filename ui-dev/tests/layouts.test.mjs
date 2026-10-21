// U-M1 / U-M2 (PLAN v2.9): the Library layouts screen (docs/design/ui-v2/layouts.html) against the mock of AdminLayoutsController.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, JUNK } from './helpers.mjs';
import { XSS } from '../mock/mock-server.js';

const MOVE_WARN = "Changing this moves this friend's titles now. It takes a few minutes. Titles may disappear for a moment and come back. Watched marks are kept for most titles.";
const PLACE_WARN = 'Everyone who can open that library will see these titles unless you hide them for a person below.';
const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;

async function layouts(extra) {
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'layouts' }, settleMs: 350 }, extra || {}));
}

const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));
const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const byLabel = (root, label) => root.querySelector('[aria-label="' + label + '"]');
const dialog = (p) => p.page.querySelector('dialog[open]');
const row = (p, name) => [...p.page.querySelectorAll('.shoal-lib')].find((r) => r.querySelector('.shoal-lib__title')?.textContent === name);
const preset = (p, name) => [...p.page.querySelectorAll('.shoal-preset')].find((c) => c.querySelector('h3 > span').textContent === name);
const orderNames = (ol) => [...ol.querySelectorAll('.shoal-order__name > span')].map((s) => s.textContent);
const live = (p) => p.page.querySelector('[data-slot="live"]').textContent;

function choose(select, value) {
    select.value = value;
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
}

test('the screen has the h1, the back link to Libraries & access, the lead and the rule note', async () => {
    const p = await layouts();
    try {
        const view = p.page.querySelector('[data-shoal-view="layouts"]');
        assert.equal(view.hidden, false);
        assert.equal(view.querySelector('h1').textContent, 'Library layouts');
        const back = p.page.querySelector('[data-slot="backbar"]');
        assert.equal(back.hidden, false);
        assert.equal(back.textContent.trim(), 'Libraries & access');
        assert.match(back.querySelector('a').getAttribute('href'), /view=libraries/);
        const rule = p.page.querySelector('.shoal-rule');
        assert.equal(rule.getAttribute('role'), 'note');
        assert.match(rule.textContent, /Each friend feeds one library per type\. One library for their movies and one for their TV shows\. A library can hold several friends\./);
        assert.doesNotMatch(p.text(), JUNK);
    } finally {
        p.close();
    }
});

test('the page talks only to the elevated admin routes it needs', async () => {
    const p = await layouts();
    try {
        const paths = p.mock.calls.map((c) => c.method + ' ' + c.path.split('?')[0]);
        for (const x of paths) {
            assert.match(x, /^GET (Layouts\/Libraries|Libraries|Status)$/, x);
        }
    } finally {
        p.close();
    }
});

test('a running move shows a progress card with role=progressbar, its numbers, and the owner\'s words', async () => {
    const p = await layouts({ layoutJob: 'running', layoutJobCurrent: true, layoutJobStep: 0 });
    try {
        const card = p.page.querySelector('.shoal-job');
        assert.ok(card, 'job card');
        const title = card.querySelector('.shoal-job__title');
        assert.equal(title.textContent, "Moving Granite's movies into Movies");
        const bar = card.querySelector('[role="progressbar"]');
        assert.equal(bar.getAttribute('aria-valuemin'), '0');
        assert.equal(bar.getAttribute('aria-valuemax'), '612');
        assert.equal(bar.getAttribute('aria-valuenow'), '380');
        assert.equal(bar.getAttribute('aria-valuetext'), '380 of 612 titles');
        assert.equal(bar.getAttribute('aria-labelledby'), title.id);
        assert.match(bar.querySelector('.shoal-bar__fill').getAttribute('style'), /^--shoal-progress: 62\.1%;?$/);
        assert.match(card.textContent, /Titles may disappear for a moment and come back\./);
        // While it runs, Apply and Move wait for it.
        buttonIn(preset(p, 'One per friend'), 'Preview').click();
        await p.wait(80);
        const apply = buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout');
        assert.equal(apply.getAttribute('aria-disabled'), 'true');
        assert.equal(p.page.querySelector('#' + apply.getAttribute('aria-describedby')).textContent, 'Available when the current move finishes.');
    } finally {
        p.close();
    }
});

test('the job is polled while it runs; when it ends the card goes, the page reloads and says it is done', async () => {
    const p = await layouts({ layoutJob: 'running', layoutJobStep: 116 });
    try {
        p.handle.ctx.shown = true;
        Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
        assert.ok(p.page.querySelector('.shoal-job'));
        await p.wait(4600);
        assert.equal(p.page.querySelector('.shoal-job'), null, 'card gone');
        assert.ok(calls(p, 'GET', /^Layouts\/Job$/).length >= 2, 'polled');
        assert.ok(calls(p, 'GET', /^Layouts\/Libraries$/).length >= 2, 'reloaded');
        assert.match(p.text(), /Move finished\. Your libraries are up to date\./);
    } finally {
        p.close();
    }
});

test('a move that stopped half way says Shoal finishes it after a restart, and Apply waits', async () => {
    const p = await layouts({ layoutJob: 'stopped' });
    try {
        assert.equal(p.page.querySelector('.shoal-job'), null);
        assert.match(p.text(), /The last move stopped before it finished\. Shoal picks it up again when Jellyfin restarts\./);
        buttonIn(preset(p, 'All mixed'), 'Preview').click();
        await p.wait(80);
        assert.equal(buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout').getAttribute('aria-disabled'), 'true');
    } finally {
        p.close();
    }
});

test('four preset cards in the owner order (Custom last); All mixed is Recommended; the current one is marked; Custom has no Preview button', async () => {
    const p = await layouts();
    try {
        const cards = [...p.page.querySelectorAll('.shoal-preset')];
        assert.deepEqual(cards.map((c) => c.querySelector('h3 > span').textContent), ['All mixed', 'One per friend', 'Combine into my libraries', 'Custom']);
        assert.equal(preset(p, 'All mixed').querySelector('.shoal-rec-tag').textContent, 'Recommended');
        assert.equal(p.page.querySelectorAll('.shoal-rec-tag').length, 1);
        assert.match(preset(p, 'Custom').textContent, /Current/);
        assert.doesNotMatch(preset(p, 'All mixed').textContent, /Current/);
        assert.equal(buttonIn(preset(p, 'Custom'), 'Preview'), undefined);
        for (const name of ['All mixed', 'One per friend', 'Combine into my libraries']) {
            const b = buttonIn(preset(p, name), 'Preview');
            assert.equal(b.getAttribute('aria-pressed'), 'false');
            assert.equal(b.getAttribute('aria-label'), 'Preview ' + name);
            assert.equal(cards.find((c) => c === preset(p, name)).getAttribute('aria-labelledby'), preset(p, name).querySelector('h3').id);
        }

        // The pictures are decoration: hidden from screen readers, images only from this server.
        for (const pic of p.page.querySelectorAll('.shoal-choice__pic')) {
            assert.equal(pic.getAttribute('aria-hidden'), 'true');
        }
    } finally {
        p.close();
    }
});

test('Preview shows what moves, who loses access and the warning; nothing is written until Apply', async () => {
    const p = await layouts();
    try {
        const b = buttonIn(preset(p, 'One per friend'), 'Preview');
        b.click();
        await p.wait(80);
        assert.equal(calls(p, 'POST', /^Layouts\/Preview$/).at(-1).body.preset, 'PerFriend');
        assert.equal(buttonIn(preset(p, 'One per friend'), 'Preview').getAttribute('aria-pressed'), 'true');
        assert.ok(preset(p, 'One per friend').classList.contains('is-previewing'));
        assert.match(preset(p, 'One per friend').textContent, /Previewing below/);
        const pv = p.page.querySelector('.shoal-preview');
        assert.equal(pv.querySelector('h3').textContent, 'Preview: One per friend');
        assert.equal(document.activeElement, pv.querySelector('h3'), 'focus moves to the preview');
        const rows = [...pv.querySelectorAll('.shoal-diff li')].map((li) => li.textContent.replace(/\s+/g, ' ').trim());
        assert.deepEqual(rows, [
            'Shoal Movies (Fernwood)becomes Fernwood Movies',
            'Shoal TV Shows (Fernwood, Granite)becomes Fernwood TV Shows, Granite TV Shows',
            'Added into Movies (Granite)becomes Granite Movies',
            'Hops Movies, Hops TV ShowsNo change'
        ]);
        assert.equal(pv.querySelector('.shoal-diff .shoal-sr').textContent, 'becomes ');
        assert.equal(pv.querySelector('.shoal-warnbox').textContent, "Applying this moves 2 friends' titles now. It takes a few minutes. Titles may disappear for a moment and come back. Watched marks are kept for most titles.");
        assert.equal(calls(p, 'POST', /^Layouts\/Apply$/).length, 0);
        // Cancel closes it and gives focus back to the card's button.
        buttonIn(pv, 'Cancel').click();
        await p.wait(30);
        assert.equal(p.page.querySelector('.shoal-preview'), null);
        assert.equal(document.activeElement, buttonIn(preset(p, 'One per friend'), 'Preview'));
    } finally {
        p.close();
    }
});

test('the preview names who would lose access and how many titles collapse', async () => {
    const p = await layouts();
    try {
        buttonIn(preset(p, 'All mixed'), 'Preview').click();
        await p.wait(80);
        const text = p.page.querySelector('.shoal-preview').textContent;
        assert.match(text, /41 titles that show in two libraries today will show in one\./);
        assert.match(text, /Alex will lose access to some friends' titles\./);
    } finally {
        p.close();
    }
});

test('Apply posts the preset and starts the move job card', async () => {
    const p = await layouts({ layoutJobStep: 0 });
    try {
        buttonIn(preset(p, 'One per friend'), 'Preview').click();
        await p.wait(80);
        buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout').click();
        await p.wait(80);
        assert.equal(calls(p, 'POST', /^Layouts\/Apply$/).at(-1).body.preset, 'PerFriend');
        // Pilot 14 UI-APPLY: Apply sends back the preview's planHash (the route refuses without it).
        assert.equal(calls(p, 'POST', /^Layouts\/Apply$/).at(-1).body.planHash, 'ph-PerFriend-1');
        assert.equal(p.page.querySelector('.shoal-preview'), null);
        assert.ok(p.page.querySelector('.shoal-job [role="progressbar"]'));
        assert.equal(live(p), 'Moving titles now. This takes a few minutes.');
    } finally {
        p.close();
    }
});

test('a refused Apply says why in words', async () => {
    const p = await layouts({ refuse: [{ re: /^POST Layouts\/Apply$/, status: 409, error: 'syncing' }] });
    try {
        buttonIn(preset(p, 'All mixed'), 'Preview').click();
        await p.wait(80);
        buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout').click();
        await p.wait(80);
        assert.match(p.text(), /A sync is running\. Try again in a few minutes\./);
    } finally {
        p.close();
    }
});

test('the libraries list: name, type and count, placement, friends who feed it, who can see it', async () => {
    const p = await layouts();
    try {
        const head = p.page.querySelector('.shoal-libs__head');
        assert.equal(head.getAttribute('aria-hidden'), 'true');
        assert.deepEqual([...p.page.querySelectorAll('.shoal-lib .shoal-lib__title')].map((n) => n.textContent), ['Movies', 'Hops Movies', 'Shoal Movies', 'Hops TV Shows', 'Shoal TV Shows']);
        const hops = row(p, 'Hops Movies');
        assert.match(hops.textContent, /Movies · 1,204 titles/);
        assert.match(hops.textContent, /Own Shoal library/);
        assert.equal(hops.querySelector('.shoal-lib__who').textContent, 'Everyone');
        assert.match(row(p, 'Hops TV Shows').textContent, /TV Shows · 86 shows/);
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-lib__who').textContent, '2 people: You and Sam');
        assert.deepEqual([...row(p, 'Shoal TV Shows').querySelectorAll('.shoal-feed__name')].map((n) => n.textContent), ['Fernwood', 'Granite']);
        // Rename and delete name their library.
        assert.ok(byLabel(hops, 'Rename Hops Movies'));
        assert.ok(byLabel(hops, 'Delete Hops Movies'));
    } finally {
        p.close();
    }
});

test('a library added into a main library: placement, the owner\'s placement warning, who sees it, and only "Stop adding"', async () => {
    const p = await layouts();
    try {
        const main = row(p, 'Movies');
        assert.match(main.textContent, /Your main library · 612 friends' titles/);
        assert.equal(main.querySelector('.shoal-place').textContent, 'Added into Movies');
        assert.equal(main.querySelector('.shoal-warnbox').textContent, PLACE_WARN);
        assert.equal(main.querySelector('.shoal-warnbox').getAttribute('role'), 'note');
        assert.equal(main.querySelector('.shoal-lib__who').textContent, 'Everyone who can open Movies, except Kit');
        assert.ok(byLabel(main, 'Stop adding into Movies'));
        assert.equal(byLabel(main, 'Rename Movies'), null);
    } finally {
        p.close();
    }
});

test('without counts from the server the row shows the type alone (no junk)', async () => {
    const p = await layouts({ layoutCounts: false });
    try {
        assert.equal(row(p, 'Hops Movies').querySelector('small').textContent, 'Movies');
        assert.doesNotMatch(p.text(), JUNK);
    } finally {
        p.close();
    }
});

test('moving a friend: the chip opens the panel with the owner\'s move warning; Move waits for a real change; the PUT names the target', async () => {
    const p = await layouts({ layoutJobStep: 0 });
    try {
        const chip = row(p, 'Shoal Movies').querySelector('.shoal-feed');
        assert.equal(chip.getAttribute('aria-label'), "Move Fernwood's movies");
        assert.equal(chip.getAttribute('aria-expanded'), 'false');
        chip.click();
        await p.wait(30);
        const r = row(p, 'Shoal Movies');
        assert.equal(r.querySelector('.shoal-feed').getAttribute('aria-expanded'), 'true');
        const panel = r.querySelector('.shoal-move');
        const select = panel.querySelector('select');
        assert.equal(panel.querySelector('label').textContent, "Move Fernwood's movies to");
        assert.deepEqual([...select.options].map((o) => o.textContent), ['Added into Movies', 'Hops Movies', 'Shoal Movies (now)', 'A new library…']);
        assert.equal(document.activeElement, select);
        const warn = panel.querySelector('.shoal-warnbox');
        assert.equal(warn.textContent, MOVE_WARN);
        assert.ok(select.getAttribute('aria-describedby').split(' ').includes(warn.id));
        const go = buttonIn(panel, 'Move Fernwood');
        assert.equal(go.getAttribute('aria-disabled'), 'true', 'nothing to move yet');
        choose(select, 'lk-hopsmov');
        await p.wait(30);
        const panel2 = row(p, 'Shoal Movies').querySelector('.shoal-move');
        assert.equal(panel2.querySelectorAll('.shoal-warnbox').length, 1, 'no placement warning for an own library');
        buttonIn(panel2, 'Move Fernwood').click();
        await p.wait(120);
        const put = calls(p, 'PUT', /^Connections\/h-fern\/Library$/).at(-1);
        assert.deepEqual(put.body, { type: 'movies', libKey: 'lk-hopsmov' });
        assert.ok(p.page.querySelector('.shoal-job'), 'the job card shows');
        assert.equal(p.page.querySelector('.shoal-job__title').textContent, "Moving Fernwood's movies into Hops Movies");
        assert.deepEqual([...row(p, 'Hops Movies').querySelectorAll('.shoal-feed__name')].map((n) => n.textContent), ['Hops', 'Fernwood']);
    } finally {
        p.close();
    }
});

test('moving into a main library adds the owner\'s placement warning to the move warning', async () => {
    const p = await layouts();
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-feed').click();
        await p.wait(30);
        choose(row(p, 'Shoal Movies').querySelector('.shoal-move select'), 'lk-main');
        await p.wait(30);
        const panel = row(p, 'Shoal Movies').querySelector('.shoal-move');
        const texts = [...panel.querySelectorAll('.shoal-warnbox')].map((w) => w.textContent);
        assert.deepEqual(texts, [MOVE_WARN, PLACE_WARN]);
        const ids = panel.querySelector('select').getAttribute('aria-describedby').split(' ');
        assert.equal(ids.length, 2);
    } finally {
        p.close();
    }
});

test('moving into a new library asks for its name and sends newLibrary; an empty name is refused before any call', async () => {
    const p = await layouts({ mainLibraries: true });
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-feed').click();
        await p.wait(30);
        choose(row(p, 'Shoal Movies').querySelector('.shoal-move select'), 'new');
        await p.wait(30);
        let panel = row(p, 'Shoal Movies').querySelector('.shoal-move');
        const name = panel.querySelector('input');
        assert.equal(name.value, 'Fernwood Movies');
        name.value = ' ';
        name.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(panel, 'Move Fernwood').click();
        await p.wait(30);
        assert.equal(calls(p, 'PUT', /Library$/).length, 0);
        assert.match(panel.textContent, /Type a name\./);
        name.value = 'Fern Films';
        name.dispatchEvent(new window.Event('input', { bubbles: true }));
        // With main libraries known, the new library can be added into one; that brings the placement warning.
        const place = [...panel.querySelectorAll('select')][1];
        assert.deepEqual([...place.options].map((o) => o.textContent), ['As its own library', 'Added into Movies']);
        choose(place, 'main0');
        await p.wait(30);
        panel = row(p, 'Shoal Movies').querySelector('.shoal-move');
        assert.equal(panel.querySelectorAll('.shoal-warnbox').length, 2);
        assert.equal(panel.querySelector('input').value, 'Fern Films', 'the typed name survives the redraw');
        buttonIn(panel, 'Move Fernwood').click();
        await p.wait(80);
        assert.deepEqual(calls(p, 'PUT', /^Connections\/h-fern\/Library$/).at(-1).body, { type: 'movies', newLibrary: { name: 'Fern Films', placement: 'Main', mainLibraryId: 'main-movies' } });
        // Main library ids never reach the page (only an index in the option value).
        assert.doesNotMatch(p.page.innerHTML, /main-movies/);
    } finally {
        p.close();
    }
});

test('Cancel closes the move panel and gives focus back to the chip', async () => {
    const p = await layouts();
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-feed').click();
        await p.wait(30);
        buttonIn(row(p, 'Shoal Movies').querySelector('.shoal-move'), 'Cancel').click();
        await p.wait(30);
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-move'), null);
        assert.equal(document.activeElement, row(p, 'Shoal Movies').querySelector('.shoal-feed'));
    } finally {
        p.close();
    }
});

test('U-M2: who can see a library is edited in a dialog; Everyone is sent as the access body; people who lose access are named', async () => {
    const p = await layouts();
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-lib__who').click();
        await p.wait(30);
        const dlg = dialog(p);
        assert.equal(dlg.querySelector('h2').textContent, 'Who can see Shoal Movies?');
        const radios = [...dlg.querySelectorAll('input[type="radio"]')];
        assert.equal(radios.length, 2);
        assert.equal(radios[0].checked, true, 'Only selected users');
        // Kit has parental limits: not pickable until the ratings box is ticked.
        const kit = [...dlg.querySelectorAll('.shoal-picker__row')].find((r) => r.textContent.includes('Kit'));
        assert.equal(kit.querySelector('input').disabled, true);
        // Untick Sam, then save.
        const sam = [...dlg.querySelectorAll('.shoal-picker__row')].find((r) => r.querySelector('.shoal-check__label').textContent === 'Sam');
        sam.querySelector('input').click();
        buttonIn(dlg, 'Save').click();
        await p.wait(80);
        const put = calls(p, 'PUT', /^Layouts\/Libraries\/lk-shoalmov$/).at(-1);
        assert.deepEqual(put.body, { access: { mode: 'Selected', selectedUserIds: ['u-rich'], includeRestricted: false, ratingsAcknowledged: false } });
        assert.equal(live(p), 'Sam can no longer see Shoal Movies.');
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-lib__who').textContent, 'Only you');
        assert.equal(document.activeElement, row(p, 'Shoal Movies').querySelector('.shoal-lib__who'));
    } finally {
        p.close();
    }
});

test('U-M2: switching a library to Everyone disables the list and saves the mode', async () => {
    const p = await layouts();
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-lib__who').click();
        await p.wait(30);
        const dlg = dialog(p);
        const everyone = [...dlg.querySelectorAll('input[type="radio"]')][1];
        everyone.checked = true;
        everyone.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.ok([...dlg.querySelectorAll('.shoal-picker__row input')].every((i) => i.disabled));
        buttonIn(dlg, 'Save').click();
        await p.wait(80);
        assert.equal(calls(p, 'PUT', /^Layouts\/Libraries\/lk-shoalmov$/).at(-1).body.access.mode, 'Everyone');
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-lib__who').textContent, 'Everyone');
    } finally {
        p.close();
    }
});

test('rename: the dialog sends the name; a name in use is refused in words and the dialog stays', async () => {
    const p = await layouts();
    try {
        byLabel(row(p, 'Hops Movies'), 'Rename Hops Movies').click();
        await p.wait(30);
        let dlg = dialog(p);
        const input = dlg.querySelector('input');
        assert.equal(input.value, 'Hops Movies');
        input.value = 'Shoal Movies';
        buttonIn(dlg, 'Save').click();
        await p.wait(60);
        dlg = dialog(p);
        assert.ok(dlg, 'still open');
        assert.match(dlg.textContent, /Another library already has that name\. Pick another one\./);
        dlg.querySelector('input').value = 'Hops Films';
        buttonIn(dlg, 'Save').click();
        await p.wait(120);
        assert.equal(dialog(p), null);
        assert.deepEqual(calls(p, 'PUT', /^Layouts\/Libraries\/lk-hopsmov$/).at(-1).body, { name: 'Hops Films' });
        assert.ok(row(p, 'Hops Films'));
    } finally {
        p.close();
    }
});

test('delete: a library friends still feed explains what to do and sends nothing', async () => {
    const p = await layouts();
    try {
        byLabel(row(p, 'Hops Movies'), 'Delete Hops Movies').click();
        await p.wait(30);
        const dlg = dialog(p);
        assert.equal(dlg.querySelector('h2').textContent, 'Hops Movies still has friends in it');
        assert.match(dlg.textContent, /Move Hops to another library first\./);
        buttonIn(dlg, 'Close').click();
        await p.wait(30);
        assert.equal(calls(p, 'DELETE', /Layouts/).length, 0);
    } finally {
        p.close();
    }
});

test('create, then delete an empty library', async () => {
    const p = await layouts();
    try {
        buttonIn(p.page, 'Create library').click();
        await p.wait(30);
        let dlg = dialog(p);
        assert.equal(dlg.querySelector('h2').textContent, 'Create a library');
        dlg.querySelector('input').value = 'Docs';
        choose(dlg.querySelector('select'), 'tvshows');
        buttonIn(dlg.querySelector('.shoal-dialog__foot'), 'Create library').click();
        await p.wait(120);
        assert.deepEqual(calls(p, 'POST', /^Layouts\/Libraries$/).at(-1).body, { name: 'Docs', type: 'tvshows', placement: 'Shoal' });
        const docs = row(p, 'Docs');
        assert.ok(docs);
        assert.match(docs.textContent, /No friends yet/);
        byLabel(docs, 'Delete Docs').click();
        await p.wait(30);
        dlg = dialog(p);
        assert.equal(dlg.querySelector('h2').textContent, 'Delete Docs?');
        buttonIn(dlg, 'Delete library').click();
        await p.wait(150);
        assert.equal(calls(p, 'DELETE', /^Layouts\/Libraries\/lk-new5$/).length, 1);
        assert.equal(row(p, 'Docs'), undefined);
    } finally {
        p.close();
    }
});

test('a legacy library can be renamed but has no delete button', async () => {
    const p = await layouts({ layoutLegacy: true });
    try {
        const old = row(p, 'Old Movies');
        assert.ok(byLabel(old, 'Rename Old Movies'));
        assert.equal(byLabel(old, 'Delete Old Movies'), null);
    } finally {
        p.close();
    }
});

test('library order: one list per type, arrows disabled at the ends, a move sends the full order and keeps focus', async () => {
    const p = await layouts();
    try {
        const lists = [...p.page.querySelectorAll('ol.shoal-order')];
        assert.equal(lists.length, 2);
        assert.deepEqual(orderNames(lists[0]), ['Movies', 'Hops Movies', 'Shoal Movies']);
        assert.deepEqual(orderNames(lists[1]), ['Hops TV Shows', 'Shoal TV Shows']);
        assert.equal(byLabel(lists[0], 'Move Movies up').disabled, true);
        assert.equal(byLabel(lists[0], 'Move Shoal Movies down').disabled, true);
        assert.equal(lists[0].querySelector('.shoal-rank').getAttribute('aria-hidden'), 'true');
        assert.ok(lists[0].getAttribute('aria-describedby'));
        byLabel(lists[0], 'Move Hops Movies up').click();
        await p.wait(60);
        assert.deepEqual(calls(p, 'PUT', /^Layouts\/Libraries$/).at(-1).body.order, ['lk-hopsmov', 'lk-main', 'lk-shoalmov', 'lk-hopstv', 'lk-shoaltv']);
        const after = [...p.page.querySelectorAll('ol.shoal-order')][0];
        assert.deepEqual(orderNames(after), ['Hops Movies', 'Movies', 'Shoal Movies']);
        assert.equal(live(p), 'Hops Movies is now number 1 of 3.');
        // Up is disabled at the top now, so focus moves to the down arrow of the same library.
        assert.equal(document.activeElement, byLabel(after, 'Move Hops Movies down'));
    } finally {
        p.close();
    }
});

test('library order: drag and drop reorders within a type', async () => {
    const p = await layouts();
    try {
        const items = [...p.page.querySelectorAll('ol.shoal-order')[0].querySelectorAll('li')];
        assert.equal(items[2].getAttribute('draggable'), 'true');
        const fire = (node, type) => {
            const ev = new window.Event(type, { bubbles: true, cancelable: true });
            node.dispatchEvent(ev);
            return ev;
        };
        fire(items[2], 'dragstart');
        assert.ok(items[2].classList.contains('is-dragging'));
        assert.equal(fire(items[0], 'dragover').defaultPrevented, true, 'a drop target of the same type');
        // A TV library is not a target for a movie library.
        const tv = p.page.querySelectorAll('ol.shoal-order')[1].querySelector('li');
        assert.equal(fire(tv, 'dragover').defaultPrevented, false);
        fire(items[0], 'drop');
        await p.wait(60);
        assert.deepEqual(calls(p, 'PUT', /^Layouts\/Libraries$/).at(-1).body.order, ['lk-shoalmov', 'lk-main', 'lk-hopsmov', 'lk-hopstv', 'lk-shoaltv']);
    } finally {
        p.close();
    }
});

test('a refused order says so and keeps the old order', async () => {
    const p = await layouts({ refuse: [{ re: /^PUT Layouts\/Libraries$/, status: 409, error: 'busy' }] });
    try {
        byLabel(p.page, 'Move Hops Movies up').click();
        await p.wait(60);
        assert.match(p.text(), /Another layout change is running\. Try again when it finishes\./);
        assert.deepEqual(orderNames(p.page.querySelector('ol.shoal-order')), ['Movies', 'Hops Movies', 'Shoal Movies']);
    } finally {
        p.close();
    }
});

test('new connections: read only with the flag off, else a select that saves through PUT Layouts/Default', async () => {
    const p = await layouts({ features: { layoutDefault: false } });
    try {
        const panel = p.page.querySelector('.shoal-defpanel');
        assert.equal(panel.querySelector('h2').textContent, 'New connections');
        assert.match(panel.textContent, /New friends' servers: Add to Shoal Movies and Shoal TV Shows\./);
        assert.equal(panel.querySelector('select'), null);
    } finally {
        p.close();
    }

    const q = await layouts();
    try {
        const select = q.page.querySelector('.shoal-defpanel select');
        assert.equal(q.page.querySelector('.shoal-defpanel label').textContent, "Layout for new friends' servers");
        assert.deepEqual([...select.options].map((o) => o.textContent), ['One per friend (own Movies and TV Shows)', 'Add to Shoal Movies and Shoal TV Shows']);
        assert.equal(select.value, 'AllMixed');
        choose(select, 'PerFriend');
        await q.wait(60);
        assert.deepEqual(calls(q, 'PUT', /^Layouts\/Default$/).at(-1).body, { preset: 'PerFriend', defaultTargets: { movies: 'lk-shoalmov', tvshows: 'lk-shoaltv' } });
        assert.equal(select.value, 'PerFriend');
        assert.equal(live(q), "Saved. New friends' servers: One per friend (own Movies and TV Shows).");
    } finally {
        q.close();
    }
});

test('the default select reverts and explains when the route is missing (an older server)', async () => {
    const p = await layouts({ layoutDefaultRoute: false });
    try {
        const select = p.page.querySelector('.shoal-defpanel select');
        choose(select, 'PerFriend');
        await p.wait(60);
        assert.equal(select.value, 'AllMixed');
        assert.match(p.text(), /This feature needs a newer Shoal\./);
    } finally {
        p.close();
    }
});

test('U-M2: Hide friends\' titles is a labelled switch per person; Kit starts hidden; a change is saved and announced', async () => {
    const p = await layouts();
    try {
        const switches = [...p.page.querySelectorAll('.shoal-people input')];
        assert.equal(switches.length, 6, 'sharing accounts are not listed');
        for (const s of switches) {
            assert.equal(s.getAttribute('role'), 'switch');
            assert.match(s.getAttribute('aria-label'), /^Hide friends' titles for /);
        }

        const kit = byLabel(p.page, "Hide friends' titles for Kit");
        assert.equal(kit.checked, true);
        const person = (name) => [...p.page.querySelectorAll('.shoal-person')].find((li) => li.querySelector('b').textContent === name);
        assert.match(person('Rich').textContent, /Admin \(you\)/);
        assert.match(person('Kit').textContent, /Profile with parental limits/);
        assert.match(person('Alex').textContent, /Can see 4 of 5 libraries/);
        const sam = byLabel(p.page, "Hide friends' titles for Sam");
        sam.click();
        await p.wait(60);
        assert.deepEqual(calls(p, 'PUT', /^Users\/u-sam\/FriendTitles$/).at(-1).body, { hidden: true });
        assert.equal(live(p), "Friends' titles are hidden for Sam.");
        assert.equal(byLabel(p.page, "Hide friends' titles for Sam").checked, true);
        assert.equal(document.activeElement, byLabel(p.page, "Hide friends' titles for Sam"));
        // The main library's "who can see" line follows.
        assert.equal(row(p, 'Movies').querySelector('.shoal-lib__who').textContent, 'Everyone who can open Movies, except Kit and Sam');
    } finally {
        p.close();
    }
});

test('a Hide friends\' titles switch that does not save flips back and says so', async () => {
    const p = await layouts({ failPaths: [/FriendTitles/] });
    try {
        const sam = byLabel(p.page, "Hide friends' titles for Sam");
        sam.click();
        await p.wait(60);
        assert.equal(byLabel(p.page, "Hide friends' titles for Sam").checked, false);
        assert.match(p.text(), /That didn't save for Sam\. Try again in a moment\./);
    } finally {
        p.close();
    }
});

// The one place a local account id may appear is the avatar picture this server serves (/Users/{id}/Images/Primary), as in Jellyfin's own pages and the picker.
const withoutIds = (html) => html.replace(/id="[^"]*"/g, '').replace(/src="\/Users\/[^"]*\/Images\/Primary[^"]*"/g, '');

test('account ids are never written into the page, even when they are GUIDs', async () => {
    const p = await layouts({ guidIds: true });
    try {
        row(p, 'Shoal Movies').querySelector('.shoal-lib__who').click();
        await p.wait(30);
        assert.doesNotMatch(withoutIds(p.page.innerHTML), GUID);
        buttonIn(dialog(p), 'Cancel').click();
        await p.wait(30);
        byLabel(p.page, "Hide friends' titles for Sam").click();
        await p.wait(60);
        assert.equal(calls(p, 'PUT', /FriendTitles$/).at(-1).path, 'Users/7a2d3b01-2c3d-4e4f-9051-1b2c3d4e5f60/FriendTitles');
        assert.doesNotMatch(withoutIds(p.page.innerHTML), GUID);
    } finally {
        p.close();
    }
});

test('hostile names stay text everywhere on the screen', async () => {
    const p = await layouts({ scenario: 'hostile' });
    try {
        assert.equal(p.page.querySelectorAll('img[src="x"], script').length, 0);
        assert.ok(p.text().includes(XSS));
        for (const node of p.page.querySelectorAll('*')) {
            for (const attr of node.getAttributeNames()) {
                assert.ok(!/^on/i.test(attr), attr);
            }
        }

        row(p, XSS).querySelector('.shoal-feed').click();
        await p.wait(30);
        assert.equal(p.page.querySelectorAll('img[src="x"]').length, 0);
    } finally {
        p.close();
    }
});

test('with no Shoal libraries the list says so and the order section is left out', async () => {
    const p = await layouts({ scenario: 'empty' });
    try {
        assert.match(p.text(), /No Shoal libraries yet\. Add a friend's server, or create a library\./);
        assert.equal(p.page.querySelector('ol.shoal-order'), null);
        assert.doesNotMatch(p.text(), JUNK);
    } finally {
        p.close();
    }
});

test('a layout that fails to load shows the retry box', async () => {
    const p = await layouts({ failPaths: [/^GET Layouts\/Libraries$/] });
    try {
        assert.ok(p.page.querySelector('[data-shoal-view="layouts"] .shoal-loaderror'));
    } finally {
        p.close();
    }
});

test('Settings and Libraries & access link to the layouts screen; Settings says what the layout is now', async () => {
    const s = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400 });
    try {
        const panel = s.page.querySelector('[data-shoal-view="settings"] .shoal-linkpanel');
        assert.equal(panel.querySelector('h2').textContent, 'Library layouts');
        assert.equal(panel.querySelector('.shoal-meta').textContent, 'Current: Custom. 4 Shoal libraries, 1 added into a main library.');
        const a = panel.querySelector('a');
        assert.equal(a.textContent, 'Change library layouts');
        assert.match(a.getAttribute('href'), /view=layouts/);
        a.click();
        await s.wait(20);
        assert.match(s.navigations.at(-1), /view=layouts/);
    } finally {
        s.close();
    }

    const l = await mountPage({ scenario: 'default', params: { view: 'libraries' }, settleMs: 400 });
    try {
        const a = l.page.querySelector('[data-shoal-view="libraries"] .shoal-linkpanel a');
        assert.match(a.getAttribute('href'), /view=layouts/);
    } finally {
        l.close();
    }
});

test('helpers: placeWithinType keeps the other type in place; previewRows groups by source', async () => {
    const p = await layouts();
    try {
        const { placeWithinType, previewRows } = p.mod;
        const libs = [{ libKey: 'a', type: 'movies' }, { libKey: 'x', type: 'tvshows' }, { libKey: 'b', type: 'movies' }, { libKey: 'c', type: 'movies' }];
        assert.deepEqual(placeWithinType(libs, ['a', 'x', 'b', 'c'], 'c', 0), ['c', 'x', 'a', 'b']);
        assert.deepEqual(placeWithinType(libs, ['a', 'x', 'b', 'c'], 'a', 9), ['b', 'x', 'c', 'a']);
        assert.deepEqual(placeWithinType(libs, ['a', 'x', 'b', 'c'], 'zz', 0), ['a', 'x', 'b', 'c']);
        assert.deepEqual(previewRows({ moves: [] }, []), []);
        assert.deepEqual(previewRows({ moves: [{ peerKey: 'h', peerName: 'H', type: 'movies', from: [], to: 'T' }] }, []), [{ was: 'H', to: 'T' }]);
    } finally {
        p.close();
    }
});

test('Pilot 14: a plan that changed after the preview (409 planChanged) shows the new plan and says so; applying that one works', async () => {
    const p = await layouts({ layoutJobStep: 0, planChangedOnce: true });
    try {
        buttonIn(preset(p, 'One per friend'), 'Preview').click();
        await p.wait(80);
        buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout').click();
        await p.wait(120);
        assert.equal(calls(p, 'POST', /^Layouts\/Preview$/).length, 2, 'previewed again');
        assert.ok(p.page.querySelector('.shoal-preview'), 'the new plan is shown');
        assert.equal(document.activeElement, p.page.querySelector('.shoal-preview__title'));
        assert.match(p.text(), /Something changed since the preview\. Check the new plan below, then apply it\./);
        assert.equal(p.page.querySelector('.shoal-job [role="progressbar"]'), null, 'nothing started');
        buttonIn(p.page.querySelector('.shoal-preview'), 'Apply layout').click();
        await p.wait(120);
        const applies = calls(p, 'POST', /^Layouts\/Apply$/);
        assert.equal(applies.length, 2);
        assert.equal(applies[1].body.planHash, 'ph-PerFriend-2');
        assert.ok(p.page.querySelector('.shoal-job [role="progressbar"]'));
    } finally {
        p.close();
    }
});

// Pilot 15: Custom is the list edited by hand. Once the list no longer matches the named layout, the card offers Review changes, and Apply Custom
// (no moves, with the preview's planHash) makes Custom the current layout.
test('Pilot 15: Custom says how it works; a list that no longer matches the named layout offers Review changes and Apply Custom', async () => {
    const p = await layouts({ layoutPreset: 'AllMixed' });
    try {
        const card = preset(p, 'Custom');
        assert.match(card.textContent, /You make the groups yourself: use Move and Create library in the list below\. Each move happens right away\./);
        assert.match(card.textContent, /Your list no longer matches All mixed\./);
        const review = buttonIn(card, 'Review changes');
        assert.equal(review.getAttribute('aria-label'), 'Review changes for Custom');
        assert.equal(review.getAttribute('aria-pressed'), 'false');
        review.click();
        await p.wait(80);
        const pv = p.page.querySelector('.shoal-preview');
        assert.equal(pv.querySelector('h3').textContent, 'Review changes: Custom');
        assert.equal(document.activeElement, pv.querySelector('h3'));
        assert.match(pv.textContent, /Your libraries no longer match All mixed\. Applying Custom keeps them exactly as they are below: nothing moves\./);
        assert.equal(pv.querySelector('.shoal-warnbox'), null, 'no move warning: nothing moves');
        const apply = buttonIn(pv, 'Apply Custom');
        assert.equal(apply.getAttribute('aria-disabled'), null);
        apply.click();
        await p.wait(150);
        const body = calls(p, 'POST', /^Layouts\/Apply$/).at(-1).body;
        assert.deepEqual(body, { preset: 'Custom', planHash: 'ph-Custom-1' });
        assert.equal(live(p), 'Custom is now your layout. Nothing moved.');
        assert.equal(p.page.querySelector('.shoal-preview'), null);
        assert.equal(p.page.querySelector('.shoal-job [role="progressbar"]'), null, 'no move job');
        assert.ok(preset(p, 'Custom').querySelector('.shoal-cur'), 'Custom is current now');
        assert.equal(buttonIn(preset(p, 'Custom'), 'Review changes'), undefined);
    } finally {
        p.close();
    }
});

test('Pilot 15: Review changes stays away while the list still matches the named layout, and while Custom is current', async () => {
    for (const extra of [{ layoutPreset: 'PerFriend', layoutMatches: true }, { layoutPreset: 'Custom' }]) {
        const p = await layouts(extra);
        try {
            assert.equal(buttonIn(preset(p, 'Custom'), 'Review changes'), undefined, JSON.stringify(extra));
            assert.doesNotMatch(preset(p, 'Custom').textContent, /no longer matches/);
        } finally {
            p.close();
        }
    }
});

// Pilot 15 R6 (docs/kiro-v2.9-09.md item 4): a Main placement whose target main library has the NFO saver on is named in plain words on its row.
const NFO = "The NFO saver is on for your main library Movies. Jellyfin can overwrite friends' titles' descriptions and art. Turn it off for this library in Jellyfin (Dashboard, Libraries).";
test('R6: the NFO saver warning shows on the Main row of that main library only, and not without the field', async () => {
    const p = await layouts({ mainTarget: true, mainLibraries: true, nfoMains: ['Movies', 'Elsewhere', 7, null] });
    try {
        const notes = [...p.page.querySelectorAll('.shoal-warnbox--nfo')];
        assert.equal(notes.length, 1, 'only the row whose main library is listed');
        assert.equal(notes[0].textContent, NFO);
        assert.equal(notes[0].getAttribute('role'), 'note');
        assert.ok(row(p, 'Friends in Movies').contains(notes[0]));
    } finally {
        p.close();
    }

    const q = await layouts();
    try {
        assert.equal(q.page.querySelectorAll('.shoal-warnbox--nfo').length, 0);
    } finally {
        q.close();
    }
});

// Pilot 16: mainLibrariesSavingNfo names the TARGET main library ("Movies"), never the Shoal library ("Friends in Movies").
test('Pilot 16: the warning matches the Main row by the library it is added into, not by its own name; a Shoal row stays clean', async () => {
    // As the backend answers today: the Main row has only its target's id, and no list of main libraries; it is the only Main row.
    const p = await layouts({ mainTarget: true, nfoMains: ['Movies'] });
    try {
        const main = row(p, 'Friends in Movies');
        const notes = [...main.querySelectorAll('.shoal-warnbox--nfo')].map((n) => n.textContent);
        assert.deepEqual(notes, ["The NFO saver is on for your main library Movies. Jellyfin can overwrite friends' titles' descriptions and art. Turn it off for this library in Jellyfin (Dashboard, Libraries)."]);
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-warnbox--nfo'), null);
        assert.equal(p.page.querySelectorAll('.shoal-warnbox--nfo').length, 1);
        assert.doesNotMatch(p.page.innerHTML, /main-movies/, 'the target id is compared, never shown');
    } finally {
        p.close();
    }

    // With the main libraries listed, the target is known by id: its placement reads "Added into Movies", and a saver on another library is not shown here.
    const q = await layouts({ mainTarget: true, mainLibraries: true, nfoMains: ['Shows'] });
    try {
        assert.match(row(q, 'Friends in Movies').textContent, /Added into Movies/);
        assert.equal(q.page.querySelectorAll('.shoal-warnbox--nfo').length, 0);
    } finally {
        q.close();
    }
});

test('Pilot 16: with several Main rows and no target names, the warning goes above the list instead of guessing a row', async () => {
    const p = await layouts({ mainTarget: true, secondMain: true, nfoMains: ['Movies'] });
    try {
        const notes = [...p.page.querySelectorAll('.shoal-warnbox--nfo')];
        assert.equal(notes.length, 1);
        assert.equal(notes[0].closest('.shoal-lib'), null, 'not on a row');
        assert.ok(notes[0].compareDocumentPosition(p.page.querySelector('.shoal-libs')) & 4, 'above the list');
    } finally {
        p.close();
    }

    // With mainLibraries the two rows are told apart: the warning goes on the row added into Movies only.
    const q = await layouts({ mainTarget: true, secondMain: true, mainLibraries: true, nfoMains: ['Movies'] });
    try {
        assert.ok(row(q, 'Friends in Movies').querySelector('.shoal-warnbox--nfo'));
        assert.equal(row(q, 'Friends in Shows').querySelector('.shoal-warnbox--nfo'), null);
    } finally {
        q.close();
    }
});

// Warden 12 L2: a Jellyfin library holding a Shoal folder beside other folders is not locked; the page says so on that library's row and in Settings.
const NOT_LOCKED = (name) => "Your library " + name + " holds a friends' folder next to your own folders, so Shoal leaves its settings alone. Turn off its NFO saver and internet metadata in Jellyfin (Dashboard, Libraries) if you don't want them to touch friends' titles, or move the friends' folder into a library of its own.";
test('L2: not-locked libraries are named on their row (Shoal row by its name, Main row by its target), unknown ones above the list', async () => {
    const p = await layouts({ mainTarget: true, mainLibraries: true, notLocked: ['Hops Movies', 'Movies', 'Somewhere else'] });
    try {
        assert.deepEqual([...row(p, 'Hops Movies').querySelectorAll('.shoal-warnbox--notlocked')].map((n) => n.textContent), [NOT_LOCKED('Hops Movies')]);
        assert.deepEqual([...row(p, 'Friends in Movies').querySelectorAll('.shoal-warnbox--notlocked')].map((n) => n.textContent), [NOT_LOCKED('Movies')]);
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-warnbox--notlocked'), null);
        const above = [...p.page.querySelectorAll('.shoal-warnbox--notlocked')].filter((n) => !n.closest('.shoal-lib'));
        assert.deepEqual(above.map((n) => n.textContent), [NOT_LOCKED('Somewhere else')]);
    } finally {
        p.close();
    }

    const q = await layouts();
    try {
        assert.equal(q.page.querySelectorAll('.shoal-warnbox--notlocked').length, 0);
    } finally {
        q.close();
    }
});

test('L4: a rename refused after the access part was saved says that who can see the library changed anyway', async () => {
    const p = await layouts({ renameFailsAfterAccess: true });
    try {
        byLabel(row(p, 'Hops Movies'), 'Rename Hops Movies').click();
        await p.wait(30);
        const dlg = dialog(p);
        const input = dlg.querySelector('input');
        input.value = 'Hops Films';
        input.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(dlg, 'Save').click();
        await p.wait(80);
        assert.match(dialog(p).textContent, /Who can see this library was changed anyway\./);
    } finally {
        p.close();
    }
});

// Owner decision: Combine into my libraries (friends' movies into your Movies library, friends' shows into your TV Shows library).
const MIX = (p) => preset(p, 'Combine into my libraries');
test('Combine: the card and its picture (your Movies and Shows with friends inside); one library of each type is chosen for you', async () => {
    const p = await layouts({ layoutJobStep: 0 });
    try {
        assert.match(MIX(p).textContent, /Friends' movies go into your Movies library and friends' shows into your TV Shows library\./);
        assert.equal(MIX(p).querySelectorAll('.mini-libs--2 .mini-lib--mixed').length, 2);
        buttonIn(MIX(p), 'Preview').click();
        await p.wait(80);
        const pv = p.page.querySelector('.shoal-preview');
        assert.equal(pv.querySelector('h3').textContent, 'Preview: Combine into my libraries');
        assert.equal(pv.querySelector('select'), null, 'nothing to pick with one library per type');
        assert.match(pv.textContent, /Friends' movies go into Movies.Friends' shows go into TV Shows./);
        assert.match(pv.textContent, /12 titles that show in two libraries today will show in one\./);
        assert.match(pv.textContent, /Everyone who can open that library will see these titles unless you hide them for a person below\./);
        assert.ok(pv.querySelector('.shoal-warnbox').textContent.startsWith('Applying this moves'), 'the move warning stays first');
        buttonIn(pv, 'Apply layout').click();
        await p.wait(100);
        const body = calls(p, 'POST', /^Layouts\/Apply$/).at(-1).body;
        assert.equal(body.preset, 'CombineIntoMine');
        assert.equal(typeof body.planHash, 'string');
        assert.equal(body.targets, undefined, 'nothing picked, nothing sent');
        assert.ok(p.page.querySelector('.shoal-job [role="progressbar"]'));
    } finally {
        p.close();
    }
});

test('Combine: with two movie libraries the admin picks one in a plain select; Apply waits for it; the pick previews again and is sent with Apply', async () => {
    const p = await layouts({ layoutJobStep: 0, combine: 'many', notLocked: ['TV Shows'] });
    try {
        buttonIn(MIX(p), 'Preview').click();
        await p.wait(80);
        let pv = p.page.querySelector('.shoal-preview');
        const select = pv.querySelector('select');
        assert.equal(pv.querySelector('label[for="' + select.id + '"]').textContent, "Put friends' movies into");
        assert.deepEqual([...select.options].map((o) => o.textContent), ['Choose a library', 'Movies', 'Kids Movies']);
        assert.doesNotMatch(pv.innerHTML, /0f1e2d3c|1a2b3c4d/, 'library ids never reach the page');
        const apply = buttonIn(pv, 'Apply layout');
        assert.equal(apply.getAttribute('aria-disabled'), 'true');
        assert.equal(p.page.querySelector('#' + apply.getAttribute('aria-describedby')).textContent, "Choose a library above for friends' titles, then apply.");
        assert.match(pv.textContent, /Your library TV Shows holds a friends' folder next to your own folders/);
        select.value = '0';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(100);
        assert.deepEqual(calls(p, 'POST', /^Layouts\/Preview$/).at(-1).body, { preset: 'CombineIntoMine', targets: { movies: '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b' } });
        pv = p.page.querySelector('.shoal-preview');
        assert.equal(document.activeElement, pv.querySelector('select'), 'focus stays on the choice');
        assert.deepEqual([...pv.querySelector('select').options].map((o) => o.textContent), ['Movies', 'Kids Movies']);
        assert.match(pv.textContent, /The NFO saver is on for your main library Movies\./);
        assert.equal(buttonIn(pv, 'Apply layout').getAttribute('aria-disabled'), null);
        buttonIn(pv, 'Apply layout').click();
        await p.wait(100);
        assert.deepEqual(calls(p, 'POST', /^Layouts\/Apply$/).at(-1).body.targets, { movies: '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b' });
        assert.ok(p.page.querySelector('.shoal-job [role="progressbar"]'));
    } finally {
        p.close();
    }
});

test('Combine: no TV library of their own says friends\' shows stay where they are', async () => {
    const p = await layouts({ combine: 'noTv' });
    try {
        buttonIn(MIX(p), 'Preview').click();
        await p.wait(80);
        assert.match(p.page.querySelector('.shoal-preview').textContent, /You have no TV library of your own, so friends' shows stay where they are\./);
    } finally {
        p.close();
    }
});

// Warden 13 L5/L6 (docs/kiro-v2.9-13.md): a Main record whose main library no longer holds its folder, and Combine targets that can rewrite friends' titles.
test('L5: a missing main library is named on its Main row (by the Shoal library name), and above the list when no row matches', async () => {
    const p = await layouts({ mainTarget: true, mainsMissing: ['Friends in Movies', 'Gone Library'] });
    try {
        const onRow = [...row(p, 'Friends in Movies').querySelectorAll('.shoal-warnbox--missing')].map((n) => n.textContent);
        assert.deepEqual(onRow, ["Friends in Movies was added into one of your libraries, but that library no longer holds it, so its titles show nowhere. The library may have been removed or renamed in Jellyfin (Dashboard, Libraries). Delete Friends in Movies here, or pick another layout."]);
        const above = [...p.page.querySelectorAll('.shoal-warnbox--missing')].filter((n) => !n.closest('.shoal-lib'));
        assert.equal(above.length, 1);
        assert.match(above[0].textContent, /^Gone Library was added into one of your libraries/);
        assert.equal(row(p, 'Shoal Movies').querySelector('.shoal-warnbox--missing'), null);
    } finally {
        p.close();
    }

    const q = await layouts();
    try {
        assert.equal(q.page.querySelectorAll('.shoal-warnbox--missing').length, 0);
    } finally {
        q.close();
    }
});

test('L6: the Combine preview names each chosen library that can rewrite friends\' titles, once, next to the NFO line', async () => {
    const p = await layouts({ combine: 'many' });
    try {
        buttonIn(MIX(p), 'Preview').click();
        await p.wait(80);
        assert.equal(p.page.querySelectorAll('.shoal-warnbox--rewrites').length, 0, 'nothing chosen yet');
        const select = p.page.querySelector('.shoal-preview select');
        select.value = '0';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(100);
        const notes = [...p.page.querySelectorAll('.shoal-preview .shoal-warnbox--rewrites')].map((n) => n.textContent);
        assert.deepEqual(notes, ["Friends' titles in Movies can be rewritten by that library's metadata downloaders and NFO saver. They stay hidden from people you have not picked only through Shoal's hidden tag, which Shoal puts back after each sync. Turn off internet metadata and the NFO saver for Movies in Jellyfin (Dashboard, Libraries) to keep friends' titles as they are."]);
        assert.equal(p.page.querySelectorAll('.shoal-preview .shoal-warnbox--nfo').length, 1, 'the NFO line stays');
        assert.match(p.page.querySelector('.shoal-preview').textContent, /Everyone who can open that library will see these titles/, 'the placement warning stays');
    } finally {
        p.close();
    }
});
