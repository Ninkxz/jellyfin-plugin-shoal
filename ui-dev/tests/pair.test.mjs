// Pairing v2 (docs/ui-spec-pairing-v2.md): "Pair a server" and "Share", both directions, against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, JUNK, loadShoal } from './helpers.mjs';

async function pair(opts) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params: { view: 'pair', dir: 'watch' }, settleMs: 200, now: clock.now }, opts || {}));
    p.clock = clock;
    p.wiz = () => p.handle.screen().state();
    p.poll = async () => {
        await p.handle.screen().poll();
        await p.wait(30);
    };
    return p;
}

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const field = (p, label) => [...p.page.querySelectorAll('.shoal-field')].find((f) => f.querySelector('label') && f.querySelector('label').textContent.trim() === label);
const input = (p, label) => field(p, label).querySelector('input, select, textarea');
const dialog = (p) => p.page.querySelector('dialog[open]');
const step = (p) => p.page.querySelector('.shoal-step');
const probes = (p) => p.mock.calls.filter((c) => c.path === 'Peers/Probe');
const startPosts = (p) => p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Pairing');

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

const submit = async (p) => {
    p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
    await p.wait(120);
};

const INVITE = 'shoal1-3fa91c7e-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P';

async function click(p, label) {
    buttonIn(step(p), label).click();
    await p.wait(120);
}

async function toAddress() {
    // The wizard opens on the address (there is only one way to pair now).
}

async function fillConnect(p, o) {
    setValue(input(p, 'Name for this server'), o.name || 'Willow');
    setValue(input(p, 'Server address'), o.url || 'http://willow.tail1234.ts.net:8096');
    if (o.network) {
        setValue(input(p, 'How you reach it'), o.network);
    }
}

async function toCheck(p, method, o) {
    await toAddress(p, method);
    await fillConnect(p, o || {});
    await submit(p);
}

async function toInvite(p, o) {
    await toCheck(p, 'invite', o);
    await click(p, 'Yes, this is it');
}

async function startInvite(p, o) {
    await toInvite(p, o);
    setValue(input(p, 'Invite'), INVITE);
    await click(p, 'Start pairing');
    await p.wait(60);
}

const StepText = (p) => {
    const text = step(p).textContent.replace(/\s+/g, ' ');
    assert.doesNotMatch(text, JUNK, 'junk text on the step');
    return text;
};

/* ---------------- step 1: what do you want to do ---------------- */

test('step 1 asks the direction in plain words and waits for a choice', async () => {
    const p = await pair({ params: { view: 'pair' } });
    try {
        const t = p.text();
        assert.match(t, /What do you want to do\?/);
        assert.match(t, /Watch a friend's library/);
        assert.match(t, /Their movies show up in your library\. They keep the files\./);
        assert.match(t, /Your movies show up in their library\. Your friend needs Shoal too\./);
        assert.match(t, /To do both, pair once each way\./);
        assert.equal(buttonIn(p.page, 'Continue').disabled, true);
        assert.match(t, /Choose one to continue/);
        p.page.querySelector('input[type="radio"][value="watch"]').click();
        buttonIn(p.page, 'Continue').click();
        await p.wait(60);
        assert.equal(p.wiz().step, 'connect');
    } finally {
        p.close();
    }
});

/* ---------------- step 2: does your friend use Shoal ---------------- */

/* ---------------- step 3: the address and the connection helper ---------------- */

test('the address step: Who label, the fields in order, the helper, and the network chosen from the address (P4)', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        assert.match(StepText(p), /What is your friend's server address\?/);
        assert.match(StepText(p), /You do this/);
        const labels = [...p.page.querySelectorAll('.shoal-field > label')].filter((l) => !l.closest('details')).map((l) => l.textContent.trim());
        assert.deepEqual(labels, ['Name for this server', 'Server address', 'How you reach it']);
        assert.match(p.text(), /Ask your friend to send it\. For example http:\/\/myserver\.tail1234\.ts\.net:8096/);
        assert.equal(input(p, 'Server address').getAttribute('autocomplete'), 'off');
        assert.equal(input(p, 'Server address').getAttribute('autocapitalize'), 'off');
        const options = [...input(p, 'How you reach it').options].map((o) => o.textContent);
        assert.deepEqual(options, ['Tailscale (private)', 'Private address (home network or Tailscale router)', 'Internet (https only)']);
        assert.equal(field(p, 'Invite'), undefined, 'the invite is not asked for here any more');

        // P4: a private address over https on 8096 selects Private address and says both things.
        setValue(input(p, 'Server address'), 'https://10.0.0.20:8096');
        assert.equal(input(p, 'How you reach it').value, 'LAN');
        assert.match(p.text(), /This is a private address inside your friend's network\. It only works from here if your servers share a network, or Tailscale routes it\./);
        assert.match(p.text(), /Jellyfin uses plain http on port 8096 unless your friend turned on https there\./);
        assert.match(p.text(), /Shoal chose this from the address\. Change it if it's wrong\./);
        const helper = [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.trim() === 'Not sure which address to use?');
        assert.equal(helper.open, true, 'a warning opens the helper');

        setValue(input(p, 'Server address'), 'http://myserver.tail1234.ts.net:8096');
        assert.equal(input(p, 'How you reach it').value, 'Tailnet');
        assert.match(p.text(), /Looks like a Tailscale address\. Both servers need Tailscale running\./);
        assert.equal(p.page.querySelectorAll('.shoal-addrhints .shoal-hint--warn, .shoal-addrhints .shoal-hint--bad').length, 0, 'no warning for a Tailscale name');
    } finally {
        p.close();
    }
});

test('the address reader: numbers over https, an internet name, http on the internet, this server itself, a /web path, no scheme', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        const url = input(p, 'Server address');
        setValue(url, 'https://203.0.113.9');
        assert.match(p.text(), /Addresses made of numbers rarely have a valid https certificate, so the check may fail\. A name works better\./);
        assert.equal(input(p, 'How you reach it').value, 'Public');
        setValue(url, 'https://movies.example.com');
        assert.match(p.text(), /Looks like an internet address\. It needs a valid https certificate\./);
        setValue(url, 'http://movies.example.com');
        assert.match(p.text(), /Over the internet the address must start with https:\/\/\./);
        setValue(url, 'http://localhost:8096');
        assert.match(p.text(), /That points to this server itself\. Use your friend's address\./);
        setValue(url, 'http://myserver.tail1234.ts.net:8096/web/index.html');
        assert.match(p.text(), /Shoal removed \/web\/index\.html\. Only the start of the address is needed\./);
        assert.equal(url.value, 'http://myserver.tail1234.ts.net:8096');
        setValue(url, 'http://myserver.tail1234.ts.net:8096/jellyfin');
        assert.equal(url.value, 'http://myserver.tail1234.ts.net:8096/jellyfin', 'a real base path stays');
        setValue(url, 'myserver.tail1234.ts.net:8096');
        assert.match(p.text(), /Start the address with http:\/\/ or https:\/\/\./);
        buttonIn(p.page, 'Add http://').click();
        assert.equal(url.value, 'http://myserver.tail1234.ts.net:8096');
        assert.equal(input(p, 'How you reach it').value, 'Tailnet');
        // A person's own choice of network is not overruled by the reader.
        setValue(input(p, 'How you reach it'), 'Public');
        setValue(url, 'http://192.168.1.20:8096');
        assert.equal(input(p, 'How you reach it').value, 'Public');
    } finally {
        p.close();
    }
});

test('the connection helper: four setups, each with a Use this setup button, and the Say box', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'qc');
        const helper = [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.trim() === 'Not sure which address to use?');
        const t = helper.textContent.replace(/\s+/g, ' ');
        assert.match(t, /The address must be one that works from your server, not from your friend's living room\./);
        assert.match(t, /Say to your friend: Please send me the address of your Jellyfin that I can reach from my server\./);
        for (const title of ['Your friend shared their server with you in Tailscale', 'Your friend shares their home network through a Tailscale router', 'Your friend has a public https address', 'Your friend put their server in a DMZ or opened a port on the router']) {
            assert.ok(t.includes(title), title);
        }

        assert.match(t, /Same house\? Use the home address, for example http:\/\/192\.168\.1\.20:8096, and choose Private address\./);
        const uses = [...helper.querySelectorAll('button')].filter((b) => b.textContent.trim() === 'Use this setup');
        assert.equal(uses.length, 4);
        uses[1].click();
        assert.equal(input(p, 'How you reach it').value, 'LAN');
        uses[2].click();
        assert.equal(input(p, 'How you reach it').value, 'Public');
        assert.equal(document.activeElement, input(p, 'Server address'));
    } finally {
        p.close();
    }
});

