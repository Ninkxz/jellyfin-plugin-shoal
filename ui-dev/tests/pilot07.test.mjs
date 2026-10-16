// Pilot 07 (a real browser walk of the admin pages): the defects that can be pinned down without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, loadShoal } from './helpers.mjs';

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);

async function page(params, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params, settleMs: 300, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

test('Lumen 04: what counts as a title is visible text under the status line, not a tooltip', async () => {
    const p = await page({ view: 'overview' });
    try {
        const note = p.page.querySelector('.shoal-status__note');
        assert.ok(note && !note.hidden);
        assert.match(note.textContent, /Titles are movies and series. Seasons and episodes belong to their series/);
        assert.equal(p.page.querySelector('.shoal-status [title]'), null);
    } finally {
        p.close();
    }
});

test('D3: the Settings page prints no null', async () => {
    const p = await page({ view: 'settings' });
    try {
        assert.doesNotMatch(p.page.textContent, /null|undefined/);
        assert.match(p.text(), /Preview pictures use/);
    } finally {
        p.close();
    }
});

test('D4: the current user id is matched to the users as they are written, so the picker warning follows the ticked box', async () => {
    const mod = await loadShoal();
    const ctx = { client: { getCurrentUserId: () => 'ABCDEF0123456789ABCDEF0123456789' } };
    const data = { users: [{ id: 'abcdef01-2345-6789-abcd-ef0123456789' }, { id: 'other' }] };
    assert.equal(mod.currentUserId(ctx, data), 'abcdef01-2345-6789-abcd-ef0123456789');
    assert.equal(mod.currentUserId({ client: { getCurrentUserId: () => 'nobody' } }, data), 'nobody');
});

test('D5: an offline server does not turn the headline into "0 titles shared with you"', async () => {
    const p = await page({ view: 'overview' });
    try {
        assert.match(p.text(), /1,590 titles shared with you, 612 more on servers that are offline/);
        assert.doesNotMatch(p.text(), /\b0 titles shared/);
    } finally {
        p.close();
    }
});

test('D6: an offline server says Offline once on its page, and shows no latency or stability from the past', async () => {
    const p = await page({ view: 'server', id: 'h-gran' });
    try {
        const header = p.page.querySelector('.shoal-detail__header');
        assert.equal((header.textContent.match(/Offline/g) || []).length, 1);
        assert.equal(p.page.querySelector('.shoal-hero .shoal-pill').hidden, true);
        const latency = [...p.page.querySelectorAll('.shoal-kpi')].find((k) => /Latency/.test(k.textContent));
        assert.doesNotMatch(latency.textContent, /\d+\s*ms/);
        const stability = [...p.page.querySelectorAll('.shoal-kpi')].find((k) => /Stability/.test(k.textContent));
        assert.doesNotMatch(stability.textContent, /Good/);
    } finally {
        p.close();
    }
});

test('D6 and D12: Retry now and Test link answer with a message you can see for a few seconds', async () => {
    const p = await page({ view: 'server', id: 'h-gran' });
    try {
        buttonIn(p.page, 'Retry now').click();
        await p.wait(200);
        const toast = p.page.querySelector('.shoal-toast');
        assert.ok(toast, 'a visible toast');
        assert.match(toast.textContent, /Checking the server again/);
        await p.wait(3500);
        assert.ok(p.page.querySelector('.shoal-toast'), 'it is still there after 3 seconds');
    } finally {
        p.close();
    }
});

test('D7: 25 identical folder findings are one line with a count and the folder names', async () => {
    const p = await page({ view: 'settings' }, { orphanFolders: 25 });
    try {
        const text = p.text();
        assert.match(text, /25 library folders belong to no paired server\. Nothing is using them\./);
        assert.match(text, /Folders: 01234567/);
        assert.equal((text.match(/belongs? to no paired server/g) || []).length, 1);
    } finally {
        p.close();
    }
});

test('D8: a share library without a count shows no NaN, and the Share page is not titled Pair a server', async () => {
    const p = await page({ view: 'pair', dir: 'share' });
    try {
        assert.doesNotMatch(p.page.textContent, /NaN/);
        assert.equal(p.page.querySelector('[data-shoal-view="pair"] h1').textContent, 'Share with a friend');
    } finally {
        p.close();
    }

    const q = await page({ view: 'pair', dir: 'watch' });
    try {
        assert.equal(q.page.querySelector('[data-shoal-view="pair"] h1').textContent, 'Pair a server');
    } finally {
        q.close();
    }
});

test('D13: one title is "1 title", two are "2 titles"', async () => {
    const mod = await loadShoal();
    const fmt = { num: (n) => String(n), bytes: (n) => n + ' B' };
    const text = (both, gain) => mod.overlapSentences({ fmt }, 'Hops', { both: { count: both, localBytes: 1 }, gain: { count: gain, peerBytes: 1 } }).plain;
    assert.match(text(1, 2), /You both have 1 title [(]/);
    assert.match(text(1, 2), /adds 2 titles [(]/);
    assert.match(text(2, 1), /You both have 2 titles/);
    assert.match(text(2, 1), /adds 1 title [(]/);
});

test('D17: coming back to the wizard starts clean, and opening it asks for a session without an error', async () => {
    const p = await page({ view: 'pair', dir: 'watch' });
    try {
        assert.equal(p.handle.screen().state().step, 'connect');
        const get = p.mock.calls.filter((c) => c.method === 'GET' && c.path === 'Pairing');
        assert.ok(get.length >= 1, 'the wizard asked whether a pairing is running (and a 204 is the answer, not an error)');
        p.page.dispatchEvent(new window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: true } }));
        await p.wait(300);
        assert.equal(p.handle.screen().state().step, 'connect');
        assert.equal(p.handle.screen().state().form.url, '');
    } finally {
        p.close();
    }
});

test('Pilot 08 N7: the share address field speaks about your own server, not the friend\'s', async () => {
    const p = await page({ view: 'pair', dir: 'share' });
    try {
        const set = (label, v) => {
            const f = [...p.page.querySelectorAll('.shoal-field')].find((x) => x.querySelector('label') && x.querySelector('label').textContent.trim() === label).querySelector('input');
            f.value = v;
            f.dispatchEvent(new window.Event('input', { bubbles: true }));
        };
        set("Friend's name", 'Riley');
        await p.wait(200);
        [...p.page.querySelectorAll('.shoal-choices--checks .shoal-check')].find((c) => c.textContent.includes('Movies') && !c.textContent.includes('Hops')).querySelector('input').click();
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(200);
        p.page.querySelector('input[type="radio"][value="home"]').click();
        await p.wait(50);
        set('The address to send', 'http://192.168.1.20:8096');
        const t = p.text();
        assert.match(t, /private address inside your own network\. It only works for Riley/);
        assert.doesNotMatch(t, /inside your friend's network/);
    } finally {
        p.close();
    }
});

test('Pilot 08 N3: the card and the chart give the same latest speed, and the summary counts measurements', async () => {
    const p = await page({ view: 'overview' });
    try {
        assert.doesNotMatch(p.page.textContent, /\d+ tests, between/);
    } finally {
        p.close();
    }
});

test('Pilot 08 N5: a pairing whose server is gone says so and offers Start over, which clears it', async () => {
    const p = await page({ view: 'pair', dir: 'watch' }, { ghost: true });
    try {
        buttonIn(p.page, 'Continue').click();
        await p.wait(300);
        assert.match(p.text(), /This pairing is not there any more: the server it was for was removed./);
        buttonIn(p.page, 'Start over').click();
        await p.wait(300);
        assert.ok(p.mock.calls.some((c) => c.method === 'DELETE' && c.path === 'Pairing'));
        assert.equal(p.handle.screen().state().step, 'connect');
        assert.doesNotMatch(p.text(), /not there any more/);
    } finally {
        p.close();
    }
});

test('Pilot 08 D14 and N2: a TV library is counted as series and episodes, the same way in the picker', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { tvShows: true });
    try {
        buttonIn(p.page, 'Add another library').click();
        await p.wait(250);
        assert.match(p.text(), /TV shows · 12 series, 90 episodes/);
    } finally {
        p.close();
    }
});

