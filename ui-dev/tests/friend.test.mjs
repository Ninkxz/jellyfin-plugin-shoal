// U7: Friend detail, the sharing side (ui-spec 8.7, 6.18, DESIGN 4.1b, 4.5) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock } from './helpers.mjs';

async function friend(id, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    const p = await mountPage(Object.assign({ scenario: 'default', params: { view: 'friend', id }, settleMs: 250, now: clock.now }, extra || {}));
    p.clock = clock;
    return p;
}

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

test('header: name, status chip, sharing account and the help under Pause', async () => {
    const p = await friend('f-sam');
    try {
        assert.equal(p.page.querySelector('[data-shoal-view="friend"] h1').textContent, 'Sam');
        const header = p.page.querySelector('.shoal-detail__header');
        assert.match(header.textContent, /Waiting for friend/);
        assert.match(header.textContent, /Sharing account shoal-sam · since /);
        assert.match(header.textContent, /Signs Sam's server out at once\. Its titles stay listed on their side but won't play\./);
        assert.ok(buttonIn(header, 'Pause'));
    } finally {
        p.close();
    }
});

test('Pause and Resume are one click, announced, with the Resume help text', async () => {
    const p = await friend('f-sam');
    try {
        buttonIn(p.page, 'Pause').click();
        await p.wait(120);
        assert.equal(calls(p, 'POST', /Shares\/f-sam\/Pause$/).length, 1);
        assert.equal(dialog(p), null, 'reversible: no confirmation');
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Sam paused/);
        const header = p.page.querySelector('.shoal-detail__header');
        assert.match(header.textContent, /Paused/);
        assert.match(header.textContent, /Shoal first signs out any device that isn't Sam's server, then lets it back in\./);
        buttonIn(p.page, 'Resume').click();
        await p.wait(120);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Sam resumed/);
    } finally {
        p.close();
    }
});

test('access removed outside Shoal says what to do', async () => {
    const p = await friend('f-lee', { scenario: 'problems' });
    try {
        assert.match(p.page.querySelector('.shoal-detail__header').textContent, /Access removed/);
        assert.match(p.text(), /Lee's sharing account was turned off or deleted outside Shoal\./);
    } finally {
        p.close();
    }
});

test('devices: the expected one is named, the unexpected one is flagged, removal asks first', async () => {
    const p = await friend('f-jo');
    try {
        const text = p.text();
        assert.match(text, /Only Jo's server should be here\. Shoal signs out anything else within a minute\./);
        assert.match(text, /Jo's server/);
        assert.match(text, /Unexpected device signed out/);
        const unexpected = p.page.querySelector('.shoal-row.is-unexpected');
        assert.ok(unexpected);
        buttonIn(unexpected, 'Remove').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Remove this device\?/);
        assert.match(d.textContent, /If it's Jo's server, they'll need a new invite\./);
        assert.equal(calls(p, 'DELETE', /Devices/).length, 0);
        buttonIn(d, 'Remove device').click();
        await p.wait(150);
        assert.equal(calls(p, 'DELETE', /Devices\/d-3$/).length, 1);
    } finally {
        p.close();
    }
});

test('a fresh invite is shown once, never returns after leaving the page, and the page then says it is active', async () => {
    const p = await friend('f-sam');
    try {
        assert.match(p.text(), /To connect Sam's server, or to pair it again, create an invite\./);
        buttonIn(p.page, 'Create invite').click();
        await p.wait(200);
        assert.ok(p.page.innerHTML.includes('shoal1-3fa91c7e'), 'shown once');
        assert.match(p.text(), /Copy invite/);
        // Leaving the page drops it from the DOM.
        p.page.dispatchEvent(new window.CustomEvent('viewhide', { bubbles: true }));
        await p.wait(60);
        assert.equal(p.page.innerHTML.includes('shoal1-3fa91c7e'), false);
        p.page.dispatchEvent(new window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: true } }));
        await p.wait(200);
        assert.match(p.text(), /An invite is active · expires in /);
        assert.equal(p.page.innerHTML.includes('shoal1-3fa91c7e'), false);
    } finally {
        p.close();
    }
});

test('approval: Approve needs the typed code; a miss counts down; the third miss cancels; the code is never in the page', async () => {
    const p = await friend('f-sam', { scenario: 'problems' });
    try {
        const code = p.page.querySelector('.shoal-input--code');
        assert.ok(code, 'approval card');
        assert.match(p.text(), /Sam.s Jellyfin/);
        const approve = buttonIn(p.page, 'Approve');
        assert.equal(approve.disabled, true);
        setValue(code, 'AAAA 1111');
        assert.equal(approve.disabled, false);
        approve.click();
        await p.wait(150);
        assert.match(p.text(), /2 tries left|2 attempts left|2 tries/);
        setValue(p.page.querySelector('.shoal-input--code'), 'AAAA 2222');
        buttonIn(p.page, 'Approve').click();
        await p.wait(150);
        setValue(p.page.querySelector('.shoal-input--code'), 'AAAA 3333');
        buttonIn(p.page, 'Approve').click();
        await p.wait(250);
        assert.match(p.text(), /Request cancelled/);
        assert.equal(JSON.stringify(p.mock.calls.filter((c) => c.method === 'GET')).includes('K7QM'), false);
    } finally {
        p.close();
    }
});

test('approval: the right code approves and sends the typed text as sas', async () => {
    const p = await friend('f-sam', { scenario: 'problems' });
    try {
        setValue(p.page.querySelector('.shoal-input--code'), 'k7qm 2xa4');
        buttonIn(p.page, 'Approve').click();
        await p.wait(250);
        const post = calls(p, 'POST', /Approval$/).at(-1);
        assert.deepEqual(post.body, { approve: true, sas: 'K7QM2XA4' });
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /approved|Approved/i);
    } finally {
        p.close();
    }
});