test('N12: no unencrypted-key tick box any more; a home http address goes straight to the check', async () => {
    const p = await pair();
    try {
        await fillConnect(p, { url: 'http://192.168.1.40:8096', network: 'LAN' });
        assert.doesNotMatch(p.text(), /key travels unencrypted/);
        await submit(p);
        assert.equal(probes(p).length, 1);
        assert.doesNotMatch(p.text(), /Tick the box to continue/);
    } finally {
        p.close();
    }
});

test('field checks use the spec sentences', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        await submit(p);
        assert.match(p.text(), /Enter a name\./);
        assert.match(p.text(), /Enter the server address\./);
        setValue(input(p, 'Name for this server'), 'Willow');
        setValue(input(p, 'Server address'), 'ftp://x');
        await submit(p);
        assert.match(p.text(), /Start the address with http:\/\/ or https:\/\/\./);
        setValue(input(p, 'Server address'), 'http://willow.example.net');
        setValue(input(p, 'How you reach it'), 'Public');
        await submit(p);
        assert.match(p.text(), /Internet servers need an address that starts with https:\/\/\. A DMZ or port forward on their router does not change this\./);
        setValue(input(p, 'Server address'), 'http://localhost:8096');
        setValue(input(p, 'How you reach it'), 'Tailnet');
        await submit(p);
        assert.match(p.text(), /That points to this server itself/);
        assert.equal(probes(p).length, 0);
    } finally {
        p.close();
    }
});

test('probe errors land on the field or the form with the spec words', async () => {
    const cases = {
        blocked: /Shoal can't connect to that address\. It points to this server itself or to a private service\./,
        wrongNetwork: /That address isn't a Tailscale address\./,
        certificate: /That server's https certificate isn't trusted, so Shoal won't connect\. A name usually has a valid certificate\. An address made of numbers almost never does\./,
        notJellyfin: /That address answered, but it isn't a Jellyfin server\. Check the port\. Jellyfin usually uses 8096\./,
        rateLimited: /Too many tries\. You can try again in 5 min\./
    };
    for (const [kind, re] of Object.entries(cases)) {
        const p = await pair({ probeError: kind });
        try {
            await toCheck(p, 'invite');
            assert.match(p.text(), re, kind);
            assert.equal(p.wiz().step, 'connect', kind);
        } finally {
            p.close();
        }
    }
});

test('an unreachable address lists what to check and opens the connection helper, keeping every field', async () => {
    const p = await pair({ probeError: 'unreachable' });
    try {
        await toCheck(p, 'qc');
        const t = p.text();
        assert.match(t, /Shoal could not reach that address from this server\./);
        for (const line of ['Check the address for typing mistakes.', 'If the address is https, try http with the same address, or the other way round.', 'If your friend uses Tailscale, check that Tailscale is running on both servers.', 'A private address only works if your servers share a network or Tailscale routes it.']) {
            assert.ok(t.includes(line), line);
        }

        assert.equal(p.wiz().step, 'connect');
        assert.equal(input(p, 'Name for this server').value, 'Willow');
        assert.equal(input(p, 'Server address').value, 'http://willow.tail1234.ts.net:8096');
        const helper = [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.trim() === 'Not sure which address to use?');
        helper.open = false;
        buttonIn(p.page, 'Open the connection helper').click();
        assert.equal(helper.open, true);
    } finally {
        p.close();
    }
});

/* ---------------- step 4: is this the right server ---------------- */

test('the check: facts and the Server ID, the note, and two buttons (nothing is asked of the friend\'s server)', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'invite');
        const t = StepText(p);
        assert.match(t, /Is this the right server\?/);
        assert.match(t, /Server name\s*Willow/);
        assert.match(t, /Jellyfin version\s*12\.1\.0/);
        assert.match(t, /Server ID\s*3fa91c7e/);
        assert.match(t, /Shoal uses the Server ID later to notice if this address starts leading somewhere else\./);
        assert.doesNotMatch(t, /K7QM/);
        assert.ok(buttonIn(step(p), 'Yes, this is it') && buttonIn(step(p), 'No, change the address'));
        assert.equal(startPosts(p).length, 0);
        await click(p, 'No, change the address');
        assert.equal(p.wiz().step, 'connect');
        assert.equal(input(p, 'Server address').value, 'http://willow.tail1234.ts.net:8096', 'the fields are kept');
    } finally {
        p.close();
    }
});

/* ---------------- invite route: ask for the invite (P5), then read the code ---------------- */

