// U3 / U7: Pair a server, both directions (ui-spec 8.2, DESIGN 4.1b, 4.2, 4.2a) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, JUNK } from './helpers.mjs';

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
const input = (p, label) => field(p, label).querySelector('input, select');
const dialog = (p) => p.page.querySelector('dialog[open]');

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

async function chooseMethod(p, method) {
    p.page.querySelector(`input[type="radio"][value="${method}"]`).click();
    await p.wait(30);
}

async function fillConnect(p, o) {
    if (o.invite !== undefined) {
        setValue(input(p, 'Invite'), o.invite);
    }

    setValue(input(p, 'Name for this server'), o.name || 'Willow');
    setValue(input(p, 'Server address'), o.url || 'http://willow.tail1234.ts.net:8096');
    if (o.network) {
        setValue(input(p, 'How you reach it'), o.network);
    }
}

const submit = async (p) => {
    p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
    await p.wait(120);
};

const INVITE = 'shoal1-3fa91c7e-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P';

async function toCheck(p, method, o) {
    await chooseMethod(p, method);
    await fillConnect(p, Object.assign({ invite: method === 'invite' ? INVITE : undefined }, o || {}));
    await submit(p);
}

const StepText = (p) => {
    const text = p.page.querySelector('.shoal-step').textContent.replace(/\s+/g, ' ');
    assert.doesNotMatch(text, JUNK, 'junk text on the step');
    return text;
};

/* ---------------- step 0 and 1 ---------------- */

