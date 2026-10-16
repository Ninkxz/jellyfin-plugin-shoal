// K27: token rotation on the server detail (DESIGN v2.6.1) and the guard's counters (v2.6.3), against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock } from './helpers.mjs';

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

async function server(id, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params: { view: 'server', id }, settleMs: 300, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

const section = (p) => [...p.page.querySelectorAll('section')].find((s) => s.querySelector('h2')?.textContent === 'Server key');

test('a server paired the old way cannot rotate: the panel says to re-pair by invite and offers nothing else', async () => {
    const p = await server('h-legacy', { legacy: true });
    try {
        const s = section(p);
        assert.match(s.textContent, /was paired the old way, so Shoal cannot change its key\. Re-pair it with an invite/);
        assert.equal(s.querySelectorAll('button, select').length, 0);
    } finally {
        p.close();
    }
});

test('an invite pairing shows the last change, the schedule and the button', async () => {
    const p = await server('h-fern');
    try {
        const s = section(p);
        assert.match(s.textContent, /Key last changed/);
        assert.match(s.textContent, /Key last changed3 Oct/);
        assert.match(s.textContent, /Not scheduled/);
        const options = [...s.querySelectorAll('option')].map((o) => o.textContent);
        assert.deepEqual(options, ['Never (change it by hand)', 'Every 30 days', 'Every 60 days', 'Every 90 days']);
        assert.ok(buttonIn(s, 'Change the key now'));
    } finally {
        p.close();
    }
});

test('the schedule is saved the moment it is picked, and the next change is shown', async () => {
    const p = await server('h-fern');
    try {
        const select = section(p).querySelector('select');
        setValue(select, '30');
        await p.wait(250);
        const put = calls(p, 'PUT', /Peers\/h-fern\/Rotation$/);
        assert.equal(put.length, 1);
        assert.deepEqual(put[0].body, { days: 30 });
        const s = section(p);
        assert.doesNotMatch(s.textContent, /Not scheduled/);
        assert.equal(s.querySelector('select').value, '30');
        assert.ok([...s.querySelectorAll('[role="status"]')].some((n) => n.textContent.trim() === 'Saved' && !n.hidden), 'a visible Saved note');
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Key schedule saved/);
    } finally {
        p.close();
    }
});

test('Change the key now asks first, then rotates, and the 10 minute limit shows as a wait', async () => {
    const p = await server('h-fern');
    try {
        buttonIn(section(p), 'Change the key now').click();
        await p.wait(80);
        assert.match(dialog(p).textContent, /Change Fernwood's key now\?/);
        buttonIn(dialog(p), 'Cancel').click();
        await p.wait(60);
        assert.equal(calls(p, 'POST', /Rotate$/).length, 0);

        buttonIn(section(p), 'Change the key now').click();
        await p.wait(80);
        buttonIn(dialog(p), 'Change the key').click();
        await p.wait(300);
        assert.equal(calls(p, 'POST', /Peers\/h-fern\/Rotate$/).length, 1);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Changed Fernwood's key/);
        assert.match(section(p).textContent, /Available again in/);
        assert.equal(buttonIn(section(p), 'Change the key now').getAttribute('aria-disabled'), 'true');

        // A second click while the wait runs sends nothing.
        buttonIn(section(p), 'Change the key now').click();
        await p.wait(100);
        assert.equal(calls(p, 'POST', /Rotate$/).length, 1);
    } finally {
        p.close();
    }
});

for (const [refusal, status, words] of [
    ['streamsActive', 409, /Someone is watching from this server/],
    ['notOnline', 409, /not online right now/],
    ['busy', 409, /already running/],
    ['rejected', 502, /refused to give a new key/],
    ['lostResponse', 502, /new key never arrived/],
    ['badResponse', 502, /did not trust/]
]) {
    test(`a ${status} ${refusal} is said in plain words and changes nothing`, async () => {
        const p = await server('h-fern', status === 409 ? { rotateRefusal: refusal } : { rotateFailure: refusal });
        try {
            buttonIn(section(p), 'Change the key now').click();
            await p.wait(80);
            buttonIn(dialog(p), 'Change the key').click();
            await p.wait(300);
            assert.match(p.page.querySelector('[data-slot="alert"]').textContent, words);
            assert.doesNotMatch(p.text(), /\bnull\b|undefined|rotateErr_/);
        } finally {
            p.close();
        }
    });
}

test('alerts: a failed rotation, an unsaved new key, and an overdue schedule', async () => {
    const failed = await server('h-fern', { rotationError: 'lostResponse' });
    try {
        const t = failed.text();
        assert.match(t, /Changing Fernwood's key failed/);
        assert.match(t, /answer never arrived/);
        assert.ok(buttonIn(failed.page, 'Pair again'));
    } finally {
        failed.close();
    }

    const stored = await server('h-fern', { rotationError: 'storeFailed' });
    try {
        assert.match(stored.text(), /Fernwood's new key is not saved/);
        assert.match(stored.text(), /After Shoal restarts it would be lost/);
    } finally {
        stored.close();
    }

    const overdue = await server('h-fern', { rotationOverdue: true });
    try {
        assert.match(overdue.text(), /Fernwood's key change is overdue/);
        const go = buttonIn(overdue.page, 'Go to key settings');
        assert.ok(go, 'the button says where it goes');
        assert.equal(calls(overdue, 'POST', /Rotate$/).length, 0);
        assert.equal(buttonIn(overdue.page, 'Change the key now') !== undefined, true); // the real button is in the panel, and it asks first
    } finally {
        overdue.close();
    }

    const both = await server('h-fern', { rotationOverdue: true, rotationError: 'lostResponse' });
    try {
        assert.match(both.text(), /Changing Fernwood's key failed/);
        assert.doesNotMatch(both.text(), /overdue/, 'a failed change and an overdue one do not both show');
    } finally {
        both.close();
    }

    const fine = await server('h-fern');
    try {
        assert.doesNotMatch(fine.text(), /key failed|not saved|overdue/);
    } finally {
        fine.close();
    }
});

test('a friend whose key could not be changed says so on the friend page', async () => {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage({ scenario: 'default', params: { view: 'friend', id: 'f-sam' }, settleMs: 300, now: clock.now, shareRotationFailed: true });
    try {
        assert.match(p.text(), /Changing Sam's key failed/);
        assert.match(p.text(), /Create a new invite/);
    } finally {
        p.close();
    }

    const q = await mountPage({ scenario: 'default', params: { view: 'friend', id: 'f-sam' }, settleMs: 300, now: clock.now, shareRotatedDaysAgo: 3 });
    try {
        assert.match(q.text(), /Key last changed/);
        assert.doesNotMatch(q.text(), /key failed/);
    } finally {
        q.close();
    }
});

test('the guard counters sit under Troubleshooting-style protection in Settings, as a table of words and numbers', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, interceptHits: 3 });
    try {
        const text = p.text();
        assert.match(text, /What Shoal turned away/);
        const table = [...p.page.querySelectorAll('table')].find((tb) => /Video requests that were not direct plays/.test(tb.textContent));
        assert.ok(table);
        const rows = [...table.querySelectorAll('tbody tr')].map((r) => [...r.children].map((c) => c.textContent.trim()));
        assert.equal(rows.length, 7);
        assert.deepEqual(rows[0], ['Video requests that were not direct plays', '3']);
        assert.ok(rows.some((r) => r[0] === 'Preview-thumbnail playlists' && r[1] === '2'));
        assert.doesNotMatch(table.textContent, /\bnull\b|nonstatic-stream|counter_/);
    } finally {
        p.close();
    }
});