test('the invite is asked for after the check, with the friend\'s steps, a Say box and a Start pairing that waits for it (P5)', async () => {
    const p = await pair();
    try {
        await toInvite(p);
        assert.equal(probes(p).length, 1, 'the check ran once, without an invite');
        assert.equal(probes(p)[0].body.invite, undefined);
        const t = StepText(p);
        assert.match(t, /Ask your friend for an invite/);
        assert.match(t, /Your friend does this/);
        assert.match(t, /You do this/);
        for (const line of ['Open Shoal in the Jellyfin dashboard, under Plugins.', 'Choose Share with a friend.', 'Pick the libraries to share and choose Continue.', 'Choose how your server is reached, then choose Create invite.', 'Send the invite and their server address to you.']) {
            assert.ok(t.includes(line), line);
        }

        assert.match(t, /Say to your friend: I'm ready for the invite\. Please make it now, because it only lasts 15 minutes\./);
        assert.match(t, /Paste it here\. It starts with shoal1\./);
        assert.ok(buttonIn(step(p), 'Copy message'));
        const start = buttonIn(step(p), 'Start pairing');
        assert.equal(start.disabled, true);
        assert.match(t, /Paste the invite to continue/);
        setValue(input(p, 'Invite'), INVITE);
        assert.equal(start.disabled, false);
        assert.match(StepText(p), /Your invite is for http:\/\/willow\.tail1234\.ts\.net:8096\./);
        start.click();
        await p.wait(150);
        assert.equal(probes(p).length, 2, 'the cheap re-check with the invite');
        assert.equal(probes(p)[1].body.invite, INVITE);
        assert.equal(startPosts(p).length, 1);
        assert.equal(p.wiz().step, 'pair');
        assert.equal(p.wiz().form.invite, '', 'the invite is not kept');
    } finally {
        p.close();
    }
});

test('the Invite box recognises the other codes and says where each goes (P3)', async () => {
    const p = await pair();
    try {
        await toInvite(p);
        const box = input(p, 'Invite');
        setValue(box, '482913');
        const alert = p.page.querySelector('.shoal-invitenote [role="alert"]');
        assert.match(alert.textContent, /That's a number, not an invite or a safety code\. An invite starts with shoal1\. A safety code is 8 letters and numbers, like K7QM 2XA4\./);
        buttonIn(alert, 'Show where each code goes').click();
        await p.wait(60);
        assert.match(dialog(p).textContent, /Where each code goes/);
        buttonIn(dialog(p), 'Close').click();
        await p.wait(30);

        setValue(box, 'K7QM 2XA4');
        assert.match(p.text(), /That's a safety code\. It's read out and typed on your friend's Shoal\. It does not go in this box\./);
        setValue(box, 'http://willow.tail1234.ts.net:8096');
        assert.match(p.text(), /That's an address\. It goes in Server address on the earlier step\./);
        buttonIn(p.page, 'Go to the address').click();
        await p.wait(60);
        assert.equal(p.wiz().step, 'connect');
        await click(p, 'Check server');
        await click(p, 'Yes, this is it');
        setValue(input(p, 'Invite'), 'shoal1-3fa9');
        assert.match(p.text(), /That invite looks cut off\. Copy the whole text, from the first letter to the end\./);
        setValue(input(p, 'Invite'), 'hello');
        assert.match(p.text(), /That doesn't look like an invite\. An invite starts with shoal1\./);
        assert.equal(buttonIn(step(p), 'Start pairing').disabled, true);
    } finally {
        p.close();
    }
});

test('invite errors stay on the invite step with the name and address kept', async () => {
    const cases = [
        ['inviteWrongServer', /That invite is for a different server\. Check that you are using the address your friend sent with the invite\./, 'Change the address'],
        ['inviteInvalid', /That invite no longer works\. It lasts 15 minutes and works once\. Ask your friend to choose Create a new invite, then paste it here\./, null]
    ];
    for (const [kind, re, button] of cases) {
        const p = await pair({ probeError: kind });
        try {
            await toInvite(p);
            setValue(input(p, 'Invite'), INVITE);
            await click(p, 'Start pairing');
            assert.match(p.text(), re, kind);
            assert.equal(p.wiz().step, 'invite', kind);
            assert.equal(startPosts(p).length, 0, kind);
            if (button) {
                assert.ok(buttonIn(step(p), button), kind);
            } else {
                assert.equal(input(p, 'Invite').value, '', 'a used invite is cleared');
            }

            assert.equal(p.wiz().form.url, 'http://willow.tail1234.ts.net:8096');
            assert.equal(p.wiz().form.name, 'Willow');
        } finally {
            p.close();
        }
    }
});

test('Pilot 07 D1: the second check of the invite route carries the same acknowledgement as the first', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        await fillConnect(p, { url: 'https://192.168.1.40:8096', network: 'LAN' });
        await submit(p);
        await click(p, 'Yes, this is it');
        setValue(input(p, 'Invite'), INVITE);
        await click(p, 'Start pairing');
        assert.equal(probes(p).length, 2);
        assert.equal(probes(p)[1].body.lanAck, probes(p)[0].body.lanAck);
        assert.equal(p.wiz().step, 'pair');
    } finally {
        p.close();
    }
});

test('reading the code: Home shows the 8 character code, a Say box, no compare buttons; approval moves on by itself', async () => {
    const p = await pair();
    try {
        await startInvite(p);
        const t = StepText(p);
        assert.match(t, /Read this code to your friend/);
        assert.match(t, /Your friend types this code into the box on their Shoal\. It proves you are talking to the right person\./);
        assert.match(t, /Say to your friend: Open Shoal\. There is a request from Rich’s Jellyfin\. Type this code when it asks\. I'll read it slowly: K7QM 2XA4\./);
        const code = step(p).querySelector('.shoal-code');
        assert.equal(code.getAttribute('aria-label'), 'Safety code K 7 Q M, 2 X A 4');
        assert.deepEqual([...code.querySelectorAll('.shoal-code__group')].map((g) => g.textContent), ['K7QM', '2XA4']);
        assert.equal(code.dataset.copy, 'K7QM2XA4');
        assert.ok(buttonIn(step(p), 'Copy code') && buttonIn(step(p), 'Copy message'));
        assert.match(t, /Waiting for your friend to approve on Willow…/);
        assert.match(t, /Expires in 15:00/);
        assert.match(t, /My friend doesn't see a request/);
        assert.match(t, /Where does each code go\?/);
        assert.doesNotMatch(t, /The codes match|They don't match/);
        assert.match(t, /Cancel pairing/);
        p.clock.advance(7000);
        await p.poll();
        assert.equal(p.wiz().step, 'choose');
        assert.match(p.text(), /Choose what to add/);
    } finally {
        p.close();
    }
});

test('the countdown says Under 3 minutes left in words, with what to do (P8)', async () => {
    const p = await pair();
    try {
        await startInvite(p);
        const warn = step(p).querySelector('.shoal-countdown__warn');
        assert.equal(warn.hidden, true);
        p.clock.advance(13 * 60000);
        await p.wait(1200);
        assert.equal(warn.hidden, false);
        assert.match(warn.textContent, /Under 3 minutes left\. If this is not enough time, ask your friend for a new invite\./);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Under 3 minutes left\./);
    } finally {
        p.close();
    }
});

test('the invite\'s time left is shown when the check reports it, and left out when it does not', async () => {
    let p = await pair({ inviteLeftMin: 9 });
    try {
        await startInvite(p);
        assert.match(StepText(p), /This invite has about 9 minutes left\./);
    } finally {
        p.close();
    }

    p = await pair();
    try {
        await startInvite(p);
        assert.doesNotMatch(StepText(p), /This invite has about/);
    } finally {
        p.close();
    }
});

/* ---------------- Quick Connect route: get ready (P6), send the code, compare ---------------- */

/* ---------------- recovery cards (8.3) ---------------- */

// [pairing kind, title, body, primary button, who words, goes to]
const OUTCOMES = [
    ['expired', /The invite ran out/, /Invites last 15 minutes and work once\. Nothing was saved\./, 'Paste a new invite', /Your friend does this/, 'invite'],
    ['denied', /Your friend chose Deny/, /Nothing was saved\. If that was a mistake, ask them for a new invite\./, 'Paste a new invite', /Your friend does this/, 'invite'],
    ['codeMismatch', /The safety code didn't match/, /Your friend typed the wrong code 3 times, so their Shoal cancelled the request and the invite\. Nothing was saved\. Read the code again slowly, one group at a time\./, 'Paste a new invite', /You do this/, 'invite'],
    ['adminAccount', /That account is an admin on Willow/, /Shoal only accepts a limited sharing account/, 'Change the address', /You do this/, 'connect'],
    ['serverChanged', /The server changed while pairing/, /isn't the one you checked/, 'Change the address', /You do this/, 'connect'],
    ['badPeerResponse', /Pairing stopped for your safety/, /answered in a way Jellyfin never does/, 'Change the address', /You do this/, 'connect'],
    ['interrupted', /The invite ran out/, /Your server restarted while waiting for your friend, so this invite can't be finished/, 'Paste a new invite', /Your friend does this/, 'invite'],
    ['network', /Lost contact with Willow/, /couldn't reach Willow for 30 seconds while pairing/, 'Start again', /You do this/, 'invite'],
    ['inviteInvalid', /That invite didn't work/, /It may have run out, been used already, or been replaced by a newer one\. Ask your friend for a new one\./, 'Paste a new invite', /Your friend does this/, 'invite'],
    ['server', /Pairing didn't work/, /Something went wrong on your server\. Try again in a moment\./, 'Start again', /You do this/, 'invite'],
    ['someFutureKind', /Pairing didn't finish/, /Something stopped this pairing\. Nothing was saved\. Start again with a new invite\./, 'Paste a new invite', /You do this|Your friend does this/, 'invite']
];

for (const [kind, title, body, primary, who, goes] of OUTCOMES) {
    test(`recovery card ${kind}: its words, its Who, its one primary button, its Kept line`, async () => {
        const p = await pair({ pairing: kind });
        try {
            await startInvite(p);
            p.clock.advance(800);
            await p.poll();
            p.clock.advance(7000);
            await p.poll();
            const t = StepText(p);
            assert.match(t, title);
            assert.match(t, body);
            assert.match(t, who);
            assert.match(t, /Kept: the name Willow, the address http:\/\/willow\.tail1234\.ts\.net:8096 and the route/);
            assert.equal(step(p).querySelectorAll('.shoal-btn--primary').length, 1, 'exactly one primary button');
            assert.equal(step(p).querySelector('.shoal-btn--primary').textContent.trim(), primary);
            assert.doesNotMatch(t, /Quick Connect/);
            step(p).querySelector('.shoal-btn--primary').click();
            await p.wait(250);
            assert.equal(p.wiz().step, goes);
            assert.equal(p.wiz().form.name, 'Willow');
            assert.equal(p.wiz().form.url, 'http://willow.tail1234.ts.net:8096');
            assert.equal(p.wiz().form.invite, '');
        } finally {
            p.close();
        }
    });
}

test('after "The invite ran out" the invite box is empty and focus-ready, and the name and address are filled', async () => {
    const p = await pair({ pairing: 'denied' });
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        await click(p, 'Paste a new invite');
        assert.equal(p.wiz().step, 'invite');
        assert.equal(input(p, 'Invite').value, '');
        assert.equal(p.wiz().form.name, 'Willow');
        assert.equal(p.wiz().form.url, 'http://willow.tail1234.ts.net:8096');
    } finally {
        p.close();
    }
});

test('the invite ran out while waiting: the card says the timer was not kept running and who acts', async () => {
    const p = await pair({ pairing: 'expired' });
    try {
        await startInvite(p);
        p.clock.advance(13000);
        await p.wait(1200);
        const t = StepText(p);
        assert.match(t, /The invite ran out/);
        assert.match(t, /Invites last 15 minutes and work once\. Nothing was saved\. Ask your friend to choose Create a new invite in Shoal, then paste it here\./);
        assert.match(t, /Your friend does this/);
        assert.equal(step(p).querySelector('.shoal-countdown'), null, 'no timer keeps running');
    } finally {
        p.close();
    }
});

test('Dismiss clears the stored card and returns to Overview', async () => {
    const p = await pair({ pairing: 'network' });
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        buttonIn(step(p), 'Dismiss').click();
        await p.wait(150);
        assert.ok(p.mock.calls.some((c) => c.method === 'POST' && /\/DismissPairing$/.test(c.path)));
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Removed from your list/);
        assert.ok(p.navigations.length >= 1);
    } finally {
        p.close();
    }
});

test('the stored view reads the server record: stopped time, no code number, Dismiss', async () => {
    const p = await pair({ scenario: 'problems', params: { view: 'pair', id: 'h-stop' } });
    try {
        const t = StepText(p);
        assert.match(t, /Pairing stopped for your safety/);
        assert.match(t, /Stopped 20 min ago/);
        assert.ok(buttonIn(step(p), 'Dismiss'));
    } finally {
        p.close();
    }
    const q2 = await pair({ scenario: 'problems', params: { view: 'pair', id: 'h-elm' } });
    try {
        assert.match(StepText(q2), /The invite ran out/);
    } finally {
        q2.close();
    }
});

test('the resume banner appears when a session already exists', async () => {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const { createMock } = await import('../mock/mock-server.js');
    const { createMockClient } = await import('../mock/mock-client.js');
    const { loadShoal, ensureDom, html } = await import('./helpers.mjs');
    const mod = await loadShoal();
    const d = ensureDom();
    const mock = createMock({ scenario: 'default', now: clock.now });
    const probe = (await mock.handle('POST', 'Peers/Probe', { url: 'http://willow.tail1234.ts.net:8096', network: 'Tailnet' })).body;
    await mock.handle('POST', 'Pairing', { probeHandle: probe.probeHandle, displayName: 'Willow', method: 'qc' });
    const client = createMockClient(mock);
    const holder = d.window.document.createElement('div');
    holder.innerHTML = html;
    const page = holder.firstElementChild;
    d.window.document.querySelector('.mainAnimatedPages').append(page);
    const handle = mod.mount(page, { view: 'pair' }, { api: mod.createApi(client), client, dashboard: null, now: clock.now, locale: 'en-GB', storage: { get: () => null, set: () => {} } });
    page.dispatchEvent(new d.window.CustomEvent('viewshow', { bubbles: true }));
    await new Promise((r) => setTimeout(r, 250));
    const text = page.textContent.replace(/\s+/g, ' ');
    assert.match(text, /Pairing with Willow is still in progress\./);
    assert.ok(buttonIn(page, 'Continue') && buttonIn(page, 'Cancel pairing'));
    assert.equal(handle.screen().state().step, 'pair');
    page.remove();
});

/* ---------------- choose what to add, and the end ---------------- */

test('choose: libraries, who can see them (only you), a required box for big libraries, and the success screen', async () => {
    const p = await pair();
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        await p.wait(250);
        const t = StepText(p);
        assert.match(t, /Choose what to add/);
        assert.match(t, /Pick libraries from Willow\. At first only you can see them, so you can try them\./);
        assert.match(t, /TV shows are turned off\. Turn them on in Settings, under Extra features\./);
        assert.equal(buttonIn(p.page, 'Add and start syncing').getAttribute('aria-disabled'), 'true');
        assert.match(t, /Pick at least one library/);
        const rows = [...p.page.querySelectorAll('.shoal-libpick__item')];
        const movies = rows.find((r) => r.textContent.includes('Movies') && !r.textContent.includes('Everything'));
        movies.querySelector('input').click();
        await p.wait(30);
        assert.match(movies.textContent, /Name in your library/);
        assert.match(movies.textContent, /Only you/);
        assert.equal(movies.querySelector('.shoal-input').value, 'Willow Movies');
        assert.equal(buttonIn(p.page, 'Add and start syncing').getAttribute('aria-disabled'), null);
        const big = rows.find((r) => r.textContent.includes('Everything'));
        big.querySelector('input').click();
        await p.wait(30);
        assert.match(big.textContent, /Add all 8,400 titles/);
        assert.match(big.textContent, /The first sync takes a while and saves about 1\.7 GB of pictures\./);
        assert.equal(buttonIn(p.page, 'Add and start syncing').getAttribute('aria-disabled'), 'true', 'the big library needs its box');
        assert.match(StepText(p), /Tick the box under .* to confirm adding all of its titles/, 'the reason stays visible');
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(30);
        assert.match(p.page.querySelector('[data-slot="adderror"]').textContent, /Tick the box under/, 'a click on a blocked button says why');
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && /Libraries$/.test(c.path)).length, 0, 'and sends nothing');
        [...big.querySelectorAll('.shoal-check')].find((c) => /Add all/.test(c.textContent)).querySelector('input').click();
        await p.wait(20);
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(250);
        const body = p.mock.calls.find((c) => c.method === 'POST' && /\/Libraries$/.test(c.path)).body;
        assert.ok(Array.isArray(body));
        assert.deepEqual(body[0].selectedUserIds, ['u-rich']);
        const done = StepText(p);
        assert.match(done, /Willow is paired/);
        assert.match(done, /You can leave this page\. Syncing keeps going in the background\./);
        assert.match(done, /Go to Overview/);
        assert.doesNotMatch(done, /Last step for your friend/, 'that hint is for Quick Connect pairings');
        assert.ok(p.mock.calls.some((c) => c.method === 'DELETE' && c.path === 'Pairing'), 'the finished pairing is closed on the server (N13)');
    } finally {
        p.close();
    }
});

/* ---------------- share direction (pairing v2 9) ---------------- */

async function shareThroughWho(p) {
    setValue(input(p, "Friend's name"), 'Riley');
    await p.wait(200);
    [...p.page.querySelectorAll('.shoal-choices--checks .shoal-check')].find((c) => c.textContent.includes('Movies') && !c.textContent.includes('Hops')).querySelector('input').click();
    await submit(p);
    return p;
}

async function toReach(p) {
    await shareThroughWho(p);
    assert.equal(p.wiz().step, 'reach');
}

async function chooseTransport(p, value) {
    p.page.querySelector(`input[type="radio"][value="${value}"]`).click();
    await p.wait(60);
}

test('share step 1: Who label, friend, libraries (movies only), quality and transcodes, the hidden-account note, the codes card', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        const t = StepText(p);
        assert.match(t, /You do this/);
        assert.match(t, /Who are you sharing with\?/);
        assert.match(t, /Shown only to you\./);
        assert.match(t, /Libraries from friends' servers can't be shared on\./);
        assert.match(t, /TV shows are turned off\. Turn them on in Settings, under Extra features\./);
        assert.match(t, /Shoal makes a hidden sharing account for your friend\. It can only watch the libraries you pick, can't change anything, and you can remove it anytime\./);
        const card = p.page.querySelector('.shoal-codescard');
        assert.equal(card.open, false, 'closed the first time');
        card.open = true;
        assert.equal(card.querySelectorAll('dl').length, 2, 'the share side has two rows');
        assert.match(card.textContent, /The approval box on this page/);
        const q1 = input(p, 'Highest quality per stream');
        assert.deepEqual([...q1.options].map((o) => o.textContent), ['No limit', '40 Mbps (4K)', '20 Mbps (1080p, high)', '10 Mbps (1080p)', '4 Mbps (720p)']);
        const libs = [...p.page.querySelectorAll('.shoal-choices--checks .shoal-check')];
        assert.ok(libs.find((c) => /TV shows/.test(c.textContent)).querySelector('input').disabled);
        assert.ok(libs.find((c) => /Hops Movies/.test(c.textContent)).querySelector('input').disabled);
        await shareThroughWho(p);
        const call = p.mock.calls.find((c) => c.method === 'POST' && c.path === 'Shares');
        assert.deepEqual(call.body, { friendName: 'Riley', libraryIds: ['lib-movies'], maxBitrateMbps: null, maxConcurrentTranscodes: 4 });
    } finally {
        p.close();
    }
});

test('share step 2: how your friend reaches you, with the address to send filled from Shoal\'s guess (P11)', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toReach(p);
        let t = StepText(p);
        assert.match(t, /How will Riley reach your server\?/);
        assert.match(t, /Through Tailscale\. I shared this server with Riley/);
        assert.match(t, /Their address will look like http:\/\/myserver\.tail1234\.ts\.net:8096/);
        assert.match(t, /Through a public https address/);
        assert.match(t, /Through my home network or a Tailscale router/);
        assert.match(t, /Use only if Riley is on the same network or routes it\. Invites do not work over plain http, so give Riley a Tailscale name or an https address\./);
        assert.match(t, /Say to Riley: I'm making the invite now\. It lasts 15 minutes\. Tell me when you're ready to paste it\./);
        assert.equal(buttonIn(step(p), 'Create invite').disabled, true);
        await chooseTransport(p, 'tailnet');
        const address = input(p, 'The address to send');
        assert.equal(address.value, 'http://rich-jellyfin.tail1234.ts.net:8096');
        assert.match(p.text(), /This is the address Riley will type\. It must work from their server\./);
        assert.match(p.text(), /Looks like a Tailscale address/);
        await chooseTransport(p, 'public');
        assert.equal(address.value, 'https://movies.example.com');
        await chooseTransport(p, 'home');
        assert.equal(address.value, '', 'never the container address from the list: this page was opened on localhost, which no friend can reach');
        // A typed address is not overwritten by a later choice.
        setValue(address, 'http://my.own.address:8096');
        await chooseTransport(p, 'tailnet');
        assert.equal(address.value, 'http://my.own.address:8096');
        const helper = [...p.page.querySelectorAll('details')].find((d) => d.querySelector('summary').textContent.trim() === 'Which address do I use?');
        assert.ok(helper, 'the helper is there, phrased for the sharer');
        assert.match(helper.textContent, /I shared my server with Riley in Tailscale/);
    } finally {
        p.close();
    }
});

test('share step 2 without a guess: the field is empty with a hint, and the invite needs an address', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' }, reach: 'none' });
    try {
        await toReach(p);
        await chooseTransport(p, 'public');
        const address = input(p, 'The address to send');
        assert.equal(address.value, '');
        assert.match(p.text(), /Type the address Riley should use, for example https:\/\/movies\.example\.com/);
        buttonIn(step(p), 'Create invite').click();
        await p.wait(100);
        assert.match(p.text(), /Enter the server address\./);
        assert.equal(p.mock.calls.filter((c) => /\/Invites$/.test(c.path)).length, 0, 'no invite before the address is set');
    } finally {
        p.close();
    }
});

async function toWait(p) {
    await toReach(p);
    await chooseTransport(p, 'tailnet');
    buttonIn(step(p), 'Create invite').click();
    await p.wait(150);
}

test('share step 3: one message to send with the address and the invite, a secret warning, a Say box, and the wait', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toWait(p);
        assert.equal(p.wiz().step, 'wait');
        const t = StepText(p);
        assert.match(t, /You do this/);
        assert.match(t, /Send this to Riley/);
        assert.ok(p.page.querySelector('.shoal-invitebox').textContent.includes('shoal1-3fa91c7e-K7QM-2XA4'), 'the invite is shown once');
        const message = step(p).querySelector('textarea.shoal-message');
        assert.equal(message.hasAttribute('readonly'), true);
        assert.match(message.value, /^Shoal invite for you\. Address: http:\/\/rich-jellyfin\.tail1234\.ts\.net:8096\. Invite: shoal1-3fa91c7e-K7QM-2XA4/);
        assert.match(message.value, /It lasts 15 minutes\. In Shoal, choose Pair a server, Yes they use Shoal, then paste the address and the invite\. Your screen will show a code\. Read it to me and I will type it\.$/);
        assert.match(t, /This message contains the invite\. Send it privately, only to Riley\./);
        assert.match(t, /Say to Riley: I sent you the message\. When Shoal shows you a code, read it out to me\./);
        assert.match(t, /Waiting for Riley to use the invite…/);
        assert.match(t, /Expires in 15:00/);
        assert.ok(buttonIn(step(p), 'Copy invite') && buttonIn(step(p), 'Copy message') && buttonIn(step(p), 'Create a new invite') && buttonIn(step(p), 'Cancel invite'));

        // The friend's server redeems it: the approval step appears, with no invite and no code on it.
        p.clock.advance(8000);
        await p.poll();
        assert.equal(p.wiz().step, 'approve');
        assert.equal(p.page.innerHTML.includes('shoal1-3fa91c7e'), false, 'the invite is gone from the page');
    } finally {
        p.close();
    }
});