test('step 0 asks the direction in plain words and waits for a choice', async () => {
    const p = await pair({ params: { view: 'pair' } });
    try {
        const t = p.text();
        assert.match(t, /What do you want to do\?/);
        assert.match(t, /Their movies show up in your library\. They keep the files, and they don't need Shoal\./);
        assert.match(t, /Your friend needs Shoal too\. You choose which libraries and can stop anytime\./);
        assert.match(t, /To do both, pair once in each direction\./);
        assert.equal(buttonIn(p.page, 'Continue').disabled, true);
        assert.match(t, /Choose one to continue/);
    } finally {
        p.close();
    }
});

test('step 1: the two ways to connect, the fields in order, and the invite input', async () => {
    const p = await pair();
    try {
        assert.match(p.text(), /How will you connect\?/);
        assert.match(p.text(), /My friend sent me an invite/);
        assert.match(p.text(), /I have their server address/);
        await chooseMethod(p, 'invite');
        const labels = [...p.page.querySelectorAll('.shoal-field > label')].filter((l) => !l.closest('details')).map((l) => l.textContent.trim());
        assert.deepEqual(labels, ['Invite', 'Name for this server', 'Server address', 'How you reach it']);
        assert.match(p.text(), /Starts with shoal1-\. Your friend makes it in Shoal under Share with a friend\. It works once, for 15 minutes\./);
        assert.match(p.text(), /Your friend's server address\. The invite only works there\./);
        const inv = input(p, 'Invite');
        assert.equal(inv.getAttribute('autocapitalize'), 'characters');
        assert.equal(inv.getAttribute('spellcheck'), 'false');
        assert.equal(inv.getAttribute('autocomplete'), 'off');
    } finally {
        p.close();
    }
});

test('an invite on a home network over http is refused at once, with the reason', async () => {
    const p = await pair();
    try {
        await chooseMethod(p, 'invite');
        await fillConnect(p, { invite: INVITE, url: 'http://192.168.1.40:8096', network: 'LAN' });
        assert.match(p.text(), /Invites need Tailscale or an https address, so nobody on the network can copy them\. On a home network over http, choose I have their server address instead\./);
        await submit(p);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Peers/Probe').length, 0);
        assert.equal(p.wiz().step, 'connect');
        // The acknowledgement row does not exist on the invite path.
        assert.doesNotMatch(p.text(), /I understand Shoal's key travels unencrypted/);
    } finally {
        p.close();
    }
});

test('the home network acknowledgement is for the Quick Connect path, and required over http', async () => {
    const p = await pair();
    try {
        await chooseMethod(p, 'qc');
        await fillConnect(p, { url: 'http://192.168.1.40:8096', network: 'LAN' });
        assert.match(p.text(), /I understand Shoal's key travels unencrypted on my home network/);
        assert.match(p.text(), /Anyone on your network could copy it\. Tailscale avoids this\./);
        await submit(p);
        assert.match(p.text(), /Tick the box to continue, or use Tailscale\./);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Peers/Probe').length, 0);
    } finally {
        p.close();
    }
});

test('field checks use the spec sentences', async () => {
    const p = await pair();
    try {
        await chooseMethod(p, 'invite');
        await submit(p);
        const t = p.text();
        assert.match(t, /Paste the invite your friend sent\./);
        assert.match(t, /Enter a name\./);
        assert.match(t, /Enter the server address\./);
        setValue(input(p, 'Invite'), 'hello');
        setValue(input(p, 'Name for this server'), 'Willow');
        setValue(input(p, 'Server address'), 'ftp://x');
        await submit(p);
        assert.match(p.text(), /That doesn't look like a Shoal invite\. It starts with shoal1-\./);
        assert.match(p.text(), /Use an address that starts with http:\/\/ or https:\/\//);
        setValue(input(p, 'Invite'), INVITE);
        setValue(input(p, 'Server address'), 'http://willow.example.net');
        setValue(input(p, 'How you reach it'), 'Public');
        await submit(p);
        assert.match(p.text(), /Internet servers need an https:\/\/ address\./);
    } finally {
        p.close();
    }
});

test('probe errors land on the field or the form with the spec words', async () => {
    const cases = {
        blocked: /Shoal can't connect to that address\. It points to this server itself or to a private service\./,
        wrongNetwork: /That address isn't a Tailscale address\./,
        unreachable: /Couldn't reach that server\. Check the address, and that Tailscale is running on this server\./,
        certificate: /That server's certificate isn't trusted, so Shoal won't connect\./,
        notJellyfin: /That address answered, but it isn't a Jellyfin server\./,
        inviteWrongServer: /That invite is for a different server\. Check the address, or ask your friend for the right invite\./,
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

/* ---------------- step 2 ---------------- */

test('step 2: facts and the Server ID, never a safety code', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'invite');
        const t = StepText(p);
        assert.match(t, /Is this the right server\?/);
        assert.match(t, /Server name\s*Willow/);
        assert.match(t, /Jellyfin version\s*12\.1\.0/);
        assert.match(t, /Server ID\s*3fa91c7e/);
        assert.match(t, /The Server ID lets Shoal notice later if this address leads to a different or reset server\. It isn't a safety code\./);
        assert.doesNotMatch(t, /K7QM/);
        assert.doesNotMatch(t, /Safety code/);
        assert.ok(buttonIn(p.page, 'Start pairing'));
    } finally {
        p.close();
    }
});

test('step 2 warnings follow the path: untested version, home network, internet plus Quick Connect', async () => {
    let p = await pair();
    try {
        await toCheck(p, 'qc', { url: 'http://192.168.1.40:8096', network: 'LAN' });
        await p.page.querySelector('.shoal-check input').click();
        await submit(p);
        assert.match(StepText(p), /Home network connections aren't encrypted\./);
        assert.match(StepText(p), /Start when your friend is ready\. They'll have 10 minutes to enter the code\./);
    } finally {
        p.close();
    }

    p = await pair();
    try {
        await toCheck(p, 'qc', { url: 'https://untested.example.net', network: 'Public' });
        assert.match(StepText(p), /Strangers on the internet can try to grab Quick Connect codes\. If your friend installs Shoal and sends you an invite, pairing is safer\./);
        assert.match(StepText(p), /Shoal hasn't been tested with Jellyfin 12\.1\.0 yet\. It will probably work\./);
    } finally {
        p.close();
    }
});

test('Start pairing is the only click that asks for a code (arriving on step 2 never does)', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Pairing').length, 0);
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(120);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Pairing').length, 1);
        assert.deepEqual(Object.keys(p.mock.calls.find((c) => c.method === 'POST' && c.path === 'Pairing').body).sort(), ['displayName', 'method', 'probeHandle']);
    } finally {
        p.close();
    }
});

test('an expired check re-probes and stays on step 2; an active session says so', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        p.clock.advance(11 * 60000);
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(250);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Peers/Probe').length, 2, 'the page re-ran the check');
        assert.equal(p.wiz().step, 'check');
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Pairing').length, 1, 'only the refused start');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(150);
        assert.equal(p.wiz().step, 'pair');
    } finally {
        p.close();
    }
});

/* ---------------- step 3, invite path ---------------- */

test('invite path step 3: Home shows the 8-character code; there are no compare buttons', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'invite');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(150);
        const t = StepText(p);
        assert.match(t, /Read this code to your friend/);
        assert.match(t, /Call or text your friend and give them this code\. They type it into Shoal on Willow to approve\./);
        const code = p.page.querySelector('.shoal-code');
        assert.equal(code.getAttribute('aria-label'), 'Safety code K 7 Q M, 2 X A 4');
        assert.deepEqual([...code.querySelectorAll('.shoal-code__group')].map((g) => g.textContent), ['K7QM', '2XA4']);
        assert.ok(buttonIn(p.page, 'Copy code'));
        assert.match(t, /Waiting for your friend to approve on Willow…/);
        assert.match(t, /Expires in 15:00/);
        assert.doesNotMatch(t, /The codes match|They don't match/);
        assert.match(t, /Cancel pairing/);
        // The code is shown, copied by value without spaces.
        assert.equal(code.dataset.copy, 'K7QM2XA4');
        // Approval by the friend: the page moves on by itself.
        p.clock.advance(7000);
        await p.poll();
        assert.equal(p.wiz().step, 'choose');
        assert.match(p.text(), /Choose what to add/);
    } finally {
        p.close();
    }
});

/* ---------------- step 3, Quick Connect path ---------------- */

test('Quick Connect: no code before Shoal confirmed it; then both codes; then the compare step with its buttons', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(150);
        let t = StepText(p);
        assert.match(t, /Send this code to your friend/);
        assert.match(t, /Getting a code from Willow…/);
        assert.equal(p.page.querySelector('.shoal-code'), null, 'no code is visible yet');
        assert.equal(p.page.querySelector('.shoal-step__body').getAttribute('aria-busy'), 'true');

        p.clock.advance(800);
        await p.poll();
        t = StepText(p);
        const codes = [...p.page.querySelectorAll('.shoal-code')];
        assert.equal(codes.length, 2);
        assert.match(codes[0].getAttribute('aria-label'), /^Code \d \d \d \d \d \d$/);
        assert.equal(codes[1].getAttribute('aria-label'), 'Safety code K 7 Q M');
        assert.match(t, /After your friend approves, they'll see Shoal: Rich’s Jellyfin \(K7QM\) in their Devices list\. You'll compare it then\./);
        assert.match(t, /Waiting for Willow to approve…/);
        assert.match(t, /Expires in (10:00|9:59)/);
        assert.doesNotMatch(t, /Get a new code/);
        assert.doesNotMatch(t, /The codes match/);
        const steps = p.page.querySelector('details');
        assert.ok(steps.open, 'steps for your friend are open the first time');
        assert.match(steps.textContent, /Steps for your friend/);

        p.clock.advance(5000);
        await p.poll();
        t = StepText(p);
        assert.match(t, /Approved\. Shoal is connected to Willow\./);
        assert.match(t, /Confirm within 15:00/);
        assert.match(t, /Compare the safety code/);
        assert.match(t, /Ask your friend to open Dashboard > Devices on Willow and find Shoal: Rich’s Jellyfin \(K7QM\)\./);
        assert.match(t, /Then your friend locks the sharing account to that device/);
        assert.ok(buttonIn(p.page, 'The codes match') && buttonIn(p.page, 'They don\'t match'));
        buttonIn(p.page, 'The codes match').click();
        await p.wait(150);
        assert.equal(p.wiz().step, 'choose');
        assert.equal(p.mock.calls.filter((c) => c.path === 'Pairing/Confirm').at(-1).body.match, true);
    } finally {
        p.close();
    }
});

test('a silent friend server turns the status row warn and returns by itself', async () => {
    const p = await pair({ pairing: 'silent' });
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(100);
        p.clock.advance(1000);
        await p.poll();
        assert.match(StepText(p), /Waiting for Willow to approve…/);
        p.clock.advance(3000);
        await p.poll();
        assert.match(StepText(p), /Waiting for Willow to answer…/);
        assert.match(StepText(p), /Shoal keeps trying for 30 seconds\./);
        p.clock.advance(2500);
        await p.poll();
        assert.match(StepText(p), /Waiting for Willow to approve…/);
    } finally {
        p.close();
    }
});

test('"They don\'t match" saves nothing and shows the safety outcome with a stored card to dismiss (D4)', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(100);
        p.clock.advance(800);
        await p.poll();
        p.clock.advance(5000);
        await p.poll();
        buttonIn(p.page, "They don't match").click();
        await p.wait(150);
        const t = StepText(p);
        assert.match(t, /Pairing stopped for your safety/);
        assert.match(t, /The safety codes didn't match, so another device may have taken your place\. Shoal signed out of Willow and saved nothing\./);
        assert.match(t, /Ask your friend to delete the sharing account in Dashboard > Users and make a new one\. Then start again\./);
        assert.ok(buttonIn(p.page, 'Start again') && buttonIn(p.page, 'Dismiss'));
        // The server keeps a record that stays on the Overview card: Status says so.
        const status = (await p.mock.handle('GET', 'Status')).body;
        const rec = status.servers.find((s) => s.displayName === 'Willow');
        assert.equal(rec.status, 'PairingAborted');
        assert.equal(rec.pairingError, 'codesDidNotMatch');
        assert.equal(rec.paired, false);
        // After a safety stop, "Start again" asks for a new sharing account first.
        buttonIn(p.page, 'Start again').click();
        await p.wait(200);
        assert.equal(p.wiz().step, 'check');
        const start = buttonIn(p.page, 'Start pairing');
        assert.equal(start.disabled, true);
        assert.match(StepText(p), /Last time, someone else may have got in with the old sharing account\. Only a new account is safe\./);
        assert.match(StepText(p), /Tick the box once your friend has made a new account\./);
        const box = [...p.page.querySelectorAll('.shoal-check')].find((c) => /deleted the old sharing account/.test(c.textContent));
        box.querySelector('input').click();
        await p.wait(20);
        assert.equal(start.disabled, false);
        buttonIn(p.page.querySelector('.shoal-step'), 'Show the steps').click();
        await p.wait(60);
        assert.match(dialog(p).textContent, /In Dashboard > Users, add a new user for me/);
    } finally {
        p.close();
    }
});