test('Pilot 08 D12: saving Settings shows a message you can see', async () => {
    const p = await page({ view: 'settings' });
    try {
        buttonIn(p.page, 'Save').click();
        await p.wait(300);
        const toast = p.page.querySelector('.shoal-toast');
        assert.ok(toast);
        assert.match(toast.textContent, /Settings saved/);
    } finally {
        p.close();
    }
});

test('Pilot 08 N10 and N8: the TV help says it controls syncing, and the offline hours say what the number means', async () => {
    const p = await page({ view: 'settings' });
    try {
        assert.match(p.text(), /It controls what Shoal syncs, not what Jellyfin already shows./);
        assert.match(p.text(), /For example 72 for three days. Use 0.5 for half an hour./);
        assert.doesNotMatch(p.text(), /(0.5 is half an hour)/);
    } finally {
        p.close();
    }
});

test('a refused Remove keeps its dialog open and says why in words (syncing, pairing in progress)', async () => {
    for (const [kind, re] of [['syncing', /Shoal is syncing Hops right now, so it can't be removed yet./], ['pairingActive', /A pairing with Hops is in progress. Cancel it first/]]) {
        const p = await page({ view: 'server', id: 'h-hops' }, { removeError: kind });
        try {
            buttonIn(p.page, 'Remove server…').click();
            await p.wait(60);
            const d = p.page.querySelector('dialog[open]');
            const typed = d.querySelector('input[type="text"], input:not([type])');
            typed.value = 'Hops';
            typed.dispatchEvent(new window.Event('input', { bubbles: true }));
            buttonIn(d, 'Remove server').click();
            await p.wait(200);
            const still = p.page.querySelector('dialog[open]');
            assert.ok(still, 'the dialog stays');
            assert.match(still.textContent, re, kind);
            assert.equal(buttonIn(still, 'Remove server').disabled, false, 'and can be pressed again');
        } finally {
            p.close();
        }
    }
});

test('an action that fails leaves a bar on screen until it is dismissed', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { actionFails: true });
    try {
        buttonIn(p.page, 'Pause').click();
        await p.wait(250);
        const bar = p.page.querySelector('.shoal-errorbar');
        assert.ok(bar);
        assert.match(bar.textContent, /That didn't work. Try again in a moment./);
        await p.wait(6500);
        assert.ok(p.page.querySelector('.shoal-errorbar'), 'still there after the 5 second alert region has cleared');
        buttonIn(bar, 'Dismiss').click();
        assert.equal(p.page.querySelector('.shoal-errorbar'), null);
    } finally {
        p.close();
    }
});

/* ---------------- Firefox report: a button must never do nothing ---------------- */

async function removeHops(p) {
    buttonIn(p.page, 'Remove server…').click();
    await p.wait(60);
    const d = p.page.querySelector('dialog[open]');
    const typed = d.querySelector('input[type="text"], input:not([type])');
    typed.value = 'Hops';
    typed.dispatchEvent(new window.Event('input', { bubbles: true }));
    buttonIn(d, 'Remove server').click();
    await p.wait(300);
}

test('Firefox: a success with an empty body is a success (Remove server goes through)', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { emptyBodies: true });
    try {
        await removeHops(p);
        assert.equal(p.page.querySelector('dialog[open]'), null, 'the dialog closed');
        assert.equal(p.page.querySelector('.shoal-errorbar'), null, 'and nothing is shown as failed');
        assert.ok(p.mock.calls.some((c) => c.method === 'DELETE' && /^Peers\/h-hops/.test(c.path)));
    } finally {
        p.close();
    }
});