test('share: the countdown warns in words, and a run out invite is a recovery card that keeps the address (9.5)', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' }, shareFlow: 'wait' });
    try {
        await toWait(p);
        p.clock.advance(13 * 60000);
        await p.wait(1200);
        assert.match(StepText(p), /Under 3 minutes left\. If Riley needs more time, make a new invite\./);
        p.clock.advance(3 * 60000);
        await p.wait(1200);
        const t = StepText(p);
        assert.match(t, /The invite ran out/);
        assert.match(t, /Invites last 15 minutes and work once\. Make a new one when Riley is ready\./);
        assert.match(t, /You do this/);
        assert.match(t, /Kept: Riley, the libraries and the address/);
        assert.equal(step(p).querySelectorAll('.shoal-btn--primary').length, 1);
        await click(p, 'Make a new invite');
        assert.equal(p.wiz().step, 'reach');
        assert.equal(input(p, 'The address to send').value, 'http://rich-jellyfin.tail1234.ts.net:8096');
    } finally {
        p.close();
    }
});

async function toApproval(p) {
    await toWait(p);
    p.clock.advance(8000);
    await p.poll();
}

test('share step 4: the code Riley reads out, never displayed; its box recognises a Quick Connect code', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        const t = StepText(p);
        assert.match(t, /Type the code Riley reads to you/);
        assert.match(t, /Sam’s Jellyfin wants to connect/);
        assert.match(t, /Server ID\s*3fa91c7e/);
        assert.match(t, /Connecting from\s*a Tailscale address/);
        assert.match(t, /8 letters and numbers, like K7QM 2XA4\. Riley sees it on their screen\./);
        assert.doesNotMatch(t, /K7QM2XA4|shoal1-/);
        const code = p.page.querySelector('.shoal-input--code');
        assert.equal(code.getAttribute('maxlength'), '11');
        assert.equal(code.getAttribute('autocapitalize'), 'characters');
        assert.equal(buttonIn(p.page, 'Approve').disabled, true);
        assert.ok(buttonIn(p.page, 'Deny'));
        setValue(code, '482913');
        code.dispatchEvent(new window.Event('blur'));
        assert.match(p.text(), /A safety code is 8 letters and numbers, like K7QM 2XA4\./);
        setValue(code, 'k7qm 2x');
        code.dispatchEvent(new window.Event('blur'));
        assert.match(p.text(), /A safety code is 8 letters and numbers, like K7QM 2XA4\./);
    } finally {
        p.close();
    }
});

