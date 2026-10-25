// U-P1, U-P2, U-Q1, U-Q2 (PLAN v2.9; mockup docs/design/ui-v2/server.html): priority, quality limit and the speed-test card on Server detail,
// against the K53 routes (docs/kiro-v2.9-04.md) as the mock serves them.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, visibleText, JUNK } from './helpers.mjs';

async function server(id, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'server', id }, settleMs: 300, now: clock.now }, extra || {}));
}

// The mock's play order also lists a connection the page does not show (h-gone), so counts read "of 4".
const ALL = { playOrder: true, quality: true };
const prio = (p) => p.page.querySelector('.shoal-prio-panel');
const qual = (p) => p.page.querySelector('.shoal-quality');
const rec = (p) => p.page.querySelector('.shoal-rec');
const live = (p) => p.page.querySelector('[data-slot="live"]').textContent;
const calls = (p, method, path) => p.mock.calls.filter((c) => c.method === method && c.path === path);
const segButtons = (node) => [...node.querySelectorAll('.shoal-seg button')];
const pressed = (node) => segButtons(node).filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.textContent.trim());
const dialog = (p) => p.page.querySelector('dialog[open]');
const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const iconOf = (node) => node.querySelector('use').getAttribute('href');
const alertText = (p) => visibleText(p.page.querySelector('.shoal-errors') || p.page);

function type(input, value) {
    input.value = String(value);
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
    input.dispatchEvent(new window.Event('change', { bubbles: true }));
}

test('an older server without the routes: no priority, quality or suggestion blocks, and each route is asked once per visit', async () => {
    const p = await server('h-fern');
    try {
        assert.equal(prio(p).hidden, true);
        assert.equal(qual(p).hidden, true);
        assert.equal(rec(p).hidden, true);
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(calls(p, 'GET', 'Peers/h-fern/Quality').length, 1);
        assert.equal(calls(p, 'GET', 'PlayOrder').length, 1);
    } finally {
        p.close();
    }
});

test('layout as in the mockup: priority and quality on the left, the suggestion and Link speed on the right', async () => {
    const p = await server('h-fern', { playOrder: true, quality: 'rec' });
    try {
        const cols = [...p.page.querySelectorAll('.shoal-detail-grid > .shoal-detail-grid__col')];
        assert.equal(cols.length, 2);
        assert.deepEqual([...cols[0].children].map((c) => c.className), ['shoal-panel shoal-prio-panel', 'shoal-panel shoal-quality']);
        assert.equal(cols[1].children[0], rec(p));
        assert.ok(cols[1].children[1].classList.contains('shoal-speed'));
        assert.doesNotMatch(visibleText(p.page.querySelector('.shoal-detail-grid')), JUNK);
    } finally {
        p.close();
    }
});

test('U-P2: the indicator says Automatic with its place and reason; a tier set by the admin says Set by you with the pin', async () => {
    const p = await server('h-fern', ALL);
    try {
        const panel = prio(p);
        assert.equal(panel.hidden, false);
        const h2 = panel.querySelector('h2');
        assert.equal(h2.textContent, 'When titles are on several servers');
        assert.equal(panel.getAttribute('aria-labelledby'), h2.id);
        assert.match(visibleText(panel), /People see each title once\. This decides whether Fernwood plays it first\./);
        const ind = panel.querySelector('.shoal-indicator');
        assert.equal(ind.getAttribute('role'), 'status');
        assert.equal(visibleText(ind), 'Automatic: 2nd of 4 (slower link)');
        assert.equal(ind.querySelector('b').textContent, 'Automatic: 2nd of 4');
        assert.ok(iconOf(ind).endsWith('i-auto'));
        assert.match(visibleText(panel), /Normal lets Shoal pick by link speed and steadiness\. Always prefer puts Fernwood first\. Backup only uses Fernwood when no other server has the title online\./);
    } finally {
        p.close();
    }

    const g = await server('h-gran', ALL);
    try {
        const ind = prio(g).querySelector('.shoal-indicator');
        assert.equal(visibleText(ind), 'Set by you: 4th of 4 (backup only, and offline right now)');
        assert.ok(iconOf(ind).endsWith('i-pin'));
    } finally {
        g.close();
    }

    const one = await server('h-hops', { playOrder: 'one', quality: true });
    try {
        assert.equal(visibleText(prio(one).querySelector('.shoal-indicator')), 'Automatic (the only server for now)');
    } finally {
        one.close();
    }
});

