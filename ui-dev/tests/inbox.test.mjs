// U-M3 (PLAN v2.9): the inbox card on Settings against K49, GET/POST Shoal/Admin/Inbox/Explainer: the card answers
// { installed, posted, postedUtc, recipients, missingRecipients, title, body, buttonNote, fallbackNote, error }, POST answers { sent, explainer }.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, visibleText, middayNow } from './helpers.mjs';

async function settings(extra) {
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, now: middayNow() }, extra || {})); // relative times stay "hours ago" after midnight
}

const card = (p) => p.page.querySelector('.shoal-inbox');
const post = (p) => card(p).querySelector('[data-fkey="inbox:post"]');
const status = (p) => card(p).querySelector('.shoal-inbox__status').textContent;
const live = (p) => p.page.querySelector('[data-slot="live"]').textContent;
const calls = (p, method) => p.mock.calls.filter((c) => c.method === method && /^Inbox\/Explainer$/.test(c.path));

test('the card is on by default; the flag turned off means no card and no call', async () => {
    const p = await settings({ inbox: 'ok', features: { inboxExplainer: false } });
    try {
        assert.equal(card(p), null);
        assert.equal(calls(p, 'GET').length, 0);
    } finally {
        p.close();
    }

    const q = await settings({ inbox: 'ok' });
    try {
        assert.equal(card(q).hidden, false);
    } finally {
        q.close();
    }
});

test('an older backend without the route keeps the card hidden', async () => {
    const p = await settings();
    try {
        assert.equal(calls(p, 'GET').length, 1);
        assert.equal(card(p).hidden, true);
    } finally {
        p.close();
    }
});

test('the card shows the message as plain text (no markdown marks, title once) and the Moonfin button note', async () => {
    const p = await settings({ inbox: 'ok' });
    try {
        const c = card(p);
        assert.equal(c.querySelector('h2').textContent, 'Message to your users');
        assert.equal(c.querySelector('.shoal-inbox__msgtitle').textContent, 'Why might a title not work?');
        const body = c.querySelector('.shoal-inbox__msgbody').textContent;
        assert.ok(body.startsWith("Some titles come from a friend's server."));
        assert.doesNotMatch(c.textContent, /\*\*/);
        assert.equal(visibleText(c).match(/Why might a title not work\?/g).length, 1);
        assert.equal(c.querySelector('.shoal-inbox__note').textContent, "Moonfin shows the inbox button only when it is turned on in Moonfin's settings.");
    } finally {
        p.close();
    }
});

test('Post sends once; the answer card disables the button; a second click sends nothing', async () => {
    const p = await settings({ inbox: 'ok' });
    try {
        assert.equal(post(p).textContent.trim(), "Post to users' inbox");
        assert.equal(post(p).disabled, false);
        post(p).click();
        await p.wait(80);
        assert.equal(calls(p, 'POST').length, 1);
        assert.equal(post(p).disabled, true);
        assert.equal(status(p), 'Posted just now. Each person gets it once.');
        assert.equal(live(p), "Posted to your users' inbox.");
        post(p).click();
        await p.wait(50);
        assert.equal(calls(p, 'POST').length, 1, 'a second click sends nothing');
    } finally {
        p.close();
    }
});

test('posted before and in everyone\'s inbox: when, and the button disabled', async () => {
    const p = await settings({ inbox: 'posted' });
    try {
        assert.equal(post(p).disabled, true);
        assert.equal(status(p), 'Posted 2 hours ago. Each person gets it once.');
    } finally {
        p.close();
    }
});

test('posted before but newer people lack it: Post again for new people reaches them', async () => {
    const p = await settings({ inbox: 'missing' });
    try {
        assert.equal(post(p).disabled, false);
        assert.equal(post(p).textContent.trim(), 'Post again for new people');
        assert.equal(status(p), "Posted 2 hours ago. 2 people added since then don't have it yet.");
        post(p).click();
        await p.wait(80);
        assert.equal(post(p).disabled, true);
        assert.equal(post(p).textContent.trim(), "Post to users' inbox", 'the label after the busy text is the new one');
        assert.equal(status(p), 'Posted just now. Each person gets it once.');
    } finally {
        p.close();
    }
});

test('without Moonbase there is no button, only the server\'s fallback note', async () => {
    const p = await settings({ inbox: 'noMoonfin' });
    try {
        assert.equal(post(p).hidden, true);
        assert.equal(status(p), "Moonbase isn't installed. Each friend's title still says where it comes from.");
        assert.equal(card(p).querySelector('.shoal-inbox__note').hidden, true);
    } finally {
        p.close();
    }
});

test('Moonfin refusing is said in words; a post that sent nothing says so', async () => {
    const p = await settings({ inbox: 'refused' });
    try {
        assert.equal(status(p), 'Moonfin refused the message. Check that you are signed in as an administrator, then try again.');
        assert.equal(post(p).disabled, false);
        post(p).click();
        await p.wait(80);
        assert.match(p.text(), /Moonfin refused the message\./);
    } finally {
        p.close();
    }
});

test('a failed post says so and leaves the button usable', async () => {
    const p = await settings({ inbox: 'ok', failPaths: [/^POST Inbox/] });
    try {
        post(p).click();
        await p.wait(80);
        assert.equal(post(p).disabled, false);
        assert.match(p.text(), /The message didn't post\. Try again in a moment\./);
    } finally {
        p.close();
    }
});