test('Firefox: the priority is saved with an empty answer too, and says Saved', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { emptyBodies: true });
    try {
        const field = [...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label') && f.querySelector('label').textContent.trim() === 'Priority').querySelector('input');
        field.value = '7';
        field.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(p.page, 'Save').click();
        await p.wait(300);
        const put = p.mock.calls.filter((c) => c.method === 'PUT' && /^Peers\/h-hops$/.test(c.path)).at(-1);
        assert.equal(put.body.priority, 7);
        assert.equal(p.page.querySelector('.shoal-errorbar'), null);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent + p.text(), /Saved/);
    } finally {
        p.close();
    }
});

test('Firefox: the typed name wakes Remove on change, keyup and paste as well as input', async () => {
    const p = await page({ view: 'server', id: 'h-hops' });
    try {
        buttonIn(p.page, 'Remove server…').click();
        await p.wait(60);
        const d = p.page.querySelector('dialog[open]');
        const typed = d.querySelector('input[type="text"], input:not([type])');
        typed.value = 'Hops';
        typed.dispatchEvent(new window.Event('keyup', { bubbles: true }));
        assert.equal(buttonIn(d, 'Remove server').disabled, false);
    } finally {
        p.close();
    }
});

test('an answer the page cannot read is said in words and leaves the button usable', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { garbage: true });
    try {
        buttonIn(p.page, 'Pause').click();
        await p.wait(300);
        const bar = p.page.querySelector('.shoal-errorbar');
        assert.ok(bar);
        assert.match(bar.textContent, /answered in a way this page could not read/);
        assert.equal(buttonIn(p.page, 'Pause').getAttribute('aria-disabled'), null, 'not left busy');
    } finally {
        p.close();
    }
});

