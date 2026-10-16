// K34 to K40 in the admin UI: TV lines in the overlap, held series released one by one, the ping that was throttled, the TV switches and the TV picker.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock } from './helpers.mjs';

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));

async function page(params, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params, settleMs: 300, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

test('the overlap report and the server page carry the TV line, and leave it out when the server sends none', async () => {
    let p = await page({ view: 'overlap', id: 'h-hops' }, { tvShows: true });
    try {
        assert.match(p.text(), /TV shows. The counts above are movies and episodes, and each series is counted here. In both libraries: 12 series \(episodes: 310\)\. New from Hops: 8 series \(episodes: 150\)\./);
    } finally {
        p.close();
    }

    p = await page({ view: 'server', id: 'h-hops' }, { tvShows: true });
    try {
        assert.match(p.text(), /TV shows. The counts above are movies and episodes, and each series is counted here. In both libraries: 12 series \(episodes: 310\)/);
    } finally {
        p.close();
    }

    p = await page({ view: 'overlap', id: 'h-hops' });
    try {
        assert.doesNotMatch(p.text(), /TV shows\. In both libraries/);
    } finally {
        p.close();
    }
});

test('held series: a Release button per series sends the series key, and the row goes', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { heldSeries: 2 });
    try {
        const t = p.text();
        assert.match(t, /Held back, by series/);
        assert.match(t, /Removals on hold/);
        assert.match(t, /Andor 2022 \(12 episodes\)/);
        assert.match(t, /Additions on hold/);
        assert.match(t, /Silo 2023 \(20 episodes\)/);
        const row = [...p.page.querySelectorAll('.shoal-holdseries li')].find((li) => li.textContent.includes('Andor'));
        buttonIn(row, 'Remove now').click();
        await p.wait(200);
        const post = calls(p, 'POST', /Holds\/removals\/Release/).at(-1);
        assert.match(post.path, /\?series=sk-1$/);
        assert.doesNotMatch(p.text(), /Andor 2022/);
        assert.match(p.text(), /Severance 2022/);
        const add = [...p.page.querySelectorAll('.shoal-holdseries li')].find((li) => li.textContent.includes('Silo'));
        assert.ok(buttonIn(add, 'Add now'));
        assert.doesNotMatch(add.closest('section').textContent, /[-\u2013\u2014>]/);
    } finally {
        p.close();
    }
});

test('a server with nothing held shows no held series panel', async () => {
    const p = await page({ view: 'server', id: 'h-hops' });
    try {
        assert.doesNotMatch(p.text(), /Held back, by series/);
    } finally {
        p.close();
    }
});

test('a throttled Shoal check is not the same as no Shoal', async () => {
    const p = await page({ view: 'pair', dir: 'watch' }, { pingThrottled: true, hasShoal: false });
    try {
        const set = (label, v) => {
            const f = [...p.page.querySelectorAll('.shoal-field')].find((x) => x.querySelector('label') && x.querySelector('label').textContent.trim() === label).querySelector('input');
            f.value = v;
            f.dispatchEvent(new window.Event('input', { bubbles: true }));
        };
        set('Name for this server', 'Willow');
        set('Server address', 'http://willow.tail1234.ts.net:8096');
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.match(p.text(), /Shoal could not be checked just now because the server limited the check\./);
        assert.doesNotMatch(p.text(), /doesn't seem to have Shoal/);
    } finally {
        p.close();
    }
});

test('Settings: the TV switches are there, the thumbnails follow the main one, and both are saved', async () => {
    const p = await page({ view: 'settings' });
    try {
        const row = (label) => [...p.page.querySelectorAll('label.shoal-check')].find((l) => l.textContent.includes(label)).querySelector('input');
        const tv = row('Show TV series from friends');
        const thumbs = row('Episode pictures');
        assert.equal(tv.checked, false);
        assert.equal(thumbs.disabled, true);
        tv.click();
        assert.equal(thumbs.disabled, false);
        buttonIn(p.page, 'Save').click();
        await p.wait(200);
        const put = calls(p, 'PUT', /^Settings$/).at(-1);
        assert.equal(put.body.tvShowsEnabled, true);
        assert.equal(put.body.tvEpisodeThumbs, true);
    } finally {
        p.close();
    }
});

test('the library picker offers a TV library once TV is on, and says how to turn it on while it is off', async () => {
    let p = await page({ view: 'server', id: 'h-hops' });
    try {
        buttonIn(p.page, 'Add another library').click();
        await p.wait(200);
        assert.match(p.text(), /TV shows are turned off\. Turn them on in Settings, under Extra features\./);
    } finally {
        p.close();
    }

    p = await page({ view: 'server', id: 'h-hops' }, { tvShows: true });
    try {
        buttonIn(p.page, 'Add another library').click();
        await p.wait(200);
        const tv = [...p.page.querySelectorAll('.shoal-check')].find((c) => c.textContent.includes('TV shows')).querySelector('input');
        assert.equal(tv.disabled, false);
    } finally {
        p.close();
    }
});
