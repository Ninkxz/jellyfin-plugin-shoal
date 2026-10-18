// K27: two-way friends (DESIGN v2.6.2: T-TWO-1 to T-TWO-4 on the page side), against the mock admin API.
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

async function page(params, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params, settleMs: 300, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

// U-A1 (owner decision 7): the two lanes replace the two-way card. A linked friend is named in each lane, each half with its own chip.
const serverRow = (p, name) => [...p.page.querySelectorAll('[data-slot="cards"] > .shoal-lanerow')].find((r) => r.querySelector('.shoal-row__nametext').textContent === name);
const friendRow = (p, name) => [...p.page.querySelectorAll('[data-slot="friends"] > .shoal-lanerow')].find((r) => r.querySelector('.shoal-row__nametext').textContent === name);

test('T-TWO-1 (v2): a linked friend shows in both lanes and each row says the other half exists; unlinked friends stay as they were', async () => {
    const p = await page({ view: 'overview' });
    try {
        assert.equal(p.page.querySelectorAll('.shoal-twoway, .shoal-twoway__card').length, 0, 'no separate two-way card');
        assert.match(serverRow(p, 'Hops').textContent, /Also watches your server/);
        assert.match(serverRow(p, 'Hops').textContent, /1,204 titles/);
        assert.doesNotMatch(serverRow(p, 'Fernwood').textContent, /Also watches your server/);
        assert.match(friendRow(p, 'Sam').textContent, /Also your server: Hops/);
        assert.doesNotMatch(friendRow(p, 'Jo').textContent, /Also your server/);
    } finally {
        p.close();
    }
});

test('T-TWO-2 (v2): each half keeps its own state in its own lane', async () => {
    const chips = async (extra) => {
        const p = await page({ view: 'overview' }, extra);
        try {
            return [serverRow(p, 'Hops').querySelector('.shoal-chip').textContent.trim(), friendRow(p, 'Sam').querySelector('.shoal-chip').textContent.trim()];
        } finally {
            p.close();
        }
    };

    assert.deepEqual(await chips({}), ['Online', 'Active']);
    assert.deepEqual(await chips({ twoWayShare: 'Paused' }), ['Online', 'Paused']);
    assert.deepEqual(await chips({ twoWayShare: 'AccessRemoved' }), ['Online', 'Access removed']);
    assert.deepEqual(await chips({ twoWayServer: 'Offline', twoWayShare: 'Paused' }), ['Offline', 'Paused']);
    assert.deepEqual(await chips({ twoWayServer: 'Degraded' }), ['Online, unstable', 'Active']);
});

test('T-TWO-2: the roll-up order, worst first', async () => {
    const order = ['AuthFailed', 'IdentityMismatch', 'TlsKeyChanged', 'PairingAborted', 'Offline', 'Degraded', 'Paused', 'Online'];
    const p = await page({ view: 'overview' });
    try {
        const mod = await import('./helpers.mjs');
        const shoal = await mod.loadShoal();
        // The page maps every word of the order to a chip; none falls back to "Unknown".
        for (const word of order) {
            const m = shoal.describeLinkStatus(word);
            assert.notEqual(m.text, 'Unknown', word);
        }

        // One state, one word: a changed certificate reads the same here as on the server card.
        assert.equal(shoal.describeLinkStatus('TlsKeyChanged').text, shoal.describeLinkStatus('IdentityMismatch').text);

        assert.equal(shoal.describeLinkStatus('SomethingNew').text, 'Unknown');
    } finally {
        p.close();
    }
});

test('with no link there is no "also" note in either lane', async () => {
    const p = await page({ view: 'overview' }, { twoWay: false });
    try {
        assert.doesNotMatch(p.text(), /Also your server|Also watches your server/);
    } finally {
        p.close();
    }
});

test('server detail: the panel for the friend who also watches, and Pause and Remove say they only change watching', async () => {
    const p = await page({ view: 'server', id: 'h-hops' });
    try {
        const t = p.text();
        assert.match(t, /Hops also watches your libraries/);
        assert.match(t, /1 library · 1 device/);
        assert.doesNotMatch(t, /\bnull\b/);
        assert.match(t, /Hops will still watch your libraries\./); // under Pause
        buttonIn(p.page, 'Remove server…').click();
        await p.wait(80);
        assert.match(dialog(p).textContent, /Hops will still watch your libraries\./);
        const open = [...p.page.querySelectorAll('a')].find((a) => /Open Hops as a friend/.test(a.textContent));
        assert.ok(open, 'a link to the friend');
    } finally {
        p.close();
    }
});

test('server detail of a server nobody watches back has no such panel', async () => {
    const p = await page({ view: 'server', id: 'h-fern' });
    try {
        assert.doesNotMatch(p.text(), /also watches your libraries|will still watch/);
    } finally {
        p.close();
    }
});

test('friend detail: "You also watch" with the server, and Stop sharing says you keep watching', async () => {
    const p = await page({ view: 'friend', id: 'f-sam' });
    try {
        const t = p.text();
        assert.match(t, /You also watch Hops/);
        assert.match(t, /1,204 titles/);
        assert.doesNotMatch(t, /Online · Online/); // the status is said once
        buttonIn(p.page, 'Stop sharing…').click();
        await p.wait(80);
        assert.match(dialog(p).textContent, /You will still watch Hops's libraries\./);
    } finally {
        p.close();
    }
});

test('friend detail of a friend you do not watch has no such panel', async () => {
    const p = await page({ view: 'friend', id: 'f-jo' });
    try {
        assert.doesNotMatch(p.text(), /You also watch/);
    } finally {
        p.close();
    }
});

test('T-TWO-4: a library that comes from a friend is listed in the share wizard and cannot be picked', async () => {
    const p = await page({ view: 'pair', dir: 'share' });
    try {
        await p.wait(300);
        const row = [...p.page.querySelectorAll('label.shoal-check')].find((r) => /Hops Movies/.test(r.textContent));
        assert.ok(row, 'the library is listed');
        assert.equal(row.querySelector('input').disabled, true);
        const kids = [...p.page.querySelectorAll('label.shoal-check')].find((r) => /Kids/.test(r.textContent));
        assert.equal(kids.querySelector('input').disabled, false);
        assert.match(p.text(), /Libraries from friends' servers can't be shared on\./);
    } finally {
        p.close();
    }
});

test('the share wizard offers the servers you already watch as a prefill for the friend name', async () => {
    const p = await page({ view: 'pair', dir: 'share' });
    try {
        assert.match(p.text(), /Use the name of a server you watch:/);
        const pick = buttonIn(p.page, 'Hops');
        assert.ok(pick);
        pick.click();
        const name = [...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label')?.textContent.trim() === 'Friend\'s name').querySelector('input');
        assert.equal(name.value, 'Hops');
    } finally {
        p.close();
    }
});

test('the watch wizard says when the friend already watches you', async () => {
    const p = await page({ view: 'pair', dir: 'watch' });
    try {
        setValue([...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label')?.textContent.trim() === 'Name for this server').querySelector('input'), 'Sam');
        setValue([...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label')?.textContent.trim() === 'Server address').querySelector('input'), 'http://sam.tail1234.ts.net:8096');
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(200);
        assert.match(p.text(), /You already share with Sam Jellyfin; this adds the other direction\./);
    } finally {
        p.close();
    }

    const other = await page({ view: 'pair', dir: 'watch' });
    try {
        setValue([...other.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label')?.textContent.trim() === 'Name for this server').querySelector('input'), 'Willow');
        setValue([...other.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label')?.textContent.trim() === 'Server address').querySelector('input'), 'http://willow.tail1234.ts.net:8096');
        other.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await other.wait(200);
        assert.doesNotMatch(other.text(), /already share with/);
    } finally {
        other.close();
    }
});

test('a hostile friend or server name is only ever text in both lanes', async () => {
    const p = await page({ view: 'overview' }, { scenario: 'hostile' });
    try {
        for (const sel of ['[data-slot="cards"]', '[data-slot="friends"]']) {
            const host = p.page.querySelector(sel);
            assert.equal(host.querySelectorAll('script, [onerror], [onload]').length, 0);
            assert.ok(host.textContent.includes('<'));
        }
    } finally {
        p.close();
    }
});