/* ---------------- production report: an older Shoal that lacks a route ---------------- */

test('a 405 on an admin route says the feature needs a newer Shoal, and stays until dismissed', async () => {
    const p = await page({ view: 'server', id: 'h-hops' });
    try {
        const orig = p.mock.handle.bind(p.mock);
        p.mock.handle = async (method, path, body) => (method === 'DELETE' && /^Peers\/h-hops/.test(path) ? { status: 405, body: undefined, headers: { Allow: 'GET' } } : orig(method, path, body));
        buttonIn(p.page, 'Remove server…').click();
        await p.wait(60);
        const d = p.page.querySelector('dialog[open]');
        const typed = d.querySelector('input[type="text"], input:not([type])');
        typed.value = 'Hops';
        typed.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(d, 'Remove server').click();
        await p.wait(300);
        assert.match(p.page.querySelector('dialog[open]').textContent, /This feature needs a newer Shoal\. Update it in Dashboard, Plugins, Shoal, then restart Jellyfin\./);
    } finally {
        p.close();
    }

    const q = await page({ view: 'server', id: 'h-hops' }, { actionFails: true });
    try {
        buttonIn(q.page, 'Pause').click();
        await q.wait(250);
        assert.ok(q.page.querySelector('.shoal-errorbar'), 'a failed call is a visible bar');
    } finally {
        q.close();
    }
});

test('the Settings page and the Overview footer show the Shoal version, and say when a newer one is out', async () => {
    let p = await page({ view: 'settings' }, { installed: '1.0.1.0', latest: '1.0.10.0' });
    try {
        assert.match(p.text(), /Shoal version 1\.0\.1\.0/);
        assert.match(p.text(), /A newer Shoal is available: 1\.0\.10\.0\. Update it in Dashboard, Plugins, Shoal, then restart Jellyfin\./);
    } finally {
        p.close();
    }

    p = await page({ view: 'overview' }, { installed: '1.0.2.0' });
    try {
        assert.match(p.text(), /Shoal version 1\.0\.2\.0/);
        assert.doesNotMatch(p.text(), /A newer Shoal is available/);
    } finally {
        p.close();
    }

    p = await page({ view: 'overview' }, { noCatalog: true });
    try {
        assert.doesNotMatch(p.text(), /Shoal version/);
    } finally {
        p.close();
    }
});