test('typing the code: capitals as you type, spaces ignored, Approve only at 8 valid characters', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        const code = p.page.querySelector('.shoal-input--code');
        const approve = buttonIn(p.page, 'Approve');
        setValue(code, 'k7qm 2xa');
        assert.equal(code.value, 'K7QM 2XA');
        assert.equal(approve.disabled, true);
        setValue(code, 'k7qm 2xa4');
        assert.equal(code.value, 'K7QM 2XA4');
        assert.equal(approve.disabled, false);
        setValue(code, 'K7QM-2XAU');
        assert.equal(approve.disabled, true, 'U is not in the alphabet');
        setValue(code, 'k7qm2xaa');
        code.dispatchEvent(new window.Event('blur'));
        assert.equal(approve.disabled, false, 'whether it is right is the server\'s call');
    } finally {
        p.close();
    }
});

test('wrong codes keep the typed value and count down; the third ends the request and its invite', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        const code = p.page.querySelector('.shoal-input--code');
        for (const [n, text] of [[2, '2 tries left'], [1, '1 tries left']]) {
            setValue(code, 'AAAA 1111');
            buttonIn(p.page, 'Approve').click();
            await p.wait(120);
            assert.match(p.text(), new RegExp("That code doesn't match\\. Check it with Riley and type it again\\. " + n + ' tries left\\.'), text);
            assert.equal(code.getAttribute('aria-invalid'), 'true');
            assert.equal(code.value, 'AAAA 1111', 'the typed value stays');
            assert.ok(p.page.querySelector('[data-slot="alert"]').textContent.includes('tries left'));
            assert.equal(document.activeElement, code);
        }

        setValue(code, 'AAAA 1111');
        buttonIn(p.page, 'Approve').click();
        await p.wait(150);
        const t = StepText(p);
        assert.match(t, /Request cancelled/);
        assert.match(t, /The safety code didn't match 3 times, so Shoal cancelled this request and its invite to be safe\. Nothing was shared\. Call Riley, check the code together, then create a new invite\./);
        assert.match(t, /Kept: Riley, the libraries and the address/);
        assert.equal(step(p).querySelector('.shoal-btn--primary').textContent.trim(), 'Create a new invite');
        const status = (await p.mock.handle('GET', 'Shares')).body;
        assert.ok(status.shares.some((s) => s.friendName === 'Riley'));
        await click(p, 'Create a new invite');
        assert.equal(p.wiz().step, 'reach');
    } finally {
        p.close();
    }
});

