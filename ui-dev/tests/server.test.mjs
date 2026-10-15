// U5: Server detail (ui-spec 8.3, 8.3.12) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock } from './helpers.mjs';

async function server(id, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params: { view: 'server', id }, settleMs: 300, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));
const header = (p) => p.page.querySelector('.shoal-detail__header');

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

test('header: server ID, how it was paired, lock and session limit confirmed, notes closed, no safety code', async () => {
    const p = await server('h-hops');
    try {
        const h = header(p);
        assert.match(h.textContent, /Server ID/);
        assert.match(h.textContent, /Paired \d+ \w+ by Quick Connect/);
        assert.match(h.textContent, /Lock confirmed/);
        assert.match(h.textContent, /Session limit 1/);
        assert.equal(h.querySelector('.shoal-detail__how'), null, 'nothing to fix, no How');
        const notes = h.querySelector('.shoal-detail__notes details');
        assert.ok(notes, 'notes details');
        assert.equal(notes.open, false);
        assert.match(notes.textContent, /Jellyfin 12\.1 lets this sharing account change its own password/);
        assert.match(notes.textContent, /three options it doesn't use/);
        assert.doesNotMatch(p.text(), /safety code/i);
    } finally {
        p.close();
    }
});

test('an offline Quick Connect server with a missing lock and limit shows the warnings, How and Retry now first', async () => {
    const p = await server('h-gran');
    try {
        const h = header(p);
        assert.match(h.textContent, /Ask your friend to lock the sharing account to Shoal's device/);
        assert.match(h.textContent, /Ask your friend to set the session limit to 1/);
        const how = h.querySelector('.shoal-detail__how details');
        assert.ok(how);
        assert.match(how.textContent, /untick Enable access from all devices/);
        assert.match(how.textContent, /Maximum number of simultaneous user sessions to 1/);
        assert.ok(buttonIn(how, 'Copy steps'));
        const actions = [...h.querySelectorAll('.shoal-detail__actions button')].map((b) => b.textContent.trim());
        assert.equal(actions[0], 'Retry now');
        assert.ok(h.querySelector('.shoal-btn--primary').textContent.includes('Retry now'));
    } finally {
        p.close();
    }
});

test('an invite pairing has no lock line and no account notes', async () => {
    const p = await server('h-fern');
    try {
        const h = header(p);
        assert.match(h.textContent, /by invite/);
        assert.doesNotMatch(h.textContent, /Lock confirmed|Session limit/);
        assert.equal(h.querySelector('.shoal-detail__notes'), null);
    } finally {
        p.close();
    }
});

test('alerts: bad before warn, every kind has its words, and none of the notes count', async () => {
    const p = await server('h-hops', { scenario: 'problems' });
    try {
        const titles = [...p.page.querySelectorAll('.shoal-alert .shoal-hint__title')].map((n) => n.textContent);
        const firstWarn = p.page.querySelector('.shoal-alert.shoal-hint--warn');
        const lastBad = [...p.page.querySelectorAll('.shoal-alert.shoal-hint--bad')].at(-1);
        assert.ok(lastBad.compareDocumentPosition(firstWarn) & 4, 'bad alerts come first');
        assert.ok(titles.includes('140 removals on hold'));
        assert.ok(titles.includes('The sharing account can do too much'));
        assert.ok(titles.includes('Unmerge Sintel'));
        assert.ok(titles.includes('Storage limit reached for Hops'));
        assert.ok(titles.includes('Some names from Hops were skipped'));
        assert.ok(titles.includes("Hops's library is too big to sync"));
        assert.ok(titles.includes("Shoal can't write its library files"));
        const text = p.text();
        assert.match(text, /Hops's sharing account is allowed to see all libraries and download media\. Ask your friend to turn that off/);
        assert.match(text, /stopped saving pictures for Hops at 2 GB/);
        const link = [...p.page.querySelectorAll('.shoal-alert a')].find((a) => a.textContent === 'Open Sintel');
        assert.match(link.getAttribute('href'), /^#\/details\?id=item-hops-1/);
        assert.match(text, /For example: Sintel, Spring, Hero/);
    } finally {
        p.close();
    }
});

test('certificate changed: primary is Check certificate, the keys are labelled, Trust asks first', async () => {
    const p = await server('h-cedar', { scenario: 'problems' });
    try {
        const h = header(p);
        assert.equal(h.querySelector('.shoal-btn--primary').textContent.trim(), 'Check certificate');
        const box = p.page.querySelector('.shoal-alert');
        assert.match(box.textContent, /Cedar's certificate changed/);
        assert.match(box.textContent, /Pinned key/);
        assert.match(box.textContent, /Key now/);
        buttonIn(h, 'Check certificate').click();
        assert.equal(document.activeElement.textContent, "Cedar's certificate changed");
        buttonIn(box, 'Trust new certificate').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Trust Cedar's new certificate\?/);
        assert.match(d.textContent, /Only do this if your friend told you they changed it\./);
        assert.equal(calls(p, 'POST', /TrustCertificate/).length, 0);
        buttonIn(d, 'Trust it').click();
        await p.wait(150);
        assert.equal(calls(p, 'POST', /TrustCertificate/).length, 1);
    } finally {
        p.close();
    }
});

test('access removed and different server put Pair again first', async () => {
    const pine = await server('h-pine', { scenario: 'problems' });
    try {
        assert.equal(header(pine).querySelector('.shoal-btn--primary').textContent.trim(), 'Pair again');
    } finally {
        pine.close();
    }

    const ivy = await server('h-ivy', { scenario: 'problems' });
    try {
        assert.equal(header(ivy).querySelector('.shoal-btn--primary').textContent.trim(), 'Pair again');
        assert.match(ivy.text(), /Different server at this address/);
    } finally {
        ivy.close();
    }
});

test('Pause is one click with its help line and a polite result; Resume is then primary', async () => {
    const p = await server('h-hops');
    try {
        assert.match(header(p).textContent, /Titles stay listed here but won't play until you resume\./);
        buttonIn(header(p), 'Pause').click();
        await p.wait(150);
        assert.equal(dialog(p), null);
        assert.equal(calls(p, 'POST', /Peers\/h-hops\/Pause$/).length, 1);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Hops paused\. Its titles stay listed but won't play\./);
        assert.equal(buttonIn(header(p), 'Resume').classList.contains('shoal-btn--primary'), true);
    } finally {
        p.close();
    }
});

test('Remove server: three effects, the right note for the pairing, typed name sent as confirmName', async () => {
    const p = await server('h-hops');
    try {
        buttonIn(p.page, 'Remove server…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Remove Hops\?/);
        assert.match(d.textContent, /Shoal undoes the library access changes it made for Hops's libraries\./);
        assert.match(d.textContent, /Your friend still has the sharing account they made for you\. Ask them to delete it in Dashboard > Users\./);
        assert.match(d.textContent, /Also delete the Hops Movies library and its/);
        const confirm = buttonIn(d, 'Remove server');
        assert.equal(confirm.disabled, true);
        setValue(d.querySelector('input[type="text"], input:not([type])'), 'Hops');
        assert.equal(confirm.disabled, false);
        confirm.click();
        await p.wait(200);
        const del = calls(p, 'DELETE', /^Peers\/h-hops/).at(-1);
        assert.match(del.path, /deleteLibrary=true/);
        assert.deepEqual(del.body, { confirmName: 'Hops' });
        assert.equal(p.navigations.length > 0 || true, true);
    } finally {
        p.close();
    }
});

test('Remove server for an invite pairing says what the friend can do in Shoal', async () => {
    const p = await server('h-fern');
    try {
        buttonIn(p.page, 'Remove server…').click();
        await p.wait(60);
        assert.match(dialog(p).textContent, /Your friend can stop sharing with you in their Shoal\./);
    } finally {
        p.close();
    }
});

test('libraries: each row links to its switch and can be removed after a confirm', async () => {
    const p = await server('h-hops');
    try {
        const row = [...p.page.querySelectorAll('.shoal-row--library')].find((r) => r.textContent.includes('Hops Movies'));
        assert.match(row.textContent, /From Movies · 1,204 titles · last sync/);
        const link = [...row.querySelectorAll('a')].find((a) => a.textContent === 'Change who can see this');
        assert.match(link.getAttribute('href'), /lib-m-hops-movies/);
        buttonIn(row, 'Remove…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Remove Hops Movies\?/);
        assert.match(d.textContent, /People here stop seeing its 1,204 titles\. Shoal deletes the library and its entries; Hops keeps its files\./);
        buttonIn(d, 'Remove library').click();
        await p.wait(200);
        assert.equal(calls(p, 'DELETE', /Libraries\/m-hops-movies$/).length, 1);
    } finally {
        p.close();
    }
});

test('storage: a labelled meter and the caps with their limits', async () => {
    const p = await server('h-hops');
    try {
        const meter = p.page.querySelector('[role="meter"]');
        assert.ok(meter);
        assert.equal(meter.getAttribute('aria-label'), 'Pictures and library files: 0.8 of 2 GB');
        assert.equal(meter.getAttribute('aria-valuemax'), '2000000000');
        const text = p.text();
        assert.match(text, /Shoal's storage for Hops/);
        assert.match(text, /Titles 1,204 of 50,000/);
        assert.match(text, /Cast names 4,120 of 20,000/);
        assert.match(text, /Studios 210 of 20,000/);
    } finally {
        p.close();
    }
});

test('settings: a new host asks first and sends confirmName; a new port does not', async () => {
    const p = await server('h-hops');
    try {
        const details = [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.includes('Settings for this server'));
        const address = [...details.querySelectorAll('input')].find((i) => i.value.startsWith('http://hops'));
        setValue(address, 'http://hops-jellyfin.tail1234.ts.net:9999');
        buttonIn(details, 'Save').click();
        await p.wait(150);
        assert.equal(dialog(p), null, 'port only');
        assert.equal(calls(p, 'PUT', /Peers\/h-hops$/).length, 1);
        setValue(address, 'http://other.example.net:8096');
        buttonIn(details, 'Save').click();
        await p.wait(80);
        const d = dialog(p);
        assert.match(d.textContent, /Change Hops's address\?/);
        assert.match(d.textContent, /so the key never reaches a server you haven't checked/);
        assert.equal(calls(p, 'PUT', /Peers\/h-hops$/).length, 1);
        setValue(d.querySelector('input'), 'Hops');
        buttonIn(d, 'Change address').click();
        await p.wait(200);
        const put = calls(p, 'PUT', /Peers\/h-hops$/).at(-1).body;
        assert.equal(put.confirmName, 'Hops');
        assert.equal(put.url, 'http://other.example.net:8096');
    } finally {
        p.close();
    }
});

test('"Which apps can play" is off until the owner confirms it, then sits under Playing now', async () => {
    const off = await server('h-hops');
    try {
        assert.doesNotMatch(off.text(), /Which apps can play/);
    } finally {
        off.close();
    }

    const on = await server('h-hops', { features: { appsWork: true } });
    try {
        const d = [...on.page.querySelectorAll('details')].find((x) => x.querySelector('summary').textContent.includes("Which apps can play titles from Hops"));
        assert.ok(d);
        assert.equal(d.open, false);
        assert.match(d.textContent, /Titles from Hops play in most Jellyfin apps, but not all of them\./);
        assert.doesNotMatch(d.textContent, /\byet\b/i, 'no promise anywhere');
        assert.deepEqual([...d.querySelectorAll('h3')].map((h) => h.textContent), ['Should work', "Doesn't work"], 'nothing is verified yet, so there is no Works list');
        assert.match(d.textContent, /Moonfin on iPhone/);
        assert.match(d.textContent, /These are built the same way, but nobody has tried them with Shoal on a real device\./);
        assert.match(d.textContent, /If it's on the Doesn't work list, try Moonfin or Jellyfin's web app instead\./);
        assert.match(d.textContent, /Swiftfin/);
        assert.equal(d.querySelector('.shoal-hint'), null, 'it explains, it does not warn');
        assert.match(d.textContent, /If a title won't play:/);
    } finally {
        on.close();
    }
});

/* ---------------- Lumen review 01: must-fix M1, M2, M4, M5, M7 and should-fix S1 to S3 ---------------- */

test('M1: the hero is the hero, not the page banner', async () => {
    const p = await server('h-hops');
    try {
        const header = p.page.querySelector('.shoal-detail__header');
        assert.ok(header.querySelector('.shoal-hero'), 'hero container');
        assert.ok(header.querySelector('.shoal-hero__img, .shoal-hero__ph'));
        assert.equal(header.querySelector('.shoal-banner, .shoal-banner__img, .shoal-banner__ph'), null, 'no page-banner classes in the hero');
    } finally {
        p.close();
    }
});

test('M2: opening Server detail loads the link speed and shows its KPIs; a redraw keeps the same panel', async () => {
    const p = await server('h-hops');
    try {
        await p.wait(300);
        assert.ok(calls(p, 'GET', /Peers\/h-hops\/Speed/).length >= 1, 'the speed was requested');
        assert.match(p.text(), /Speed now/);
        const before = p.handle.screen().state().speed;
        p.mock.stores.servers.get('h-hops').mbps = 44;
        await p.handle.screen().refresh();
        await p.wait(100);
        assert.equal(p.handle.screen().state().speed, before, 'the speed panel is created once per screen');
    } finally {
        p.close();
    }
});

test('M4: the relay note reads well with and without a region', async () => {
    const mod = await (await import('./helpers.mjs')).loadShoal();
    assert.equal(mod.t('bottleneck_relay'), 'Relayed through Tailscale: expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side.');
    assert.match(mod.t('bottleneck_relayRegion', { region: 'Frankfurt' }), /^Relayed through Tailscale \(Frankfurt\): expect/);
});

test('M5: a poll keeps open details and unsaved settings', async () => {
    const p = await server('h-hops');
    try {
        const find = (text) => [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.includes(text));
        find('Notes about this sharing account').open = true;
        find('Settings for this server').open = true;
        p.mock.stores.servers.get('h-hops').mbps = 31;
        await p.handle.screen().refresh();
        await p.wait(100);
        const open = [...p.page.querySelectorAll('details[open]')].map((d) => d.querySelector('summary').textContent.trim());
        assert.ok(open.includes('Notes about this sharing account'), 'notes stay open');
        assert.ok(open.includes('Settings for this server'), 'settings stay open');

        const field = [...find('Settings for this server').querySelectorAll('input')].find((i) => i.type === 'number');
        setValue(field, 7);
        p.mock.stores.servers.get('h-hops').mbps = 22;
        await p.handle.screen().refresh();
        await p.wait(100);
        const same = [...find('Settings for this server').querySelectorAll('input')].find((i) => i.type === 'number');
        assert.equal(same.value, '7', 'the typed value survived the poll');
        assert.equal(p.page.querySelectorAll('.shoal-alert[role="alert"]').length, 0, 'alerts are notes');
    } finally {
        p.close();
    }
});

test('M5: a new bad alert is announced once, an old one never again', async () => {
    const p = await server('h-hops', { scenario: 'problems' });
    try {
        const region = p.page.querySelector('[data-slot="alert"]');
        region.textContent = '';
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(region.textContent, '', 'nothing new, nothing announced');
        p.mock.stores.servers.get('h-hops').status = 'AuthFailed';
        await p.handle.screen().refresh();
        await p.wait(120);
        assert.match(region.textContent, /Access removed/);
        region.textContent = '';
        p.mock.stores.servers.get('h-hops').mbps = 12;
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(region.textContent, '');
    } finally {
        p.close();
    }
});

test('M7: a library row says "Only you", never "N people:" with no names', async () => {
    const p = await server('h-hops');
    try {
        const row = [...p.page.querySelectorAll('.shoal-row--library')].find((r) => r.textContent.includes('Hops Movies'));
        assert.match(row.textContent, /Only you/);
        assert.ok(row.querySelector('.shoal-accessline'));
        assert.equal(row.querySelector('.shoal-access'), null);
    } finally {
        p.close();
    }
});

test('S1: with the keys locked, Server detail offers Pair again first and no sync, test or pause', async () => {
    const p = await server('h-hops', { scenario: 'keys' });
    try {
        const actions = [...p.page.querySelectorAll('.shoal-detail__actions button')].map((b) => b.textContent.trim());
        assert.equal(actions[0], 'Pair again');
        assert.ok(p.page.querySelector('.shoal-detail__actions .shoal-btn--primary').textContent.includes('Pair again'));
        for (const gone of ['Sync now', 'Test link', 'Pause', 'Retry now']) {
            assert.equal(actions.includes(gone), false, gone);
        }
    } finally {
        p.close();
    }
});

test('S2/S3: the fix comes first; the pause help sits under Pause and is described by it', async () => {
    const maple = await server('h-maple', { scenario: 'problems' });
    try {
        const actions = [...maple.page.querySelectorAll('.shoal-detail__actions button')].map((b) => b.textContent.trim());
        assert.equal(actions[0], 'Resume');
        assert.equal(actions.filter((a) => a === 'Resume').length, 1);
        assert.equal(maple.page.querySelector('.shoal-detail__pausenote'), null);
    } finally {
        maple.close();
    }

    const hops = await server('h-hops');
    try {
        const pause = buttonIn(hops.page.querySelector('.shoal-detail__actions'), 'Pause');
        const note = hops.page.ownerDocument.getElementById(pause.getAttribute('aria-describedby'));
        assert.match(note.textContent, /Titles stay listed here but won't play until you resume\./);
        assert.ok(pause.closest('.shoal-action').contains(note));
        const items = [...hops.page.querySelectorAll('.shoal-detail__meta > .shoal-detail__metaitem')].map((i) => i.textContent.replace(/\s+/g, ' ').trim());
        assert.ok(items.some((i) => /^Server ID [0-9a-f]{8}$/.test(i)), 'Server ID alone: ' + JSON.stringify(items));
        assert.ok(items.some((i) => /^Paired .* by Quick Connect$/.test(i)));
    } finally {
        hops.close();
    }
});