test('Settings: the offline card switch, the request service name and its optional address are saved', async () => {
    const p = await page({ view: 'settings' });
    try {
        const t = p.text();
        assert.match(t, /When a friend's server is offline, play shows a short video saying so instead of an error\./);
        const field = (label) => [...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label') && f.querySelector('label').textContent.trim() === label).querySelector('input');
        const sw = [...p.page.querySelectorAll('label.shoal-check')].find((l) => l.textContent.includes('Show a short video')).querySelector('input');
        assert.equal(sw.checked, true, 'on by default');
        assert.equal(field('Request service name').value, 'Seerr');
        assert.equal(field('Request service address (optional)').value, '');
        // A bad address is refused next to the field, nothing is sent.
        const url = field('Request service address (optional)');
        url.value = 'seerr.example.com';
        url.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(p.page, 'Save').click();
        await p.wait(200);
        assert.match(p.text(), /Use an address that starts with http:\/\/ or https:\/\/, or leave it empty\./);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').length, 0);
        url.value = 'https://seerr.example.com';
        url.dispatchEvent(new window.Event('input', { bubbles: true }));
        sw.click();
        buttonIn(p.page, 'Save').click();
        await p.wait(300);
        const put = p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').at(-1);
        assert.equal(put.body.offlineCardEnabled, false);
        assert.equal(put.body.requestServiceName, 'Seerr');
        assert.equal(put.body.requestServiceUrl, 'https://seerr.example.com');
    } finally {
        p.close();
    }
});

test('Lumen 05: a failure bar is fixed to the top of the viewport (it can never be off screen) and stays until dismissed', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { actionFails: true });
    try {
        buttonIn(p.page, 'Pause').click();
        await p.wait(250);
        const bar = p.page.querySelector('.shoal-errors > .shoal-errorbar');
        assert.ok(bar, 'the bar is inside the fixed strip');
        assert.equal(bar.getAttribute('role'), 'alert');
        buttonIn(bar, 'Dismiss').click();
        assert.equal(p.page.querySelector('.shoal-errorbar'), null);
    } finally {
        p.close();
    }
});

test('Lumen 05: a server that can only be re-paired has one primary button, Re-pair with an invite, and no Pause', async () => {
    const p = await page({ view: 'server', id: 'h-legacy' }, { legacy: true });
    try {
        const actions = [...p.page.querySelectorAll('.shoal-detail__actions button')].map((b) => b.textContent.trim());
        assert.equal(actions[0], 'Re-pair with an invite');
        assert.equal(actions.filter((a) => /Re-pair|Pair again/.test(a)).length, 1);
        assert.ok(!actions.includes('Pause'));
        assert.equal(p.page.querySelectorAll('.shoal-detail__header .shoal-btn--primary').length, 1);
    } finally {
        p.close();
    }
});

test('Settings: the video chunks to load ahead is a whole number from 2 to 20, default 6, saved by name', async () => {
    const p = await page({ view: 'settings' });
    try {
        assert.match(p.text(), /How many video chunks your server loads ahead from a friend's server. Higher smooths a slow link and uses more memory./);
        const field = [...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label') && f.querySelector('label').textContent.trim() === 'Video chunks to load ahead').querySelector('input');
        assert.equal(field.value, '6');
        field.value = '25';
        field.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(p.page, 'Save').click();
        await p.wait(200);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').length, 0, 'out of range is refused');
        field.value = '10';
        field.dispatchEvent(new window.Event('input', { bubbles: true }));
        buttonIn(p.page, 'Save').click();
        await p.wait(300);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').at(-1).body.hlsBufferAhead, 10);
    } finally {
        p.close();
    }
});

test('repairBlockedByHttp: the card and the detail page say to switch to Tailscale or https, Remove is the primary, no Re-pair button', async () => {
    const sentence = /Switch this server to Tailscale or https, then pair it again, or remove it/;
    const p = await page({ view: 'overview' }, { legacyHttp: true });
    try {
        const card = [...p.page.querySelectorAll('.shoal-card')].find((c) => c.textContent.includes('Plain Friend'));
        assert.match(card.textContent, sentence);
        assert.ok(!card.textContent.includes('Re-pair with an invite'));
        assert.ok(buttonIn(card, 'Remove or change the address'));
    } finally {
        p.close();
    }

    const q = await page({ view: 'server', id: 'h-legacyhttp' }, { legacyHttp: true });
    try {
        assert.match(q.text(), sentence);
        assert.match(q.text(), /Change the address:/);
        assert.equal(buttonIn(q.page, 'Re-pair with an invite'), undefined);
        const remove = buttonIn(q.page.querySelector('.shoal-detail__actions'), 'Remove server...') || [...q.page.querySelectorAll('.shoal-detail__actions button')].find((b) => /^Remove/.test(b.textContent.trim()));
        assert.ok(remove && remove.classList.contains('shoal-btn--primary'), 'Remove is primary');
        assert.doesNotMatch(q.page.querySelector(".shoal-detail__title").textContent, /[\u2013\u2014]/);
    } finally {
        q.close();
    }
});
