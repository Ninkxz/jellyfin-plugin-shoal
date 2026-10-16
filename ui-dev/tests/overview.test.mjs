// U2: Overview (ui-spec 8.1, 7) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

async function overview(scenario, extra) {
    return mountPage(Object.assign({ scenario, params: { view: 'overview' }, settleMs: 300 }, extra || {}));
}

test('the header counts servers, attention and the titles of servers in use', async () => {
    const p = await overview('default');
    try {
        const t = p.text();
        assert.match(t, /2 of 3 servers online/);
        assert.match(t, /1,590 titles shared with you/); // 1,204 + 386; the offline server is not counted (ui-spec 0.3 #9)
        assert.match(t, /Last sync/);
        assert.equal(p.page.querySelectorAll('.shoal-card').length, 3);
    } finally {
        p.close();
    }
});

test('cards are alphabetical and the first online server is selected', async () => {
    const p = await overview('default');
    try {
        const names = [...p.page.querySelectorAll('.shoal-card__nametext')].map((n) => n.textContent);
        assert.deepEqual(names, ['Fernwood', 'Granite', 'Hops']);
        const selected = p.page.querySelector('.shoal-card.is-selected .shoal-card__nametext');
        assert.equal(selected.textContent, 'Fernwood');
        assert.match(p.text(), /What plays from Fernwood/);
    } finally {
        p.close();
    }
});

test('an offline server keeps its stored art, greyed, and says titles stay listed (D5)', async () => {
    const p = await overview('default');
    try {
        const card = [...p.page.querySelectorAll('.shoal-card')].find((c) => c.querySelector('.shoal-card__nametext').textContent === 'Granite');
        assert.ok(card.classList.contains('is-dim'));
        assert.ok(card.querySelector('.shoal-card__img').getAttribute('src').includes('/Items/item-gran-1/Images/Backdrop'));
        assert.match(card.textContent, /titles stay listed but won't play until it's back/);
        assert.match(card.textContent, /Offline for 2 hours/);
        assert.match(card.textContent, /Retry now/);
        assert.match(card.textContent, /Next check in 4 min/);
    } finally {
        p.close();
    }
});

