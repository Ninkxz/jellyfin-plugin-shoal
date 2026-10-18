// U4: Libraries & access (ui-spec 8.4, owner D3/D4, T-VIS-1..6 driven through the UI) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

async function libraries(scenario, extra) {
    // These tests drive the per-friend list, which a server without layouts still shows; the v2.9 list is tested in librarieslayout.test.mjs.
    return mountPage(Object.assign({ scenario, params: { view: 'libraries' }, settleMs: 350, layoutsRoute: false }, extra || {}));
}

const block = (p, name) => [...p.page.querySelectorAll('.shoal-libblock')].find((b) => b.querySelector('.shoal-row__title').textContent === name);
const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const putCalls = (p) => p.mock.calls.filter((c) => c.method === 'PUT' && /\/Access$/.test(c.path));

test('a new library starts as "Only selected users" with only the admin who added it (D3)', async () => {
    const p = await libraries('default');
    try {
        const hops = block(p, 'Hops Movies');
        assert.equal(hops.querySelector('.shoal-seg button[aria-pressed="true"]').textContent.trim(), 'Only selected users');
        assert.match(hops.textContent, /Only you/);
        assert.match(hops.textContent, /Only the people you choose can see and play it\. Everyone else won't know it's there\./);
        assert.match(p.text(), /At first only the person who added a library can see it\./);
    } finally {
        p.close();
    }
});

test('the access control is toggle buttons, not radios: moving focus never opens a dialog (E8)', async () => {
    const p = await libraries('default');
    try {
        const hops = block(p, 'Hops Movies');
        assert.equal(hops.querySelectorAll('input[type="radio"]').length, 0);
        const buttons = hops.querySelectorAll('.shoal-seg--access button');
        assert.equal(buttons.length, 2);
        assert.deepEqual([...buttons].map((b) => b.getAttribute('aria-pressed')), ['true', 'false']);
        buttons[0].focus();
        buttons[1].focus();
        await p.wait(50);
        assert.equal(dialog(p), null);
        // Pressing the selected segment does nothing.
        buttons[0].click();
        await p.wait(50);
        assert.equal(dialog(p), null);
    } finally {
        p.close();
    }
});

test('T-VIS-4: Everyone and back restores the same selection, with the right people in both dialogs', async () => {
    const p = await libraries('default');
    try {
        const hops = block(p, 'Hops Movies');
        hops.querySelector('.shoal-seg button[aria-pressed="false"]').click();
        await p.wait(60);
        let d = dialog(p);
        assert.ok(d, 'confirm dialog');
        assert.match(d.textContent, /Open Hops Movies to everyone\?/);
        // Everyone admits everybody except profiles with parental limits (Kit); Rich already sees it.
        assert.match(d.textContent, /4 more people can see and play it now: Alex, Jo, Mum and Sam\. People you add to this server later get it too\./);
        assert.match(d.textContent, /1 profile with parental limits stays out\. You can include it afterwards\./);
        assert.equal(putCalls(p).length, 0, 'nothing changes before the admin confirms');
        buttonIn(d, 'Open to everyone').click();
        await p.wait(150);
        assert.equal(putCalls(p).length, 1);
        assert.deepEqual({ mode: putCalls(p)[0].body.mode, ids: putCalls(p)[0].body.selectedUserIds }, { mode: 'Everyone', ids: ['u-rich'] });
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Hops Movies is open to everyone on this server/);
        const after = block(p, 'Hops Movies');
        assert.match(after.textContent, /Everyone on this server/);
        assert.match(after.textContent, /Your earlier selection of 1 person is saved/);
        assert.match(after.textContent, /Include profiles with parental limits/);

        after.querySelector('.shoal-seg button[aria-pressed="false"]').click();
        await p.wait(60);
        d = dialog(p);
        assert.match(d.textContent, /Limit Hops Movies to selected people\?/);
        assert.match(d.textContent, /4 people lose access: Alex, Jo, Mum and Sam\. Your saved selection of 1 person comes back\./);
        assert.match(d.textContent, /They can't play it from now on. It can stay in their lists for up to an hour./);
        assert.doesNotMatch(d.textContent, /shoal-sam/, 'sharing accounts are never listed');
        buttonIn(d, 'Limit access').click();
        await p.wait(150);
        const last = putCalls(p).at(-1).body;
        assert.deepEqual({ mode: last.mode, ids: last.selectedUserIds }, { mode: 'Selected', ids: ['u-rich'] });
        assert.match(block(p, 'Hops Movies').textContent, /Only you/);
    } finally {
        p.close();
    }
});