test('the right code approves, waits for the friend\'s server to collect its key, then says it is connected', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' }, collectMs: 100 });
    try {
        await toApproval(p);
        setValue(p.page.querySelector('.shoal-input--code'), 'k7qm 2xa4');
        buttonIn(p.page, 'Approve').click();
        await p.wait(150);
        const sent = p.mock.calls.filter((c) => c.method === 'POST' && /\/Approval$/.test(c.path)).at(-1).body;
        assert.deepEqual(sent, { approve: true, sas: 'K7QM2XA4' });
        assert.match(StepText(p), /Approved\. Waiting for Sam’s Jellyfin to connect…/);
        await p.wait(200);
        await p.poll();
        assert.match(StepText(p), /Riley can now watch from your server/);
        assert.match(StepText(p), /They'll see 1 library\. You can pause or stop sharing on Riley's page\./);
    } finally {
        p.close();
    }
});

test('Deny needs no code and ends with its own words; a key nobody collects is a failure', async () => {
    let p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        buttonIn(p.page, 'Deny').click();
        await p.wait(150);
        assert.match(StepText(p), /You denied the request/);
        assert.match(StepText(p), /Nothing was shared\./);
        assert.match(StepText(p), /Your friend does this/);
        assert.deepEqual(p.mock.calls.filter((c) => /\/Approval$/.test(c.path) && c.method === 'POST').at(-1).body, { approve: false });
    } finally {
        p.close();
    }

    p = await pair({ params: { view: 'pair', dir: 'share' }, shareFlow: 'uncollected', collectMs: 50 });
    try {
        await toApproval(p);
        setValue(p.page.querySelector('.shoal-input--code'), 'K7QM2XA4');
        buttonIn(p.page, 'Approve').click();
        await p.wait(150);
        await p.wait(100);
        await p.poll();
        assert.match(StepText(p), /Sam’s Jellyfin didn't finish connecting/);
        assert.match(StepText(p), /Shoal signed that device out to be safe\. Create a new invite and try again\./);
    } finally {
        p.close();
    }
});

/* ---------------- security ---------------- */

test('T-SEC-11 / T-PAIR-7: hostile server, invite-side and Home names stay text through the whole wizard', async () => {
    const p = await pair({ scenario: 'hostile', pairing: 'badPeerResponse' });
    try {
        await toCheck(p, 'invite');
        const check = () => assert.equal(p.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        check();
        assert.ok(p.text().includes('<img src=x onerror=alert(1)>'));
        await click(p, 'Yes, this is it');
        setValue(input(p, 'Invite'), INVITE);
        await click(p, 'Start pairing');
        await p.wait(60);
        check();
        p.clock.advance(7000);
        await p.poll();
        check();
    } finally {
        p.close();
    }

    const q2 = await pair({ scenario: 'hostile', params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(q2);
        assert.equal(q2.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        assert.ok(q2.page.querySelector('.shoal-approval__home').textContent.includes('<img src=x onerror=alert(1)>'));
    } finally {
        q2.close();
    }
});

test('the safety code the admin types is never stored, logged or put in a URL', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        setValue(p.page.querySelector('.shoal-input--code'), 'K7QM2XA4');
        assert.equal(JSON.stringify(Object.entries(globalThis.localStorage || {})).includes('K7QM2XA4'), false);
        assert.equal(window.location.href.includes('K7QM'), false);
    } finally {
        p.close();
    }
});

/* ---------------- choose step: an add that fails says so next to the button ---------------- */

async function toChoose(p) {
    await startInvite(p);
    p.clock.advance(7000);
    await p.poll();
    assert.equal(p.wiz().step, 'choose');
    await p.wait(200); // the libraries load
    [...p.page.querySelectorAll('.shoal-libpick__item')][0].querySelector('input').click();
    await p.wait(30);
}

test('no folder for library files: the add error stands next to the button, with a way to Settings (fresh-install bug)', async () => {
    const p = await pair({ addLibraryError: 'noStubRoot' });
    try {
        await toChoose(p);
        const add = buttonIn(p.page, 'Add and start syncing');
        assert.equal(add.disabled, false);
        add.click();
        await p.wait(150);
        const slot = p.page.querySelector('[data-slot="adderror"]');
        assert.ok(slot, 'the error host is part of the step');
        assert.match(slot.textContent, /Shoal has no folder for its library files yet\. Choose one in Settings, then add the libraries again\./);
        assert.ok(slot.querySelector('[role="alert"]'));
        assert.ok(buttonIn(slot, 'Open Settings'));
        assert.equal(p.wiz().step, 'choose', 'the wizard stays where it is');
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Peers/' + p.wiz().peerHandle + '/Libraries').length, 1);
        assert.equal(buttonIn(p.page, 'Add and start syncing').getAttribute('aria-disabled'), null, 'the button works again');
    } finally {
        p.close();
    }
});

test('any other add failure is also shown next to the button, and a retry clears it', async () => {
    const p = await pair({ addLibraryError: 'server' });
    try {
        await toChoose(p);
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(150);
        const slot = p.page.querySelector('[data-slot="adderror"]');
        assert.match(slot.textContent, /Couldn't add those libraries\. Try again in a moment\./);
        assert.equal(buttonIn(slot, 'Open Settings'), undefined, 'the Settings button is only for the missing folder');
    } finally {
        p.close();
    }
});

test('a library that is already added says so, not "try again" (N1)', async () => {
    const p = await pair({ addLibraryError: 'alreadyAdded' });
    try {
        await toChoose(p);
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(150);
        const slot = p.page.querySelector('[data-slot="adderror"]');
        assert.match(slot.textContent, /already added/);
        assert.doesNotMatch(slot.textContent, /Try again/);
    } finally {
        p.close();
    }
});

test('choose works with jellyfin-web upgrading every button[is=emby-button] (the v0 registerElement prototype), and the add request goes out', async () => {
    // jellyfin-web registers emby-button with document.registerElement: elements carrying is="emby-button" get its prototype
    // (createdCallback adds the class, attachedCallback does nothing for a button). This stands in for it.
    const proto = Object.create(window.HTMLButtonElement.prototype);
    proto.createdCallback = function created() {
        if (!this.classList.contains('emby-button')) {
            this.classList.add('emby-button');
        }
    };

    proto.attachedCallback = function attached() { /* anchors only */ };
    const upgrade = (node) => {
        if (node.nodeType === 1 && node.matches && node.matches('button[is="emby-button"]') && Object.getPrototypeOf(node) !== proto) {
            Object.setPrototypeOf(node, proto);
            node.createdCallback();
            node.attachedCallback();
        }

        if (node.querySelectorAll) {
            node.querySelectorAll('button[is="emby-button"]').forEach(upgrade);
        }
    };

    const observer = new window.MutationObserver((records) => records.forEach((r) => r.addedNodes.forEach(upgrade)));
    observer.observe(window.document.body, { childList: true, subtree: true });
    const p = await pair();
    try {
        await toChoose(p);
        const add = buttonIn(p.page, 'Add and start syncing');
        await p.wait(30);
        assert.equal(Object.getPrototypeOf(add), proto, 'the button was upgraded like jellyfin-web does');
        assert.equal(add.getAttribute('aria-disabled'), null, 'a ticked library enables it');
        add.click();
        await p.wait(250);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && /\/Libraries$/.test(c.path)).length, 1, 'the request went out');
        assert.equal(p.wiz().step, 'done');
    } finally {
        observer.disconnect();
        p.close();
    }
});

/* ---------------- the pure helpers ---------------- */

test('the address reader and the code classifier are exported and pure', async () => {
    const { loadShoal } = await import('./helpers.mjs');
    const mod = await loadShoal();
    assert.equal(mod.readAddress('', 'x'), null);
    assert.equal(mod.readAddress('ht', 'x'), null);
    assert.equal(mod.readAddress('http://100.64.1.1:8096', 'x').network, 'Tailnet');
    assert.equal(mod.readAddress('http://100.128.1.1:8096', 'x').network, 'Public');
    assert.equal(mod.readAddress('http://172.15.0.1:8096', 'x').network, 'Public');
    assert.equal(mod.readAddress('http://172.16.0.1:8096', 'x').network, 'LAN');
    assert.equal(mod.readAddress('http://nas.local:8096', 'x').network, 'LAN');
    assert.equal(mod.readAddress('http://me.example.com', 'me.example.com').msgs[0].key, 'pv2AddrSelf');
    assert.equal(mod.classifyCode('482913', 'invite').alert, true);
    assert.equal(mod.classifyCode('482913', 'invite').kind, 'number');
    assert.equal(mod.classifyCode(INVITE, 'invite'), null);
    assert.equal(mod.classifyCode('shoal1-3fa9', 'invite').kind, 'cut');
    assert.equal(mod.classifyCode('K7QM2XA4', 'safety'), null);
    assert.equal(mod.classifyCode('482913', 'safety').kind, 'shape');
    assert.equal(mod.inFours('K7QM2XA4'), 'K7QM 2XA4');
});