test('libraries and quality: save sends names, libraries, the limit and transcodes; a bad number is refused', async () => {
    const p = await friend('f-sam');
    try {
        const section = p.page.querySelector('#shoal-friend-libraries');
        const number = section.querySelector('input[type="number"]');
        setValue(number, 0);
        buttonIn(section, 'Save').click();
        await p.wait(60);
        assert.equal(calls(p, 'PUT', /Shares\/f-sam$/).length, 0);
        assert.match(section.textContent, /whole number/i);
        setValue(number, 3);
        const boxes = [...section.querySelectorAll('input[type="checkbox"]')];
        const kids = boxes.find((b) => b.closest('label').textContent.includes('Kids'));
        kids.click();
        buttonIn(section, 'Save').click();
        await p.wait(150);
        const put = calls(p, 'PUT', /Shares\/f-sam$/).at(-1).body;
        assert.equal(put.friendName, 'Sam');
        assert.deepEqual(put.libraryIds.slice().sort(), ['lib-kids', 'lib-movies']);
        assert.equal(put.maxConcurrentTranscodes, 3);
        assert.equal(put.maxBitrateMbps, 20);
    } finally {
        p.close();
    }
});

test('stop sharing: the button stays off until the name is typed, then the result lists every step', async () => {
    const p = await friend('f-sam');
    try {
        buttonIn(p.page, 'Stop sharing…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Stop sharing with Sam\?/);
        assert.match(d.textContent, /Shoal turns off Sam's sharing account\./);
        assert.match(d.textContent, /To share again later, create a new invite\./);
        const confirm = buttonIn(d, "Delete Sam's access");
        assert.equal(confirm.disabled, true);
        const typed = d.querySelector('input');
        setValue(typed, 'sam');
        assert.equal(confirm.disabled, true, 'case matters');
        setValue(typed, 'Sam');
        assert.equal(confirm.disabled, false);
        confirm.click();
        await p.wait(250);
        assert.deepEqual(calls(p, 'DELETE', /Shares\/f-sam$/).at(-1).body, { confirmName: 'Sam' });
        const text = p.text();
        assert.match(text, /Stopped sharing with Sam/);
        assert.match(text, /Sharing account disabled/);
        assert.match(text, /2 devices signed out/);
        assert.match(text, /No devices left/);
        assert.match(text, /Sharing account deleted/);
        assert.match(text, /Two more steps/);
        assert.match(text, /Tailscale admin console/);
        assert.match(text, /Protect your shared videos/);
        // Pilot 07 D19: no arrows, the mode is explained, and the two extra steps are numbered 1 and 2.
        assert.doesNotMatch(text, />|Log only/);
        assert.match(text, /open Machines, then this server, then Share/);
        assert.match(text, /Choose Require sign in in Settings/);
        const numbers = [...p.page.querySelectorAll('.shoal-result .shoal-checklist')].at(-1).querySelectorAll('.shoal-checklist__n');
        assert.deepEqual([...numbers].map((n) => n.textContent), ['1', '2']);
    } finally {
        p.close();
    }
});

test('stop sharing: a device that would not sign out is reported in words, not hidden', async () => {
    const p = await friend('f-sam', { revokeLeftover: true });
    try {
        buttonIn(p.page, 'Stop sharing…').click();
        await p.wait(60);
        const d = dialog(p);
        setValue(d.querySelector('input'), 'Sam');
        buttonIn(d, "Delete Sam's access").click();
        await p.wait(250);
        const text = p.text();
        assert.match(text, /1 device is still signed in\./);
        assert.match(text, /Shoal couldn't delete the account\./);
        assert.match(text, /Dashboard, then Users/);
    } finally {
        p.close();
    }
});

test('S7: while an approval waits, the invite section says the invite was used and offers only to cancel', async () => {
    const p = await friend('f-sam', { scenario: 'problems' });
    try {
        const section = [...p.page.querySelectorAll('section')].find((s) => s.querySelector('h2') && s.querySelector('h2').textContent === 'Invite');
        assert.match(section.textContent, /Sam.s Jellyfin used this invite\. Approve or deny the request above\./);
        assert.doesNotMatch(section.textContent, /expires in|Create a new invite|Create invite/);
        assert.ok(buttonIn(section, 'Cancel invite'));
    } finally {
        p.close();
    }
});