/* ---------------- outcomes (8.2.5) ---------------- */

const OUTCOMES = [
    ['expired', 'qc', /The code expired/, /Codes last 10 minutes\. Start again when your friend is ready\./, ['Start again', 'Dismiss']],
    ['denied', 'invite', /Your friend chose Deny/, /turned down the request on Willow, so nothing was saved/, ['Start again', 'Dismiss']],
    ['codeMismatch', 'invite', /The safety code didn't match/, /typed a code that didn't match yours 3 times, so their server cancelled the request and the invite/, ['Start again', 'Dismiss']],
    ['adminAccount', 'qc', /That account is an admin on Willow/, /Shoal only accepts a limited sharing account/, ['Start again', 'Dismiss']],
    ['serverChanged', 'qc', /The server changed while pairing/, /isn't the one you checked/, ['Start again', 'Dismiss']],
    ['replaced', 'qc', /Pairing stopped for your safety/, /Your pairing request was replaced, or Willow restarted\. Tell your friend not to enter code \d{6}\./, ['Start again', 'Dismiss']],
    ['badPeerResponse', 'qc', /Pairing stopped for your safety/, /answered in a way Jellyfin never does/, ['Start again', 'Dismiss']],
    ['quickConnectOff', 'qc', /Quick Connect is off on Willow/, /turn on Quick Connect in Dashboard > General/, ['Start again', 'Dismiss']],
    ['sessionLimitBlocked', 'qc', /Your friend's server didn't accept the code/, /the session limit is probably still at 1/, ['Start again', 'Dismiss']],
    ['quickConnectUnreachable', 'qc', /Couldn't find Quick Connect on Willow/, /including any \/jellyfin at the end/, ['Change the address', 'Dismiss']],
    ['interrupted', 'qc', /The code expired/, /Your server restarted before your friend approved, so Shoal saved nothing\. The code may still work on Willow for a few minutes/, ['Start again', 'Dismiss']],
    ['interrupted', 'invite', /The invite expired/, /Your server restarted while waiting for your friend, so this invite can't be finished/, ['Start again', 'Dismiss']],
    ['network', 'qc', /Lost contact with Willow/, /couldn't reach Willow for 30 seconds while pairing/, ['Start again', 'Dismiss']],
    ['inviteInvalid', 'invite', /That invite didn't work/, /It may have expired, been used already, or been cancelled/, ['Start again']],
    ['server', 'qc', /Pairing didn't work/, /Something went wrong on your server\. Try again in a moment\./, ['Start again']]
];

for (const [kind, method, title, body, buttons] of OUTCOMES) {
    test(`outcome ${kind} (${method}): its title, its body, its actions`, async () => {
        const p = await pair({ pairing: kind });
        try {
            await toCheck(p, method);
            buttonIn(p.page, 'Start pairing').click();
            await p.wait(120);
            p.clock.advance(800);
            await p.poll();
            p.clock.advance(7000);
            await p.poll();
            const t = StepText(p);
            assert.match(t, title);
            assert.match(t, body);
            for (const b of buttons) {
                assert.ok(buttonIn(p.page.querySelector('.shoal-step'), b), b);
            }

            assert.doesNotMatch(t, /too many sessions|maximum number of sessions|disable the sharing account/i);
        } finally {
            p.close();
        }
    });
}

test('"Start again" on the invite path returns to step 1 with the address kept and the invite cleared', async () => {
    const p = await pair({ pairing: 'denied' });
    try {
        await toCheck(p, 'invite');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(120);
        p.clock.advance(7000);
        await p.poll();
        buttonIn(p.page, 'Start again').click();
        await p.wait(100);
        assert.equal(p.wiz().step, 'connect');
        assert.equal(input(p, 'Invite').value, '');
        assert.equal(input(p, 'Server address').value, 'http://willow.tail1234.ts.net:8096');
    } finally {
        p.close();
    }
});

test('Dismiss clears the stored card and returns to Overview', async () => {
    const p = await pair({ pairing: 'network' });
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(120);
        p.clock.advance(800);
        await p.poll();
        p.clock.advance(5000);
        await p.poll();
        buttonIn(p.page, 'Dismiss').click();
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
        assert.ok(buttonIn(p.page, 'Dismiss'));
    } finally {
        p.close();
    }
    const q2 = await pair({ scenario: 'problems', params: { view: 'pair', id: 'h-elm' } });
    try {
        assert.match(StepText(q2), /Shoal restarted before you confirmed/);
        assert.match(StepText(q2), /delete the device named Shoal: Rich’s Jellyfin in Dashboard > Devices on Elm/);
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

/* ---------------- step 4 and success ---------------- */

test('step 4: libraries, who can see them (only you), a required box for big libraries, and the success screen', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'invite');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(120);
        p.clock.advance(7000);
        await p.poll();
        await p.wait(250);
        const t = StepText(p);
        assert.match(t, /Choose what to add/);
        assert.match(t, /Pick libraries from Willow\. At first only you can see them, so you can try them\./);
        assert.match(t, /TV shows are coming in a later version\./);
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
        assert.doesNotMatch(done, /Remind your friend to finish/, 'that hint is for Quick Connect pairings');
    } finally {
        p.close();
    }
});

test('a Quick Connect pairing ends with the reminder to finish steps 7 to 9', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(100);
        p.clock.advance(800);
        await p.poll();
        p.clock.advance(5000);
        await p.poll();
        buttonIn(p.page, 'The codes match').click();
        await p.wait(200);
        [...p.page.querySelectorAll('.shoal-libpick__item')][0].querySelector('input').click();
        await p.wait(20);
        buttonIn(p.page, 'Add and start syncing').click();
        await p.wait(250);
        assert.match(StepText(p), /Remind your friend to finish: sign out of the private window, lock the sharing account to Shoal's device, then set its session limit to 1\./);
        assert.ok(buttonIn(p.page, 'Copy steps'));
    } finally {
        p.close();
    }
});

/* ---------------- steps for your friend (8.2.8) ---------------- */

test('steps for your friend: nine steps, the three groups, the note, step 9, and the truthful footer', async () => {
    const p = await pair();
    try {
        await toCheck(p, 'qc');
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(100);
        p.clock.advance(800);
        await p.poll();
        const d = p.page.querySelector('details');
        const t = d.textContent.replace(/\s+/g, ' ');
        assert.equal(d.querySelectorAll('.shoal-checklist > li').length, 9);
        assert.match(t, /Turn on: Hide this user from the sign-in screen\./);
        assert.match(t, /Turn off: Administrator\./);
        assert.match(t, /Leave for now: Maximum number of simultaneous user sessions stays at 0\. You'll change it in step 9\./);
        assert.match(t, /Jellyfin has no switch for these/);
        assert.match(t, /When I tell you the codes matched: in that user's settings, set Maximum number of simultaneous user sessions to 1, and save\./);
        assert.match(t, /To stop sharing: delete the user/);
        assert.match(t, /To pause for a while: turn the user off, and while it's off, give it a new long password\./);
        assert.match(t, /To pair again later: set the user's session limit back to 0/);
        assert.match(t, /If you see an error when you enter it, the session limit is probably still at 1\. Set it to 0, then try again\./);
        assert.match(t, /called Shoal: Rich’s Jellyfin \(K7QM\)/);
        assert.doesNotMatch(t, /disable the user|too many sessions|maximum number of sessions/i);
        // Every row has a status word for readers.
        assert.ok(d.querySelector('.shoal-checklist .shoal-sr'));
    } finally {
        p.close();
    }
});

test('"Copy steps" produces the plain-text format of 8.2.8', async () => {
    const { friendStepsForTest } = await import('./helpers.mjs').then(async (h) => ({ friendStepsForTest: (await h.loadShoal()).friendSteps }));
    const steps = friendStepsForTest({ home: 'Rich’s Jellyfin', code: 'K7QM' });
    const lines = steps.text.split('\n');
    assert.match(lines[0], /^1\. Give me access to your Jellyfin server/);
    assert.ok(lines.includes('   - Turn on: Hide this user from the sign-in screen. Allow remote connections. Allow media playback. Allow video and audio playback that needs transcoding, and video that only needs remuxing.'));
    assert.ok(lines.some((l) => /^ {3}Jellyfin has no switch for these: /.test(l)));
    assert.match(steps.text, /\n\nTo stop sharing: delete the user/);
    assert.match(steps.text, /6\. Open Dashboard > Devices\. There must be exactly one device for that user called Shoal: Rich’s Jellyfin \(K7QM\)/);
    const early = friendStepsForTest({ home: 'Rich’s Jellyfin', code: '' }).text;
    assert.match(early, /called Shoal: Rich’s Jellyfin \(and a 4-character code I'll give you\)/);
    const tail = friendStepsForTest({ home: 'Rich’s Jellyfin', code: 'K7QM', range: [7, 9] }).text;
    assert.match(tail, /^7\. Sign out of the private window\.\n8\./);
    assert.doesNotMatch(tail, /To stop sharing/);
});

/* ---------------- re-pairs ---------------- */

test('pair again: the form is prefilled, and step 2 carries the session-limit note', async () => {
    const p = await pair({ scenario: 'default', params: { view: 'pair', dir: 'watch', again: 'h-hops' } });
    try {
        assert.equal(input(p, 'Name for this server').value, 'Hops');
        assert.equal(input(p, 'Server address').value, 'http://hops-jellyfin.tail1234.ts.net:8096');
        await submit(p);
        assert.match(StepText(p), /Pairing again\? Ask your friend to set the sharing account's session limit to 0 first, and back to 1 once you're paired\./);
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(100);
        p.clock.advance(800);
        await p.poll();
        assert.match(StepText(p), /If your friend sees an error when they approve, the session limit is probably still at 1\. Ask them to set it to 0, then try again\./);
    } finally {
        p.close();
    }
});

/* ---------------- share direction ---------------- */

async function shareThroughInvite(p) {
    setValue(input(p, "Friend's name"), 'Riley');
    await p.wait(200);
    [...p.page.querySelectorAll('.shoal-choices--checks .shoal-check')].find((c) => c.textContent.includes('Movies') && !c.textContent.includes('Hops')).querySelector('input').click();
    await submit(p);
    return p;
}

test('share step 1: friend, libraries (movies only), quality and transcodes, the hidden-account note', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        const t = StepText(p);
        assert.match(t, /Who and what/);
        assert.match(t, /Shown only to you\./);
        assert.match(t, /Libraries from friends' servers can't be shared on\./);
        assert.match(t, /TV shows are coming in a later version\./);
        assert.match(t, /How many videos Riley's server|How many videos your friend's server can ask yours to convert at the same time\. Converting uses your CPU\. This is a limit, not a hard cap: audio and subtitle conversions aren't counted\./);
        assert.match(t, /Shoal creates a hidden sharing account for your friend with a random password nobody ever sees\./);
        const q1 = input(p, 'Highest quality per stream');
        assert.deepEqual([...q1.options].map((o) => o.textContent), ['No limit', '40 Mbps (4K)', '20 Mbps (1080p, high)', '10 Mbps (1080p)', '4 Mbps (720p)']);
        const libs = [...p.page.querySelectorAll('.shoal-choices--checks .shoal-check')];
        assert.ok(libs.find((c) => /TV shows/.test(c.textContent)).querySelector('input').disabled);
        assert.ok(libs.find((c) => /Hops Movies/.test(c.textContent)).querySelector('input').disabled);
        await shareThroughInvite(p);
        const call = p.mock.calls.find((c) => c.method === 'POST' && c.path === 'Shares');
        assert.deepEqual(call.body, { friendName: 'Riley', libraryIds: ['lib-movies'], maxBitrateMbps: null, maxConcurrentTranscodes: 4 });
    } finally {
        p.close();
    }
});

test('share step 2 to 4: an invite shown once, then the code typed by the admin, never displayed', async () => {
    const p = await pair({ params: { view: 'pair', dir: 'share' } });
    try {
        await shareThroughInvite(p);
        let t = StepText(p);
        assert.match(t, /Invite Riley/);
        assert.match(t, /How will Riley's server reach yours\?/);
        assert.equal(buttonIn(p.page, 'Create invite').disabled, true);
        p.page.querySelector('input[type="radio"][value="tailnet"]').click();
        await p.wait(20);
        buttonIn(p.page, 'Create invite').click();
        await p.wait(150);
        t = StepText(p);
        assert.ok(p.page.querySelector('.shoal-invitebox').textContent.includes('shoal1-3fa91c7e-K7QM-2XA4'), 'the invite is shown once');
        assert.match(t, /Shoal shows this invite only once\. Copy it now and send it privately, for example in Signal or a text\. It works one time, for 15 minutes\./);
        assert.match(t, /Their screen then shows a safety code, and you type it here to approve\./);
        assert.match(t, /Waiting for Riley to use the invite…/);
        assert.match(t, /Expires in 15:00/);
        assert.ok(buttonIn(p.page, 'Copy invite'));
        assert.ok(buttonIn(p.page, 'Create a new invite'));
        assert.ok(buttonIn(p.page, 'Cancel invite'));

        // The friend's server redeems it: the card appears, with an input and no code.
        p.clock.advance(8000);
        await p.poll();
        t = StepText(p);
        assert.match(t, /Sam’s Jellyfin wants to connect/);
        assert.match(t, /Server ID\s*3fa91c7e/);
        assert.match(t, /Connecting from\s*a Tailscale address/);
        assert.match(t, /Ask Riley to read you the safety code on their screen, by phone, text or in person\. Type it here to approve\. If you weren't expecting this, choose Deny\./);
        assert.doesNotMatch(t, /K7QM2XA4|K7QM 2XA4/);
        assert.doesNotMatch(t, /shoal1-/);
        assert.match(t, /Type the 8-character code to approve\./);
        const code = p.page.querySelector('.shoal-input--code');
        assert.equal(code.getAttribute('maxlength'), '11');
        assert.equal(code.getAttribute('autocapitalize'), 'characters');
        const approve = buttonIn(p.page, 'Approve');
        assert.equal(approve.disabled, true);
        assert.ok(buttonIn(p.page, 'Deny'));
        // The DOM never contains the invite once it is gone from the step.
        assert.equal(p.page.innerHTML.includes('shoal1-3fa91c7e'), false, 'the invite is not on the approval step');
    } finally {
        p.close();
    }
});

async function toApproval(p) {
    await shareThroughInvite(p);
    p.page.querySelector('input[type="radio"][value="tailnet"]').click();
    await p.wait(20);
    buttonIn(p.page, 'Create invite').click();
    await p.wait(150);
    p.clock.advance(8000);
    await p.poll();
}

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
        setValue(code, 'k7qm 2x');
        code.dispatchEvent(new window.Event('blur'));
        assert.match(p.text(), /Check the code\. It has 8 letters and numbers\./);
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
        assert.ok(buttonIn(p.page, 'Create invite'));
        const status = (await p.mock.handle('GET', 'Shares')).body;
        assert.ok(status.shares.some((s) => s.friendName === 'Riley'));
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
    const p = await pair({ scenario: 'hostile', pairing: 'quickConnectOff' });
    try {
        await toCheck(p, 'qc');
        const check = () => assert.equal(p.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        check();
        assert.ok(p.text().includes('<img src=x onerror=alert(1)>'));
        buttonIn(p.page, 'Start pairing').click();
        await p.wait(120);
        p.clock.advance(800);
        await p.poll();
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
        assert.ok(q2.page.querySelector('.shoal-approval h2').textContent.includes('<img src=x onerror=alert(1)>'));
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

/* ---------------- step 5: an add that fails says so next to the button ---------------- */

async function toChoose(p) {
    await toCheck(p, 'invite');
    buttonIn(p.page, 'Start pairing').click();
    await p.wait(150);
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

test('step 4 works with jellyfin-web upgrading every button[is=emby-button] (the v0 registerElement prototype), and the add request goes out', async () => {
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