test('U-P1 with the control turned off (flag): the choice is shown in words, no buttons, nothing sent', async () => {
    const p = await server('h-fern', Object.assign({ features: { priorityTier: false } }, ALL));
    try {
        const panel = prio(p);
        assert.equal(segButtons(panel).length, 0);
        assert.match(visibleText(panel), /Fernwood's turn: Normal\. This Shoal version can't change it from here yet\./);
        assert.equal(p.mock.calls.filter((c) => c.path.endsWith('/Priority')).length, 0);
    } finally {
        p.close();
    }
});

test('U-P1: three-way control (on by default), Normal pressed with a check; Backup only sends PUT Peers/{k}/Priority with the tier the admin saw', async () => {
    const p = await server('h-fern', ALL);
    try {
        const panel = prio(p);
        const seg = panel.querySelector('.shoal-seg');
        assert.equal(seg.getAttribute('role'), 'group');
        assert.equal(p.page.querySelector('#' + seg.getAttribute('aria-labelledby')).textContent, "Fernwood's turn");
        assert.deepEqual(segButtons(panel).map((b) => b.textContent.trim()), ['Always prefer', 'Normal', 'Backup only']);
        assert.deepEqual(pressed(panel), ['Normal']);
        assert.ok(segButtons(panel)[1].querySelector('use').getAttribute('href').endsWith('i-check'));
        assert.equal(segButtons(panel)[0].querySelector('svg'), null);
        segButtons(panel)[2].click();
        await p.wait(120);
        const put = calls(p, 'PUT', 'Peers/h-fern/Priority');
        assert.equal(put.length, 1);
        assert.deepEqual(put[0].body, { tier: 'Backup', expectedCurrent: 'Normal' });
        assert.deepEqual(pressed(prio(p)), ['Backup only']);
        assert.equal(visibleText(prio(p).querySelector('.shoal-indicator')), 'Set by you: 3rd of 4 (backup only)');
        assert.equal(live(p), "Fernwood's turn: Backup only.");
        assert.equal(document.activeElement, segButtons(prio(p))[2], 'focus stays on the choice');
        // Pressing the pressed button again sends nothing.
        segButtons(prio(p))[2].click();
        await p.wait(60);
        assert.equal(calls(p, 'PUT', 'Peers/h-fern/Priority').length, 1);
    } finally {
        p.close();
    }
});

test('U-P1: an older server without the Priority route keeps the old choice', async () => {
    const p = await server('h-fern', Object.assign({ priorityRoute: false }, ALL));
    try {
        segButtons(prio(p))[0].click();
        await p.wait(120);
        assert.deepEqual(pressed(prio(p)), ['Normal']);
        assert.doesNotMatch(live(p), /turn: Always prefer/);
    } finally {
        p.close();
    }
});

test('U-Q1: limit control Off/480p/720p/1080p/4K, speed cap field labelled, no cost note while off', async () => {
    const p = await server('h-fern', ALL);
    try {
        const panel = qual(p);
        assert.equal(panel.hidden, false);
        assert.equal(panel.querySelector('h2').textContent, 'Quality limit');
        assert.match(visibleText(panel), /The highest quality Fernwood sends to your server\./);
        assert.deepEqual(segButtons(panel).map((b) => b.textContent.trim()), ['No limit', '480p', '720p', '1080p', '4K']);
        assert.deepEqual(pressed(panel), ['No limit']);
        assert.equal(p.page.querySelector('#' + panel.querySelector('.shoal-seg').getAttribute('aria-labelledby')).textContent, 'Limit');
        const input = panel.querySelector('input');
        assert.equal(panel.querySelector('label[for="' + input.id + '"]').textContent, 'Speed cap (optional)');
        assert.equal(input.getAttribute('inputmode'), 'numeric');
        assert.equal(input.getAttribute('placeholder'), 'No cap');
        assert.equal(input.value, '');
        const help = p.page.querySelector('#' + input.getAttribute('aria-describedby').split(' ')[0]);
        assert.equal(help.textContent, 'Leave empty to use only the quality limit.');
        assert.equal(panel.querySelector('.shoal-cost'), null);
    } finally {
        p.close();
    }
});