test('every server state has its chip, hint and action (ui-spec 7)', async () => {
    const p = await overview('problems');
    try {
        const byName = (n) => [...p.page.querySelectorAll('.shoal-card')].find((c) => c.querySelector('.shoal-card__nametext').textContent === n);
        const expect = {
            Birch: [/Stopped for safety/, /Birch didn't answer like Jellyfin, so Shoal stopped/, /See what to do/, /Not paired/],
            Alder: [/Pairing stopped/, /Lost contact with Alder/, /See what to do/],
            Elm: [/Pairing stopped/, /The invite expired/, /See what to do/],
            Cedar: [/Different server/, /certificate changed/, /Check certificate/],
            Fernwood: [/Online, unstable/, /62 ms jitter, 4% loss/, /Test link again/],
            Granite: [/Offline/, /Retry now/],
            Hops: [/Syncing/, /140 removals on hold/, /7 more/],
            Ivy: [/Different server/, /isn't the one you paired with/, /Pair again/],
            Maple: [/Paused/, /You paused Maple/, /Resume/],
            Pinecone: [/Access removed/, /removed your access/, /Pair again/],
            Willow: [/Pairing/, /Waiting for Willow/, /Continue pairing/]
        };
        for (const [name, patterns] of Object.entries(expect)) {
            const card = byName(name);
            assert.ok(card, name);
            for (const re of patterns) {
                assert.match(card.textContent, re, name);
            }
        }

        assert.match(byName('Fernwood').textContent, /600 new titles on hold/);
        assert.match(p.text(), /need attention/);
    } finally {
        p.close();
    }
});

test('a friend with unexpected devices counts toward attention and shows the warning line', async () => {
    const p = await overview('default');
    try {
        const text = p.text();
        assert.match(text, /Friends watching from you/);
        assert.match(text, /Sam/);
        assert.match(text, /Playing now: 2 \(1 transcoding\)/);
        assert.match(text, /1 unexpected device signed out/);
        assert.match(text, /1 library · 1 device · last seen 4 min ago/);
        // The header link targets the friends list when only friends need attention (the default scenario also has a relayed server).
        assert.ok(p.page.querySelector('[data-attention="1"]'));
    } finally {
        p.close();
    }
});

test('first run shows the empty state and nothing else', async () => {
    const p = await overview('empty');
    try {
        const t = p.text();
        assert.match(t, /Watch your friends' movies from your own server/);
        assert.match(t, /your friend doesn't need to install anything/);
        assert.match(t, /Pair a server/);
        assert.match(t, /Share with a friend/);
        assert.doesNotMatch(t, /Link speed/);
        assert.equal(p.page.querySelectorAll('.shoal-card').length, 0);
    } finally {
        p.close();
    }
});

test('no servers but friends watch from you: compact prompt, friends section still renders', async () => {
    const p = await overview('friendsOnly');
    try {
        const t = p.text();
        assert.match(t, /You aren't watching anyone's library yet\./);
        assert.match(t, /Friends watching from you/);
        assert.doesNotMatch(t, /Link speed/);
    } finally {
        p.close();
    }
});

test('servers paired but nothing shared yet explains itself', async () => {
    const p = await overview('nothingShared');
    try {
        const t = p.text();
        assert.match(t, /Nothing shared with you yet\. Titles appear after the first sync\./);
        assert.match(t, /Overlap shows up after the first full sync\./);
    } finally {
        p.close();
    }
});

test('"Friends watching from you" is the last section (owner D6)', async () => {
    const p = await overview('default');
    try {
        const sections = [...p.page.querySelectorAll('[data-shoal-view="overview"] h2')].filter((h) => !h.closest('[hidden]')).map((h) => h.textContent);
        assert.equal(sections[sections.length - 1], 'Friends watching from you');
    } finally {
        p.close();
    }
});

test('keys that cannot be unlocked: the banner shows and every card offers Pair again', async () => {
    const p = await overview('keys');
    try {
        assert.match(p.page.querySelector('.shoal-banner').textContent, /Shoal can't unlock its saved keys/);
        const cards = [...p.page.querySelectorAll('.shoal-card')];
        assert.equal(cards.length, 3);
        for (const c of cards) {
            assert.match(c.textContent, /Pair again/);
            assert.match(c.textContent, /Not in use until Shoal can unlock its keys/);
        }
    } finally {
        p.close();
    }
});

test('recovery mode: banner, h1 and one line; no cards, no actions', async () => {
    const p = await overview('recovery');
    try {
        assert.match(p.page.querySelector('.shoal-banner').textContent, /Shoal couldn't read its settings/);
        const t = p.text();
        assert.match(t, /Shoal is paused until its settings are fixed\./);
        assert.doesNotMatch(t, /Sync now|Pair a server|Friends' servers/);
        assert.equal(p.mock.calls.filter((c) => c.path !== 'Status').length, 0, 'no other endpoint is called');
    } finally {
        p.close();
    }
});

test('Sync now posts once, announces, and a repeat is answered with a cooldown (Retry-After)', async () => {
    const p = await overview('default');
    try {
        const btn = p.page.querySelector('[data-action="sync-all"]');
        btn.click();
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Sync').length, 1);
        await p.wait(100);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Sync started/);
        assert.match(p.page.querySelector('[data-slot="syncnote"]').textContent, /Available again in 5 min/);
        assert.equal(btn.getAttribute('aria-disabled'), 'true');
        btn.click();
        await p.wait(50);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Sync').length, 1, 'a disabled button does not post again');
    } finally {
        p.close();
    }
});

test('a failed load shows one generic sentence and never a server message', async () => {
    const p = await overview('error');
    try {
        assert.match(p.text(), /Shoal couldn't load\. Your server didn't answer\. Try again in a moment\./);
        assert.doesNotMatch(p.text(), /server\b.*500/i);
        assert.match(p.text(), /Try again/);
    } finally {
        p.close();
    }
});

test('choosing another server updates the panels and announces it', async () => {
    const p = await overview('default');
    try {
        const hops = [...p.page.querySelectorAll('.shoal-card')].find((c) => c.querySelector('.shoal-card__nametext').textContent === 'Hops');
        hops.querySelector('.shoal-card__body').click();
        await p.wait(250);
        assert.match(p.text(), /What plays from Hops/);
        assert.match(p.text(), /Library overlap with Hops/);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Showing link speed for Hops/);
        assert.ok(hops.classList.contains('is-selected'));
    } finally {
        p.close();
    }
});

test('the via line follows the network class (Tailnet, LAN, Public)', async () => {
    const p = await overview('default');
    try {
        const via = Object.fromEntries([...p.page.querySelectorAll('.shoal-card')].map((c) => [c.querySelector('.shoal-card__nametext').textContent, c.querySelector('.shoal-card__via').textContent]));
        assert.match(via.Hops, /via Tailscale/);
        assert.match(via.Fernwood, /via internet \(HTTPS\)/);
        assert.match(via.Granite, /via home network/);
    } finally {
        p.close();
    }
});

test('S1: with the keys locked, the header offers no Sync now', async () => {
    const p = await mountPage({ scenario: 'keys', params: { view: 'overview' }, settleMs: 300 });
    try {
        assert.equal(p.page.querySelector('[data-action="sync-all"]').hidden, true);
    } finally {
        p.close();
    }
});

test('a server on a Tailscale relay says why it is slow and what to open', async () => {
    const p = await overview('default');
    try {
        const card = [...p.page.querySelectorAll('.shoal-card')].find((c) => c.querySelector('.shoal-card__nametext').textContent === 'Fernwood');
        assert.match(card.textContent, /This friend is connected through a Tailscale relay, so speed is limited\. Open UDP 41641 on one side\./);
    } finally {
        p.close();
    }
});

test('N16: titles on a server paused for a re-pair are called paused, not offline', async () => {
    const mod = await import('./helpers.mjs').then((h) => h.loadShoal());
    const fmt = mod.makeFormat ? mod.makeFormat('en-US', () => Date.now()) : null;
    const paused = { handle: 'a', displayName: 'A', status: 'Disabled', pairingMethod: 'qc', needsRepair: true, titleCount: 7 };
    const sum = mod.summarizeServers([paused], fmt || { durationSince: () => '', relative: () => '', num: String });
    assert.equal(sum.titlesPaused, 7);
    assert.equal(sum.titlesOffline, 0);
});