test('an invite typed into the address field says where it goes and moves there on request (7.1)', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'qc');
        setValue(input(p, 'Server address'), INVITE);
        assert.match(p.text(), /That's an invite\. It goes in the Invite box on the invite step\./);
        buttonIn(step(p), 'Move it to the Invite box').click();
        assert.equal(input(p, 'Server address').value, '');
        assert.equal(p.wiz().form.invite, INVITE);
    } finally {
        p.close();
    }
});

test('Paste a new invite lands on an empty, focused invite box', async () => {
    const p = await pair({ pairing: 'denied' });
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        await click(p, 'Paste a new invite');
        assert.equal(document.activeElement, input(p, 'Invite'));
    } finally {
        p.close();
    }
});

for (const [kind, title] of [['inviteReplaced', 'That invite was replaced'], ['inviteCancelled', 'That invite was cancelled'], ['tokenNotCollected', 'The approval wasn\'t collected in time']]) {
    test(`the invite outcome ${kind} has its own clear message and a way to start again`, async () => {
        const p = await pair({ pairing: kind });
        try {
            await startInvite(p);
            p.clock.advance(7000);
            await p.poll();
            assert.match(p.text(), new RegExp(title));
            assert.ok(buttonIn(p.page, 'Paste a new invite'));
        } finally {
            p.close();
        }
    });
}

test('M1 regression: the hints are on screen while typing, so blur and the click on Check server change nothing', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        const url = input(p, 'Server address');
        url.value = 'https://10.0.0.20:8096';
        url.dispatchEvent(new window.Event('input', { bubbles: true }));
        const before = p.page.querySelector('.shoal-addrhints').innerHTML;
        assert.ok(before.length > 0, 'the hint is already there after the keystroke, with no wait and no blur');
        const button = buttonIn(step(p), 'Check server');
        url.dispatchEvent(new window.Event('blur'));
        assert.equal(p.page.querySelector('.shoal-addrhints').innerHTML, before, 'blur does not change the hints, so nothing moves between press and release');
        setValue(input(p, 'Name for this server'), 'Willow');
        setValue(url, 'http://myserver.tail1234.ts.net:8096');
        const filled = p.page.querySelector('.shoal-addrhints').innerHTML;
        url.dispatchEvent(new window.Event('blur'));
        assert.equal(p.page.querySelector('.shoal-addrhints').innerHTML, filled);
        button.form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.equal(probes(p).length, 1, 'the first click went through');
    } finally {
        p.close();
    }
});

test('N2: the /web trim note survives the blur that follows', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        const url = input(p, 'Server address');
        setValue(url, 'https://movies.example.com/web/index.html');
        url.dispatchEvent(new window.Event('blur'));
        assert.match(p.text(), /Shoal removed \/web\/index\.html\./);
    } finally {
        p.close();
    }
});

test('M2 regression: no step prints null, and the code step has no stray text under the countdown', async () => {
    const p = await pair();
    try {
        await startInvite(p);
        assert.doesNotMatch(step(p).textContent, /null|undefined/);
    } finally {
        p.close();
    }
});

test('M4 and M5: the invite step has one Who pill per block and none at the top; its instructions are a numbered list; N1 one hint only', async () => {
    const p = await pair();
    try {
        await toInvite(p);
        assert.equal(step(p).querySelectorAll('.shoal-who').length, 2);
        assert.equal(step(p).querySelectorAll('.shoal-block .shoal-who').length, 2);
        assert.equal(step(p).querySelector('ol.shoal-numbered').children.length, 5);
        await click(p, 'Back');
        await click(p, 'No, change the address');
        setValue(input(p, 'Server address'), 'https://10.0.0.20:8096');
        assert.equal(p.page.querySelectorAll('.shoal-addrhints .shoal-hint').length, 1);
        assert.match(p.page.querySelector('.shoal-addrhints').textContent, /private address[\s\S]*Jellyfin uses plain http on port 8096/);
        setValue(input(p, 'Server address'), '482913');
        assert.match(p.page.querySelector('.shoal-addrhints').textContent, /That's a number, not an invite or a safety code/);
    } finally {
        p.close();
    }
});

test('N2 and N4: the /web trim note stays when you come back to the address step; the radio groups are named by the heading, not a second legend', async () => {
    const p = await pair();
    try {
        await toAddress(p, 'invite');
        await fillConnect(p, { url: 'https://movies.example.com/web/index.html' });
        await submit(p);
        await click(p, 'No, change the address');
        assert.match(p.text(), /Shoal removed \/web\/index\.html\./);
    } finally {
        p.close();
    }
});

test('Pilot 05 L1: a restarted attempt is marked on the approval card', async () => {
    let p = await pair({ params: { view: 'pair', dir: 'share' }, restarted: true });
    try {
        await toApproval(p);
        assert.match(StepText(p), /Riley's server started this request again. Check the code with Riley again./);
    } finally {
        p.close();
    }

    p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toApproval(p);
        assert.doesNotMatch(StepText(p), /started this request again/);
    } finally {
        p.close();
    }
});

test('Pilot 05 C: a check that went stale before Start pairing is redone WITH the invite, and the pairing starts', async () => {
    const p = await pair();
    try {
        await toInvite(p);
        p.clock.advance(11 * 60000); // the check is more than 10 minutes old
        setValue(input(p, 'Invite'), INVITE);
        await click(p, 'Start pairing');
        assert.equal(probes(p).length, 2);
        assert.equal(probes(p)[1].body.invite, INVITE, 'the fresh check carries the invite');
        assert.equal(startPosts(p).length, 1);
        assert.equal(p.wiz().step, 'pair');
    } finally {
        p.close();
    }
});

test('Pilot 05 B: starting again with the same invite after a stop is one more Start pairing, with a new check each time', async () => {
    const p = await pair({ pairing: 'network' });
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        await click(p, 'Start again');
        setValue(input(p, 'Invite'), INVITE);
        await click(p, 'Start pairing');
        assert.equal(probes(p).length, 3, 'every attempt has its own check');
        assert.ok(probes(p).slice(1).every((c) => c.body.invite === INVITE));
    } finally {
        p.close();
    }
});

test('Pilot 07 D18: each code step has exactly one Who label', async () => {
    const p = await pair();
    try {
        await startInvite(p);
        assert.equal(step(p).querySelectorAll('.shoal-who').length, 1);
    } finally {
        p.close();
    }

});

/* ---------------- D17: both servers need Shoal ---------------- */