test('U-Q1: choosing 1080p sends the class with the limit the admin saw, then shows the cost note', async () => {
    const p = await server('h-fern', ALL);
    try {
        buttonIn(qual(p), '1080p').click();
        await p.wait(120);
        const put = calls(p, 'PUT', 'Peers/h-fern/QualityLimit');
        assert.equal(put.length, 1);
        assert.deepEqual(put[0].body, { maxClass: '1080', maxMbps: null, fromRecommendation: false, expectedCurrent: { maxClass: 'Off', maxMbps: null } });
        assert.deepEqual(pressed(qual(p)), ['1080p']);
        const cost = qual(p).querySelector('.shoal-cost');
        assert.equal(cost.getAttribute('role'), 'note');
        assert.equal(visibleText(cost), "Titles above this quality will be converted by Fernwood. That uses their computer's power.");
        assert.equal(live(p), 'Quality limit for Fernwood: up to 1080p.');
        assert.equal(document.activeElement.textContent.trim(), '1080p');
        buttonIn(qual(p), 'No limit').click();
        await p.wait(120);
        assert.deepEqual(calls(p, 'PUT', 'Peers/h-fern/QualityLimit')[1].body.expectedCurrent, { maxClass: '1080', maxMbps: null });
        assert.equal(live(p), 'Quality limit for Fernwood is off.');
        assert.equal(qual(p).querySelector('.shoal-cost'), null);
    } finally {
        p.close();
    }
});

test('U-Q1: the speed cap takes a whole number 1 to 1000 or nothing; a bad value is refused in words and never sent', async () => {
    const p = await server('h-fern', ALL);
    try {
        for (const bad of ['abc', '0', '1001', '2.5', '-3']) {
            const input = qual(p).querySelector('input');
            type(input, bad);
            await p.wait(30);
            assert.equal(input.getAttribute('aria-invalid'), 'true', bad);
            const err = p.page.querySelector('#' + input.getAttribute('aria-describedby').split(' ')[1]);
            assert.equal(err.hidden, false);
            assert.equal(err.textContent.trim(), 'Enter a whole number from 1 to 1000, or leave it empty.');
        }

        assert.equal(calls(p, 'PUT', 'Peers/h-fern/QualityLimit').length, 0);
        type(qual(p).querySelector('input'), ' 20 ');
        await p.wait(120);
        assert.deepEqual(calls(p, 'PUT', 'Peers/h-fern/QualityLimit')[0].body, { maxClass: 'Off', maxMbps: 20, fromRecommendation: false, expectedCurrent: { maxClass: 'Off', maxMbps: null } });
        assert.equal(qual(p).querySelector('input').value, '20');
        assert.ok(qual(p).querySelector('.shoal-cost'), 'a speed cap alone also limits');
        assert.equal(live(p), 'Quality limit for Fernwood: up to 20 Mbps.');
        assert.equal(document.activeElement, qual(p).querySelector('input'));
        type(qual(p).querySelector('input'), '');
        await p.wait(120);
        assert.equal(calls(p, 'PUT', 'Peers/h-fern/QualityLimit')[1].body.maxMbps, null);
    } finally {
        p.close();
    }
});

test('U-Q1: a limit changed elsewhere (409 changed) is said in words and the current one is shown', async () => {
    const p = await server('h-fern', Object.assign({ qualityChanged: true }, ALL));
    try {
        buttonIn(qual(p), '4K').click();
        await p.wait(150);
        assert.match(alertText(p), /This limit was changed somewhere else a moment ago\. The page now shows the current one\./);
        assert.deepEqual(pressed(qual(p)), ['720p']);
    } finally {
        p.close();
    }
});