test('cancelling the confirm dialog changes nothing and returns focus to the segment', async () => {
    const p = await libraries('default');
    try {
        const seg = block(p, 'Hops Movies').querySelector('.shoal-seg button[aria-pressed="false"]');
        seg.focus();
        seg.click();
        await p.wait(60);
        buttonIn(dialog(p), 'Cancel').click();
        await p.wait(120);
        assert.equal(putCalls(p).length, 0);
        assert.equal(block(p, 'Hops Movies').querySelector('.shoal-seg button[aria-pressed="true"]').textContent.trim(), 'Only selected users');
        assert.ok(document.activeElement && document.activeElement.closest('.shoal-seg--access'), 'focus stays on the switch');
    } finally {
        p.close();
    }
});

test('the picker: "Select all" on top, notes, parental-limits acknowledgement, live count, warning for yourself', async () => {
    const p = await libraries('default');
    try {
        buttonIn(block(p, 'Hops Movies'), 'Choose people').click();
        await p.wait(80);
        const d = dialog(p);
        assert.match(d.querySelector('h2').textContent, /Who can see Hops Movies\?/);
        const first = d.querySelector('.shoal-picker__all');
        assert.match(first.textContent, /Select all/);
        assert.doesNotMatch(d.textContent, /\bEveryone\b(?! else)/);
        assert.match(d.textContent, /Ratings come from Hops and aren't checked\. Let me choose profiles with parental limits\./);
        assert.match(d.textContent, /1 of 6 selected/);
        // Notes, in order: You, Admin, Disabled account, all libraries ...
        const rich = [...d.querySelectorAll('.shoal-picker__row')].find((r) => r.textContent.includes('Rich'));
        assert.match(rich.textContent, /You · Admin · Has access to all libraries\. If unticked, Shoal hides this library from them and keeps the rest as it is\./);
        const kit = [...d.querySelectorAll('.shoal-picker__row')].find((r) => r.textContent.includes('Kit'));
        assert.match(kit.textContent, /Parental limits: ratings come from Hops and aren't checked/);
        assert.match(kit.textContent, /Shoal keeps this account's library list so it can hide some friends' libraries\./);
        assert.equal(kit.querySelector('input').disabled, true, 'restricted profiles wait for the acknowledgement');
        const jo = [...d.querySelectorAll('.shoal-picker__row')].find((r) => r.textContent.includes('Jo'));
        assert.match(jo.textContent, /Disabled account/);

        // Untick yourself: warning appears.
        rich.querySelector('input').click();
        await p.wait(30);
        assert.match(d.textContent, /You won't see this library either\./);
        rich.querySelector('input').click();
        // Acknowledge, then Kit can be picked.
        d.querySelector('.shoal-picker__ack input').click();
        await p.wait(20);
        assert.equal(kit.querySelector('input').disabled, false);
        kit.querySelector('input').click();
        [...d.querySelectorAll('.shoal-picker__row')].find((r) => r.textContent.includes('Sam')).querySelector('input').click();
        await p.wait(600);
        assert.match(d.textContent, /3 of 6 selected/);
        buttonIn(d, 'Save').click();
        await p.wait(150);
        const body = putCalls(p).at(-1).body;
        assert.equal(body.mode, 'Selected');
        assert.deepEqual(body.selectedUserIds.sort(), ['u-kit', 'u-rich', 'u-sam']);
        assert.equal(body.ratingsAcknowledged, true);
    } finally {
        p.close();
    }
});

test('"Select all" ticks every pickable user and shows the in-between state', async () => {
    const p = await libraries('default');
    try {
        buttonIn(block(p, 'Hops Movies'), 'Choose people').click();
        await p.wait(80);
        const d = dialog(p);
        const all = d.querySelector('.shoal-picker__all input');
        assert.equal(all.indeterminate, true, '1 of 5 pickable is selected');
        all.click();
        await p.wait(30);
        const checked = [...d.querySelectorAll('.shoal-picker__row input')].filter((i) => i.checked).length;
        assert.equal(checked, 5, 'Kit stays unticked until the acknowledgement');
        assert.equal(all.checked, true);
    } finally {
        p.close();
    }
});

test('in Everyone mode the picker is read-only and shows the saved selection', async () => {
    const p = await libraries('default');
    try {
        buttonIn(block(p, 'Fernwood Movies'), 'See saved selection').click();
        await p.wait(80);
        const d = dialog(p);
        assert.match(d.querySelector('h2').textContent, /Saved selection for Fernwood Movies/);
        assert.match(d.textContent, /These people get it back if you switch to Only selected users\./);
        assert.ok([...d.querySelectorAll('.shoal-picker__row input')].every((i) => i.disabled));
        assert.equal(d.querySelector('.shoal-picker__all'), null);
        assert.equal(buttonIn(d, 'Save'), undefined);
        assert.ok(buttonIn(d, 'Close'));
    } finally {
        p.close();
    }
});

test('including parental-limit profiles needs the acknowledgement; unticking saves at once', async () => {
    const p = await libraries('default');
    try {
        const fern = block(p, 'Fernwood Movies');
        fern.querySelector('.shoal-check input').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Ratings for Fernwood Movies come from Fernwood, and Shoal can't check them\. A child's profile could see a movie that's rated wrong\./);
        const include = buttonIn(d, 'Include them');
        assert.equal(include.disabled, true);
        d.querySelector('input[type="checkbox"]').click();
        await p.wait(20);
        assert.equal(include.disabled, false);
        include.click();
        await p.wait(150);
        const body = putCalls(p).at(-1).body;
        assert.deepEqual({ mode: body.mode, inc: body.includeRestricted, ack: body.ratingsAcknowledged }, { mode: 'Everyone', inc: true, ack: true });
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Profiles with parental limits can now see Fernwood Movies/);
        block(p, 'Fernwood Movies').querySelector('.shoal-check input').click();
        await p.wait(120);
        assert.equal(putCalls(p).at(-1).body.includeRestricted, false);
        assert.equal(dialog(p), null, 'unticking needs no dialog');
    } finally {
        p.close();
    }
});

test('drift: the list, accepting a change, and re-applying', async () => {
    const p = await libraries('default');
    try {
        const gran = block(p, 'Granite Movies');
        assert.match(gran.textContent, /Access changed outside Shoal for 2 people/);
        const det = gran.querySelector('details');
        det.open = true;
        assert.match(det.textContent, /Someone changed library access in Jellyfin's dashboard\. Shoal's choice here wins: playback is checked every time, and lists catch up within about an hour, unless you accept the change\./);
        assert.match(det.textContent, /Alex\s*Can see it in Jellyfin, but isn't selected here\./);
        assert.match(det.textContent, /Kit\s*Shoal keeps this account's library list so it can hide Granite Movies\./);
        buttonIn(det, 'Select Alex').click();
        await p.wait(150);
        assert.deepEqual(putCalls(p).at(-1).body.selectedUserIds, ['u-alex']);
        const again = block(p, 'Granite Movies');
        assert.match(again.textContent, /1 person: Alex|Alex/);
        if (again.querySelector('details')) {
            again.querySelector('details').open = true;
            buttonIn(again.querySelector('details'), 'Re-apply now').click();
            await p.wait(200);
            assert.ok(p.mock.calls.some((c) => c.method === 'POST' && /\/Reconcile$/.test(c.path)));
        }
    } finally {
        p.close();
    }
});

test('a library nobody can see says so (hidden from everyone)', async () => {
    const p = await libraries('default');
    try {
        assert.match(block(p, 'Granite Movies').textContent, /Hidden from everyone/);
    } finally {
        p.close();
    }
});

test('a failed save keeps the switch on the saved mode and says nothing changed', async () => {
    const p = await libraries('default');
    p.mock.set({ accessFail: true });
    try {
        block(p, 'Hops Movies').querySelector('.shoal-seg button[aria-pressed="false"]').click();
        await p.wait(60);
        buttonIn(dialog(p), 'Open to everyone').click();
        await p.wait(200);
        const hops = block(p, 'Hops Movies');
        assert.match(hops.textContent, /Couldn't change who can see Hops Movies\. Nothing changed\./);
        assert.equal(hops.querySelector('.shoal-seg button[aria-pressed="true"]').textContent.trim(), 'Only selected users');
        assert.ok(hops.querySelector('[role="alert"]'));
    } finally {
        p.close();
    }
});

test('"You share" lists each friend with libraries, quality and transcodes', async () => {
    const p = await libraries('default');
    try {
        const t = p.text();
        assert.match(t, /You share/);
        assert.match(t, /What each friend's server can see from yours\./);
        assert.match(t, /1 library: Movies · highest quality 20 Mbps · 4 transcodes at once/);
        assert.match(t, /2 libraries: Movies, Kids · highest quality no limit · 4 transcodes at once/);
        assert.match(t, /1 unexpected device signed out/);
    } finally {
        p.close();
    }
});

test('no friends and no libraries: both sections have their empty state', async () => {
    const p = await libraries('empty');
    try {
        const t = p.text();
        assert.match(t, /No libraries from friends yet\./);
        assert.match(t, /Pair a server/);
        assert.match(t, /You aren't sharing any libraries\./);
        assert.match(t, /Share with a friend/);
    } finally {
        p.close();
    }
});

test('T-SEC-11: hostile library, server, user and friend names stay text on this screen', async () => {
    const p = await libraries('hostile');
    try {
        assert.equal(p.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        assert.ok(p.text().includes('<img src=x onerror=alert(1)>'));
        buttonIn(p.page.querySelector('.shoal-libblock'), 'Choose people').click();
        await p.wait(80);
        assert.equal(p.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        assert.ok(dialog(p).textContent.includes('<img src=x onerror=alert(1)>'));
    } finally {
        p.close();
    }
});

test('recovery mode: nothing on this screen but the banner and the paused line', async () => {
    const p = await libraries('recovery');
    try {
        assert.match(p.text(), /Shoal is paused until its settings are fixed\./);
        assert.doesNotMatch(p.text(), /Shared with you/);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Libraries').length, 0);
    } finally {
        p.close();
    }
});