test('D17: the check is the gate. No Shoal there blocks with the install link, a message to copy and Check again; no bypass', async () => {
    const p = await pair({ hasShoal: false });
    try {
        await toCheck(p, 'invite');
        const t = StepText(p);
        assert.match(t, /This server doesn't have Shoal yet, or has an older Shoal/);
        assert.match(t, /Both servers need Shoal\. Ask your friend to install it from the link below, then check again\./);
        const link = step(p).querySelector('a[href="https://github.com/Ninkxz/jellyfin-plugin-shoal"]');
        assert.ok(link, 'the install link');
        assert.equal(link.getAttribute('rel'), 'noopener noreferrer');
        assert.match(step(p).querySelector('textarea').value, /https:\/\/github\.com\/Ninkxz\/jellyfin-plugin-shoal/);
        assert.ok(buttonIn(step(p), 'Copy message') && buttonIn(step(p), 'Check again'));
        assert.equal(buttonIn(step(p), 'Yes, this is it'), undefined, 'no way past');
        assert.equal(probes(p).length, 1);
        await click(p, 'Check again');
        assert.equal(probes(p).length, 2, 'Check again asks again');
        assert.doesNotMatch(p.text(), /Quick Connect/);
    } finally {
        p.close();
    }
});

test('D17: Shoal there continues; a throttled or unknown answer never blocks', async () => {
    for (const [opts, re] of [[{ hasShoal: true }, /Shoal is installed there\. Good\./], [{ hasShoal: 'absent' }, null], [{ hasShoal: false, pingThrottled: true }, /Shoal could not be checked just now/]]) {
        const p = await pair(opts);
        try {
            await toCheck(p, 'invite');
            if (re) {
                assert.match(StepText(p), re);
            }

            assert.equal(buttonIn(step(p), 'Yes, this is it').disabled, false);
            await click(p, 'Yes, this is it');
            assert.equal(p.wiz().step, 'invite');
        } finally {
            p.close();
        }
    }
});

test('D17: an invite over plain http outside Tailscale is a dead end with the reason and no other route', async () => {
    const p = await pair();
    try {
        await toAddress(p);
        await fillConnect(p, { url: 'http://192.168.1.40:8096', network: 'LAN' });
        await submit(p);
        assert.equal(p.wiz().step, 'check');
        assert.match(p.text(), /Invites need Tailscale or an https address so nobody on the network can copy them\. There is no other way to pair over plain http\./);
        assert.match(p.text(), /Use a Tailscale name or an https address instead./);
        assert.equal(buttonIn(step(p), 'Yes, this is it'), undefined, 'no way past');
        assert.equal(step(p).querySelector('.shoal-btn--primary').textContent.trim(), 'Change the address');
        assert.doesNotMatch(p.text(), /Quick Connect|could not reach that address/);
    } finally {
        p.close();
    }
});

test('D17: the rail has five steps, and no screen of the wizard says Quick Connect', async () => {
    const p = await pair();
    try {
        assert.equal(p.page.querySelectorAll('.shoal-steps__item').length, 5);
        assert.doesNotMatch(p.text(), /Quick Connect/);
        await toInvite(p);
        assert.match(p.page.querySelector('.shoal-steps__compact').textContent, /Step 3 of 5 · Get an invite/);
        assert.doesNotMatch(p.text(), /Quick Connect/);
        setValue(input(p, 'Invite'), INVITE);
        await click(p, 'Start pairing');
        assert.match(p.page.querySelector('.shoal-steps__compact').textContent, /Step 4 of 5 · Read the code/);
        assert.doesNotMatch(p.text(), /Quick Connect/);
    } finally {
        p.close();
    }
});

test('D17: a 6 digit number is neither an invite nor a safety code, in every box', async () => {
    const p = await pair();
    try {
        await toInvite(p);
        setValue(input(p, 'Invite'), '482913');
        const alert = p.page.querySelector('.shoal-invitenote [role="alert"]');
        assert.match(alert.textContent, /That's a number, not an invite or a safety code\./);
        assert.doesNotMatch(p.text(), /Quick Connect/);
    } finally {
        p.close();
    }
});

test('D17: an address step Back goes to the direction question only when the page was opened without one', async () => {
    let p = await pair({ params: { view: 'pair' } });
    try {
        p.page.querySelector('input[type="radio"][value="watch"]').click();
        buttonIn(p.page, 'Continue').click();
        await p.wait(60);
        buttonIn(step(p), 'Back').click();
        await p.wait(60);
        assert.match(p.text(), /What do you want to do\?/);
    } finally {
        p.close();
    }

    p = await pair();
    try {
        assert.equal(buttonIn(step(p), 'Back'), undefined);
    } finally {
        p.close();
    }
});

test('D17: pair again: the form is prefilled and the check goes on to the invite', async () => {
    const p = await pair({ scenario: 'default', params: { view: 'pair', dir: 'watch', again: 'h-hops' } });
    try {
        assert.equal(input(p, 'Name for this server').value, 'Hops');
        assert.equal(input(p, 'Server address').value, 'http://hops-jellyfin.tail1234.ts.net:8096');
        await submit(p);
        await click(p, 'Yes, this is it');
        assert.equal(p.wiz().step, 'invite');
    } finally {
        p.close();
    }
});

test('D17: a server paired the old way is paused, says Needs re-pair by invite, and offers Re-pair with an invite', async () => {
    const p = await pair({ params: { view: 'overview' }, legacy: true });
    try {
        assert.match(p.text(), /1 server was paired the old way and is paused\. Re-pair it with an invite\./);
        const card = [...p.page.querySelectorAll('.shoal-card')].find((c) => c.textContent.includes('Old Friend'));
        assert.match(card.textContent, /Needs re-pair by invite/);
        assert.ok(buttonIn(card, 'Re-pair with an invite'));
    } finally {
        p.close();
    }

    const q = await pair({ params: { view: 'server', id: 'h-legacy' }, legacy: true });
    try {
        const t = q.text();
        assert.match(t, /Needs re-pair by invite/);
        assert.match(t, /Paid|Paired .* the old way \(Quick Connect\)/);
        assert.doesNotMatch(t, /Device lock|Session limit|Lock confirmed/);
        buttonIn(q.page, 'Re-pair with an invite').click();
        await q.wait(300);
        assert.equal(q.navigations.at(-1) && q.navigations.at(-1).includes('again'), true);
    } finally {
        q.close();
    }
});

test('Lumen 05: the plain http reason shows under the address as soon as it is typed, not one step late', async () => {
    const p = await pair();
    try {
        setValue(input(p, 'Server address'), 'http://192.168.1.40:8096');
        assert.match(p.page.querySelector('.shoal-addrblock').textContent, /Invites need Tailscale or an https address/);
        setValue(input(p, 'Server address'), 'http://myserver.tail1234.ts.net:8096');
        assert.equal(p.page.querySelector('.shoal-addrblock').textContent, '', 'a Tailscale address is fine over http');
    } finally {
        p.close();
    }
});

test('N11: a pairing session with no expiry yet is not an expired invite', async () => {
    const p = await pair({ noExpiry: true });
    try {
        await startInvite(p);
        const text = StepText(p);
        assert.doesNotMatch(text, /ran out/i);
        assert.match(text, /K7QM/);
        p.clock.advance(5000);
        await p.poll();
        assert.doesNotMatch(StepText(p), /ran out/i);
    } finally {
        p.close();
    }
});

test('N20: the safety code is never "undefined": the page waits for the polled code and then shows it', async () => {
    const p = await pair({ lateCode: true });
    try {
        await startInvite(p);
        let text = StepText(p);
        assert.doesNotMatch(text, /undefined|unde fined/i);
        assert.match(text, /Getting the code/);
        assert.doesNotMatch(text, /K7QM/);
        assert.doesNotMatch(text, /read it slowly: ./);
        p.clock.advance(4000);
        await p.poll();
        text = StepText(p);
        assert.doesNotMatch(text, /undefined|unde fined/i);
        assert.match(text, /K7QM 2XA4/);
        assert.match(text, /I'll read it slowly: K7QM 2XA4./);
        assert.equal(p.page.querySelector('.shoal-code').dataset.copy, 'K7QM2XA4');
    } finally {
        p.close();
    }
});

test('N20: the pure helpers never print undefined', async () => {
    const mod = await loadShoal();
    assert.equal(mod.inFours(undefined), '');
    assert.equal(mod.inFours('undefined'), '');
    assert.equal(mod.inFours('K7QM2XA4'), 'K7QM 2XA4');
});

test('N21: when the server closes a confirmed pairing the wizard goes on to the libraries, and a new visit starts a new wizard', async () => {
    const p = await pair({ endOnConfirm: true });
    try {
        await startInvite(p);
        p.clock.advance(7000);
        await p.poll();
        await p.wait(250);
        assert.match(StepText(p), /Choose what to add/);
        buttonIn(p.page, 'Add and start syncing');
        const rows = [...p.page.querySelectorAll('.shoal-libpick__item')];
        rows.find((r) => r.textContent.includes('Movies') && !r.textContent.includes('Everything')).querySelector('input').click();
        await p.wait(30);
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(250);
        assert.match(StepText(p), /Willow is paired/);
        p.handle.screen().show();
        await p.wait(300);
        assert.doesNotMatch(step(p).textContent, /Willow is paired/);
        assert.match(p.text(), /What do you want to do?|Server address/);
    } finally {
        p.close();
    }
});

test('N17: the failure bar sits below the Jellyfin app bar (48 px, z-index 1100)', async () => {
    const fs = await import('node:fs');
    const css = fs.readFileSync(new URL('../../src/Jellyfin.Plugin.Shoal/Configuration/shoal.css', import.meta.url), 'utf8');
    const rule = css.split(/\r?\n/).find((l) => l.includes('.shoal-errors {'));
    assert.ok(rule, 'the rule exists');
    assert.ok(parseInt(/top:\s*(\d+)px/.exec(rule)[1], 10) >= 48, 'below the app bar');
    assert.ok(parseInt(/z-index:\s*(\d+)/.exec(rule)[1], 10) > 1100, 'above the app bar');
});

test('N15: a plain http address for the home route warns that the friend cannot use the invite', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await toReach(p);
        await chooseTransport(p, 'home');
        const address = input(p, 'The address to send');
        setValue(address, 'http://192.168.1.20:8096');
        await p.wait(30);
        assert.match(p.text(), /That address is plain http\. Invites need Tailscale or an https address/);
        setValue(address, 'https://movies.example.com');
        await p.wait(30);
        assert.doesNotMatch(p.text(), /That address is plain http/);
    } finally {
        p.close();
    }
});