test('U-Q2: the speed-test card: words, Apply asks first and sets the suggested class from the recommendation', async () => {
    const p = await server('h-fern', { playOrder: true, quality: 'rec' });
    try {
        const card = rec(p);
        assert.equal(card.hidden, false);
        assert.equal(card.tagName, 'ASIDE');
        assert.equal(p.page.querySelector('#' + card.getAttribute('aria-labelledby')).textContent, 'This link is slow for 4K. Limit Fernwood to 1080p?');
        assert.equal(visibleText(card), 'Suggestion from the speed test This link is slow for 4K. Limit Fernwood to 1080p? Measured 17 Mbps. 120 of 386 titles are above 1080p.ApplyDismiss');
        assert.ok(buttonIn(card, 'Apply').classList.contains('shoal-btn--primary'));
        buttonIn(card, 'Apply').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(visibleText(d), /Limit Fernwood to 1080p\?/);
        assert.match(visibleText(d), /Titles above 1080p will be converted by Fernwood\. That uses their computer's power\. You can change this any time under Quality limit\./);
        assert.equal(calls(p, 'PUT', 'Peers/h-fern/QualityLimit').length, 0, 'nothing before the confirm');
        buttonIn(d, 'Limit to 1080p').click();
        await p.wait(200);
        assert.deepEqual(calls(p, 'PUT', 'Peers/h-fern/QualityLimit')[0].body, { maxClass: '1080', maxMbps: null, fromRecommendation: true, expectedCurrent: { maxClass: 'Off', maxMbps: null } });
        assert.equal(rec(p).hidden, true, 'the card goes once the limit holds');
        assert.deepEqual(pressed(qual(p)), ['1080p']);
        assert.match(visibleText(qual(p)), /Set from the speed test suggestion just now|Set from the speed test suggestion/);
        assert.equal(document.activeElement, qual(p).querySelector('h2'));
    } finally {
        p.close();
    }
});

test('U-Q2: Apply cancelled sends nothing; Dismiss sends the shown class, hides the card and moves focus', async () => {
    const p = await server('h-fern', { playOrder: true, quality: 'rec' });
    try {
        buttonIn(rec(p), 'Apply').click();
        await p.wait(60);
        buttonIn(dialog(p), 'Cancel').click();
        await p.wait(100);
        assert.equal(calls(p, 'PUT', 'Peers/h-fern/QualityLimit').length, 0);
        assert.equal(rec(p).hidden, false);
        buttonIn(rec(p), 'Dismiss').click();
        await p.wait(150);
        const post = calls(p, 'POST', 'Peers/h-fern/Recommendations/Dismiss');
        assert.equal(post.length, 1);
        assert.deepEqual(post[0].body, { suggestedClass: '1080' });
        assert.equal(rec(p).hidden, true);
        assert.equal(live(p), 'Suggestion dismissed. It comes back only if the link gets clearly slower.');
        assert.equal(document.activeElement, qual(p).querySelector('h2'));
    } finally {
        p.close();
    }
});

test('U-Q2: a stale card (a new speed test) is said in words and the card is read again', async () => {
    const p = await server('h-fern', { playOrder: true, quality: 'rec', recStale: true });
    try {
        buttonIn(rec(p), 'Dismiss').click();
        await p.wait(150);
        assert.match(alertText(p), /The suggestion changed after a new speed test\. The page now shows the current one\./);
        assert.equal(rec(p).hidden, false);
        assert.ok(calls(p, 'GET', 'Peers/h-fern/Quality').length >= 2);
    } finally {
        p.close();
    }
});

test('a limit set from a suggestion says so; other servers get no card', async () => {
    const p = await server('h-fern', { playOrder: true, quality: 'limited' });
    try {
        assert.deepEqual(pressed(qual(p)), ['1080p']);
        assert.match(visibleText(qual(p)), /Set from the speed test suggestion 3 hours ago\./);
        assert.equal(rec(p).hidden, true);
    } finally {
        p.close();
    }

    const h = await server('h-hops', { playOrder: true, quality: 'rec' });
    try {
        assert.equal(rec(h).hidden, true);
        assert.equal(qual(h).hidden, false);
    } finally {
        h.close();
    }
});

test('a pairing server shows none of the blocks', async () => {
    const p = await server('h-new', { playOrder: true, quality: 'rec' });
    try {
        for (const node of [prio(p), qual(p), rec(p)]) {
            assert.ok(!node || node.hidden);
        }
    } finally {
        p.close();
    }
});

test('a poll never redraws a block the admin is typing in', async () => {
    const p = await server('h-fern', ALL);
    try {
        const input = qual(p).querySelector('input');
        input.focus();
        input.value = '4';
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(qual(p).querySelector('input'), input);
        assert.equal(input.value, '4');
    } finally {
        p.close();
    }
});

test('pure helpers: readMbps and limitWords', async () => {
    const mod = await import('./helpers.mjs').then((h) => h.loadShoal());
    assert.equal(mod.readMbps(''), null);
    assert.equal(mod.readMbps('  '), null);
    assert.equal(mod.readMbps('1'), 1);
    assert.equal(mod.readMbps('1000'), 1000);
    for (const bad of ['0', '1001', 'x', '1e3', '2.5', '-1', '10000']) {
        assert.equal(mod.readMbps(bad), undefined, bad);
    }

    assert.equal(mod.limitWords({ maxClass: 'Off', maxMbps: null }), null);
    assert.equal(mod.limitWords({ maxClass: '2160', maxMbps: null }), 'up to 4K');
    assert.equal(mod.limitWords({ maxClass: '720', maxMbps: 8 }), 'up to 720p and 8 Mbps');
    assert.equal(mod.limitWords({ maxClass: 'bogus', maxMbps: 5 }), 'up to 5 Mbps');
});
