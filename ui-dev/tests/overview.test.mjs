// U2 + U-A1: Overview v2 (docs/design/ui-v2/overview.html; ui-spec 8.1, 7) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, visibleText } from './helpers.mjs';

async function overview(scenario, extra) {
    return mountPage(Object.assign({ scenario, params: { view: 'overview' }, settleMs: 300 }, extra || {}));
}

const lane = (p, which) => p.page.querySelector('.shoal-lane--' + which);
const rowsOf = (p) => [...p.page.querySelectorAll('[data-slot="cards"] > .shoal-lanerow')];
const rowNamed = (p, name) => rowsOf(p).find((r) => r.querySelector('.shoal-row__nametext').textContent === name);
const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);

test('U-A1: two real buttons at the top start the pair flow in the right direction', async () => {
    const p = await overview('default');
    try {
        const tiles = [...p.page.querySelectorAll('.shoal-actions > button.shoal-act')];
        assert.equal(tiles.length, 2);
        assert.match(tiles[0].textContent, /Add a friend's server/);
        assert.match(tiles[0].textContent, /Paste the invite your friend sent you/);
        assert.match(tiles[1].textContent, /Let a friend watch mine/);
        assert.match(tiles[1].textContent, /You choose which libraries they get/);
        // Screen readers hear the title as the name and the sentence as the description; the picture is hidden from them.
        for (const tile of tiles) {
            const body = p.page.querySelector('#' + tile.getAttribute('aria-describedby'));
            assert.ok(body && tile.contains(body));
            assert.equal(tile.querySelector('.shoal-flow').getAttribute('aria-hidden'), 'true');
        }

        tiles[0].click();
        tiles[1].click();
        await p.wait(20);
        assert.deepEqual(p.navigations.map((n) => n.replace(/^.*\?/, '')), ['name=Shoal&view=pair&dir=watch', 'name=Shoal&view=pair&dir=share']);
    } finally {
        p.close();
    }
});

test('U-A1: the Watching tile shows real posters from friends; the share tile shows none until the server sends its own', async () => {
    const p = await overview('default');
    try {
        const inImgs = [...p.page.querySelectorAll('[data-slot="flowin"] img')];
        assert.equal(inImgs.length, 3);
        for (const img of inImgs) {
            assert.equal(img.getAttribute('alt'), '');
            assert.match(img.getAttribute('src'), /^\/Items\/item-[^/]+\/Images\/Primary/);
        }

        assert.equal(p.page.querySelectorAll('[data-slot="flowout"] img').length, 0);
    } finally {
        p.close();
    }
});

test('U-A1: two lanes, each a labelled and described section, Watching first', async () => {
    const p = await overview('default');
    try {
        const lanes = [...p.page.querySelectorAll('.shoal-lanes > section.shoal-lane')];
        assert.deepEqual(lanes.map((l) => l.querySelector('h2').textContent), ['Watching', 'Being watched']);
        for (const l of lanes) {
            assert.equal(p.page.querySelector('#' + l.getAttribute('aria-labelledby')).tagName, 'H2');
            assert.ok(p.page.querySelector('#' + l.getAttribute('aria-describedby')).textContent.length > 10);
        }

        assert.match(visibleText(lanes[0]), /Friends' servers your server/);
        assert.match(visibleText(lanes[1]), /Your server friends' servers/);
    } finally {
        p.close();
    }
});

test('Watching: counts every listed title, says how many servers are online and links the one that needs attention', async () => {
    const p = await overview('default');
    try {
        const t = visibleText(lane(p, 'in'));
        assert.match(t, /2,202 titles from 3 servers/); // 1,204 + 386 + 612: an offline server's titles stay listed
        assert.match(t, /2 of 3 online/);
        assert.match(t, /1 needs attention/);
        assert.match(t, /Last sync/);
        assert.equal(rowsOf(p).length, 3);
        buttonIn(lane(p, 'in'), '1 needs attention').click();
        assert.equal(document.activeElement, rowNamed(p, 'Fernwood').querySelector('a'));
    } finally {
        p.close();
    }
});

test('Watching: rows are alphabetical links to the server, with the speed verdict in the meta line', async () => {
    const p = await overview('default');
    try {
        assert.deepEqual(rowsOf(p).map((r) => r.querySelector('.shoal-row__nametext').textContent), ['Fernwood', 'Granite', 'Hops']);
        const hops = rowNamed(p, 'Hops');
        const link = hops.querySelector('a.shoal-row');
        assert.match(link.getAttribute('href'), /view=server&id=h-hops/);
        assert.match(visibleText(hops), /Online/);
        assert.match(visibleText(hops), /1,204 titles · via Tailscale · 1080p smooth/);
        // No button inside a link (a row's action sits beside it).
        assert.equal(link.querySelectorAll('button, a').length, 0);
    } finally {
        p.close();
    }
});

test('an offline server keeps its stored art, greyed, and says titles stay listed (D5)', async () => {
    const p = await overview('default');
    try {
        const row = rowNamed(p, 'Granite');
        assert.ok(row.classList.contains('is-dim'));
        assert.ok(row.querySelector('.shoal-row__img').getAttribute('src').includes('/Items/item-gran-1/Images/Backdrop'));
        const t = visibleText(row);
        assert.match(t, /612 titles stay listed/);
        assert.match(t, /Offline for 2 hours/);
        assert.match(t, /Next check in 4 min/);
        const retry = buttonIn(row, 'Retry now');
        assert.ok(retry);
        assert.equal(retry.getAttribute('aria-label'), 'Retry now (Granite)');
    } finally {
        p.close();
    }
});

test('every server state has its chip, hint and action (ui-spec 7)', async () => {
    const p = await overview('problems');
    try {
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
            const row = rowNamed(p, name);
            assert.ok(row, name);
            for (const re of patterns) {
                assert.match(row.textContent, re, name);
            }
        }

        assert.match(rowNamed(p, 'Fernwood').textContent, /600 new titles on hold/);
        assert.match(p.text(), /need attention/);
        // Pairing rows open the wizard, stopped ones their stored outcome; neither opens Server detail (ui-spec 2.2).
        assert.match(rowNamed(p, 'Willow').querySelector('a').getAttribute('href'), /view=pair&dir=watch/);
        assert.match(rowNamed(p, 'Birch').querySelector('a').getAttribute('href'), /view=pair&id=h-stop/);
    } finally {
        p.close();
    }
});

test('a server on a Tailscale relay says why it is slow and what to open', async () => {
    const p = await overview('default');
    try {
        assert.match(rowNamed(p, 'Fernwood').textContent, /This friend is connected through a Tailscale relay, so speed is limited\. Open UDP 41641 on one side\./);
    } finally {
        p.close();
    }
});

test('the via line follows the network class (Tailnet, LAN, Public)', async () => {
    const p = await overview('problems');
    try {
        assert.match(rowNamed(p, 'Hops').textContent, /via Tailscale/);
        assert.match(rowNamed(p, 'Fernwood').textContent, /via internet \(HTTPS\)/);
    } finally {
        p.close();
    }
});

test('Being watched: Playing now, then the friends with their state, devices and warnings', async () => {
    const p = await overview('default');
    try {
        const out = lane(p, 'out');
        const t = visibleText(out);
        assert.match(t, /2 friends, 2 playing now/);
        assert.match(t, /Playing from your server now 2 streams/);
        assert.match(t, /Sam's server 2 streams now · 1 transcoding/);
        assert.match(t, /Friends you share with/);
        assert.match(t, /1 library · 1 device · last seen 4 min ago/);
        assert.match(t, /1 unexpected device signed out/);
        assert.ok(out.querySelector('[data-slot="friends"] a[data-attention="1"]'));
        const live = out.querySelector('.shoal-row--live');
        assert.match(live.getAttribute('href'), /view=friend&id=f-sam/);
        assert.equal(out.querySelector('.shoal-avatar').getAttribute('aria-hidden'), 'true');
    } finally {
        p.close();
    }
});

test('Being watched: nobody playing hides the Playing now block and the count says friends only', async () => {
    const p = await overview('default', { nobodyPlaying: true });
    try {
        const out = lane(p, 'out');
        assert.equal(out.querySelector('[data-slot="playing"]').hidden, true);
        assert.match(visibleText(out.querySelector('.shoal-lane__count')), /^2 friends$/);
    } finally {
        p.close();
    }
});

test('Being watched: the foot says whether shared videos need sign-in, from the protection mode', async () => {
    const p = await overview('default');
    try {
        await p.wait(80);
        const foot = lane(p, 'out').querySelector('[data-slot="protect"]');
        assert.equal(foot.hidden, false);
        assert.match(visibleText(foot), /Shared videos don't need sign-in yet/); // the mock runs Log only
        assert.match(foot.querySelector('a').getAttribute('href'), /view=settings/);
        assert.equal(p.mock.calls.filter((c) => c.method === 'GET' && c.path === 'Hardening').length, 1, 'read once per visit');
    } finally {
        p.close();
    }
});

test('first run shows the tiles and the explainer, and nothing else', async () => {
    const p = await overview('empty');
    try {
        const t = p.text();
        assert.match(t, /Add a friend's server/);
        assert.match(t, /Let a friend watch mine/);
        assert.match(t, /Watch your friends' movies from your own server/);
        assert.doesNotMatch(t, /Link speed|Being watched|Servers you watch/);
        assert.equal(rowsOf(p).length, 0);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Hardening').length, 0);
    } finally {
        p.close();
    }
});

test('no servers but friends watch from you: Watching says so, Being watched renders normally', async () => {
    const p = await overview('friendsOnly');
    try {
        assert.match(visibleText(lane(p, 'in')), /You aren't watching anyone's library yet\./);
        assert.equal(lane(p, 'in').querySelector('[data-action="sync-all"]').hidden, true);
        assert.match(visibleText(lane(p, 'out')), /Friends you share with/);
        assert.doesNotMatch(p.text(), /Link speed/);
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

test('U-A1 order: tiles, lanes, New from friends\' servers, Library overlap, Link speed', async () => {
    const p = await overview('default');
    try {
        const heads = [...p.page.querySelectorAll('[data-shoal-view="overview"] h2')].filter((h) => !h.closest('[hidden]')).map((h) => visibleText(h));
        assert.deepEqual(heads, ['Watching', 'Being watched', "New from friends' servers Watching", 'Library overlap with Fernwood Watching', 'Link speed']);
        // "Which server plays first" waits for the order from the server (docs/kiro2-v2.9-01.md).
        assert.equal(p.page.querySelector('[data-section="playorder"]').hidden, true);
    } finally {
        p.close();
    }
});

test('Which server plays first: GET PlayOrder fills it, names from the server list, unknown handles dropped', async () => {
    const p = await overview('default', { playOrder: true });
    try {
        const section = p.page.querySelector('[data-section="playorder"]');
        assert.equal(section.hidden, false);
        const items = [...section.querySelectorAll('ol.shoal-prio > li')];
        assert.equal(items.length, 3, 'h-gone is not a server here');
        assert.deepEqual(items.map((li) => li.querySelector('.shoal-prio__name').textContent), ['Number 1: Hops', 'Number 2: Fernwood', 'Number 3: Granite']);
        assert.equal(items[0].querySelector('.shoal-rank').getAttribute('aria-hidden'), 'true');
        assert.match(visibleText(items[0]), /Fastest link Automatic/);
        assert.match(visibleText(items[1]), /Slower link Automatic/);
        assert.match(visibleText(items[2]), /Backup only, and offline right now Set by you/);
        assert.ok(items[2].querySelector('.shoal-mode--set use').getAttribute('href').endsWith('i-pin'));
        assert.ok(items[0].querySelector('.shoal-mode--auto use').getAttribute('href').endsWith('i-auto'));
        assert.ok(items[2].classList.contains('is-dim'), 'an offline server is dimmed; the list has no art (2.9.3 U6)');
        assert.equal(items[1].querySelector('a').getAttribute('aria-label'), 'Fernwood server details');
        // The route sends no "changed" fields yet: the foot carries only how to change it.
        assert.equal(visibleText(section.querySelector('.shoal-prio-foot') || section).includes('Order last changed'), false);
        assert.match(visibleText(section), /To change it, open a server\./);
        const heads = [...p.page.querySelectorAll('[data-shoal-view="overview"] h2')].filter((h) => !h.closest('[hidden]')).map((h) => visibleText(h));
        assert.deepEqual(heads.slice(2, 4), ["New from friends' servers Watching", 'Which server plays first (3) Watching']);
        // The name in the route's answer is never what the page prints: it comes from Status.
        assert.ok(p.mock.calls.some((c) => c.method === 'GET' && c.path === 'PlayOrder'));
    } finally {
        p.close();
    }
});

test('Which server plays first: a reason the page does not know says nothing; one server hides the block', async () => {
    const p = await overview('default', { playOrder: 'odd' });
    try {
        const items = [...p.page.querySelectorAll('ol.shoal-prio > li')];
        // Granite is Backup only: the tier still speaks, the unknown reason does not.
        assert.equal(items[2].querySelector('.shoal-prio__why').textContent, 'Backup only');
        assert.doesNotMatch(visibleText(items[2]), /something new/);
    } finally {
        p.close();
    }

    const q = await overview('default', { playOrder: 'one' });
    try {
        assert.equal(q.page.querySelector('[data-section="playorder"]').hidden, true);
    } finally {
        q.close();
    }
});

test('Which server plays first: an older server without the route is asked once, then not again', async () => {
    const p = await overview('default');
    try {
        assert.equal(p.page.querySelector('[data-section="playorder"]').hidden, true);
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(p.mock.calls.filter((c) => c.path === 'PlayOrder').length, 1);
    } finally {
        p.close();
    }
});

test('Library overlap: the backup line uses the K53 counts (plays first, backup for) and hides without them', async () => {
    const p = await overview('default', { playOrder: true });
    try {
        const select = p.page.querySelector('.shoal-speed select');
        select.value = 'h-hops';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(200);
        const line = p.page.querySelector('[data-slot="backupline"]');
        assert.equal(line.hidden, false);
        assert.equal(visibleText(line), '238 titles have a backup server. If Hops is offline, they play from another server instead. Hops is also the backup for 12 titles.');
        assert.deepEqual([...line.querySelectorAll('strong')].map((b) => b.textContent), ['238 titles have a backup server.', '12 titles']);
    } finally {
        p.close();
    }

    const only = await overview('default', { playOrder: true, backups: 'only' });
    try {
        const line = only.page.querySelector('[data-slot="backupline"]');
        assert.equal(visibleText(line), 'Fernwood is the backup for 12 titles. Other servers play them first.');
    } finally {
        only.close();
    }

    for (const extra of [{}, { playOrder: true, backups: 'none' }]) {
        const q = await overview('default', extra);
        try {
            assert.equal(q.page.querySelector('[data-slot="backupline"]').hidden, true);
        } finally {
            q.close();
        }
    }
});

test('keys that cannot be unlocked: the banner shows and every row offers Pair again', async () => {
    const p = await overview('keys');
    try {
        assert.match(p.page.querySelector('.shoal-banner').textContent, /Shoal can't unlock its saved keys/);
        const rows = rowsOf(p);
        assert.equal(rows.length, 3);
        for (const r of rows) {
            assert.match(r.textContent, /Pair again/);
            assert.match(r.textContent, /Not in use until Shoal can unlock its keys/);
        }
    } finally {
        p.close();
    }
});

test('recovery mode: banner, h1 and one line; no tiles, no lanes', async () => {
    const p = await overview('recovery');
    try {
        assert.match(p.page.querySelector('.shoal-banner').textContent, /Shoal couldn't read its settings/);
        const t = p.text();
        assert.match(t, /Shoal is paused until its settings are fixed\./);
        assert.doesNotMatch(t, /Sync now|Add a friend's server|Watching/);
        assert.equal(p.mock.calls.filter((c) => c.path !== 'Status').length, 0, 'no other endpoint is called');
    } finally {
        p.close();
    }
});

test('Sync now posts once, announces, and a repeat is answered with a cooldown (Retry-After)', async () => {
    const p = await overview('default');
    try {
        const btn = p.page.querySelector('[data-action="sync-all"]');
        assert.ok(lane(p, 'in').contains(btn), 'Sync now lives in the Watching lane');
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

test('SKIPPED-INVISIBLE: a skipped Sync now is announced as skipped, never "Sync finished" with the old counts, and the Overview shows it next to the last real sync', async () => {
    const p = await overview('default', { syncSkipReason: 'tooSoon' });
    try {
        p.page.querySelector('[data-action="sync-all"]').click();
        await p.wait(4200);
        document.dispatchEvent(new Event('visibilitychange'));
        await p.wait(300);
        const live = p.page.querySelector('[data-slot="live"]').textContent;
        assert.doesNotMatch(live, /Sync finished/);
        assert.match(live, /Sync now skipped this time for/);
        const line = p.page.querySelector('[data-slot="lastsync"]').textContent;
        assert.match(line, /Last sync \d+ min ago/, 'the real time stays: ' + line);
        assert.match(line, /Sync now skipped this time just now\. Shoal synced this server a few minutes ago\. Last real sync: OK\./);
    } finally {
        p.close();
    }
});

test('SKIPPED-INVISIBLE: a Sync now that ran still says "Sync finished" and shows no skip', async () => {
    const p = await overview('default');
    try {
        p.page.querySelector('[data-action="sync-all"]').click();
        await p.wait(4200);
        document.dispatchEvent(new Event('visibilitychange'));
        await p.wait(300);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Sync finished/);
        assert.equal(p.page.querySelector('[data-slot="lastsync"] [data-slot="skipped"]'), null);
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

test('choosing another server in Link speed updates the overlap and announces it', async () => {
    const p = await overview('default');
    try {
        const select = p.page.querySelector('.shoal-speed select');
        select.value = 'h-hops';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(250);
        assert.match(p.text(), /What plays from Hops/);
        assert.match(p.text(), /Library overlap with Hops/);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Showing link speed for Hops/);
    } finally {
        p.close();
    }
});

test('S1: with the keys locked, the lane offers no Sync now', async () => {
    const p = await overview('keys');
    try {
        assert.equal(p.page.querySelector('[data-action="sync-all"]').hidden, true);
    } finally {
        p.close();
    }
});

test('a screen made again (recovery mode ending) does not double a tile click or the Sync now post', async () => {
    const p = await overview('default');
    try {
        const real = p.mock.handle;
        let recovery = true;
        p.mock.handle = async (method, path, body) => {
            const res = await real(method, path, body);
            if (recovery && path === 'Status') {
                res.body = Object.assign({}, res.body, { state: { mode: 'Recovery', reason: 'unreadable', restoredFrom: null } });
            }

            return res;
        };
        await p.handle.refresh?.();
        p.page.querySelector('[data-action="retry-load"]').click(); // any Status read goes through the same path
        await p.wait(150);
        assert.equal(p.page.querySelector('[data-shoal-view="recovery"]').hidden, false, 'in recovery');
        recovery = false;
        // Leaving recovery makes the Overview screen again on the same markup.
        p.page.querySelector('[data-action="retry-load"]').click();
        await p.wait(50);
        const banner = p.page.querySelector('.shoal-banner button');
        if (banner) {
            banner.click();
        }

        await p.wait(400);
        p.page.querySelector('.shoal-act--in').click();
        await p.wait(20);
        assert.equal(p.navigations.length, 1, 'one navigation per click');
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

test('playOrderRows keeps the server order, maps the ranker reasons and drops what it cannot name', async () => {
    const mod = await import('./helpers.mjs').then((h) => h.loadShoal());
    const servers = [{ handle: 'a', displayName: 'A' }, { handle: 'b', displayName: 'B' }];
    const rows = mod.playOrderRows({ playOrder: [{ peerKey: 'b', tier: 'Prefer' }, { peerKey: 'x', tier: 'Normal' }, null, { peerKey: 'a', tier: 'Normal', reason: 'nope' }] }, servers);
    assert.deepEqual(rows.map((r) => [r.server.handle, r.setByYou, r.reason]), [['b', true, 'Always preferred'], ['a', false, '']]);
    assert.deepEqual(mod.playOrderRows(undefined, servers), []);
    assert.deepEqual(mod.playOrderRows({ playOrder: 'bad' }, servers), []);
    assert.equal(mod.playOrderRows({ playOrder: [{ peerKey: 'a', tier: 'Weird' }] }, servers)[0].tier, 'Normal');
    const why = (tier, reason) => mod.playOrderRows({ playOrder: [{ peerKey: 'a', tier, reason }] }, servers)[0].reason;
    assert.equal(why('Normal', 'fastest link'), 'Fastest link');
    assert.equal(why('Normal', 'faster, waiting to confirm'), 'Faster, waiting to confirm');
    assert.equal(why('Normal', 'a faster server is being checked'), 'A faster server is being checked');
    assert.equal(why('Normal', 'not measured yet'), 'Not tested yet');
    assert.equal(why('Normal', 'unstable link'), 'The link is unstable');
    assert.equal(why('Normal', 'needs attention'), 'Needs attention');
    assert.equal(why('Normal', 'back online, waiting until it stays up'), 'Back online, waiting until it stays up');
    assert.equal(why('Normal', 'offline'), 'Offline right now');
    assert.equal(why('Backup', 'offline'), 'Backup only, and offline right now');
    assert.equal(why('Backup', 'fastest link'), 'Backup only');
    assert.equal(why('Prefer', 'slower link'), 'Slower link');
    assert.equal(why('Normal', 'constructor'), '', 'no prototype keys');
});

test('backupVia names the other server only when exactly two can play; ordinalPlace', async () => {
    const mod = await import('./helpers.mjs').then((h) => h.loadShoal());
    const servers = [{ handle: 'a', displayName: 'A' }, { handle: 'b', displayName: 'B' }, { handle: 'c', displayName: 'C' }];
    const two = { playOrder: [{ peerKey: 'a', tier: 'Normal' }, { peerKey: 'b', tier: 'Normal' }] };
    assert.equal(mod.backupVia(two, servers, servers[0]).displayName, 'B');
    assert.equal(mod.backupVia(two, servers, servers[2]), null);
    assert.equal(mod.backupVia({ playOrder: [...two.playOrder, { peerKey: 'c' }] }, servers, servers[0]), null);
    assert.equal(mod.backupVia(null, servers, servers[0]), null);
    assert.deepEqual([1, 2, 3, 4, 11, 12, 13, 21, 22, 23, 101, 111].map(mod.ordinalPlace), ['1st', '2nd', '3rd', '4th', '11th', '12th', '13th', '21st', '22nd', '23rd', '101st', '111th']);
});
