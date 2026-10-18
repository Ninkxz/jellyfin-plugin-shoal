// Pilot 14 (docs/tests/pilot-14-v2.9.md) UI fixes: 7b the duplicate-title note, the bottleneck said once, 8 hash navigation never shows stale data.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, visibleText } from './helpers.mjs';

const count = (text, needle) => text.split(needle).length - 1;

test('7b: the note for titles another server also has speaks of one title with a backup, not of removing a server', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'server', id: 'h-fern' }, settleMs: 350, dedup: true });
    try {
        const text = p.text();
        assert.match(text, /41 titles are also on Hops\. People see each once: they play from the best server, and the other stays ready as a backup\./);
        assert.doesNotMatch(text, /remove it to use this server's copy|it wins when both/);
    } finally {
        p.close();
    }

    const o = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 350, dedup: true });
    try {
        assert.doesNotMatch(o.text(), /remove it to use this server's copy/);
    } finally {
        o.close();
    }
});

test('the speed bottleneck is said once on Server detail, even when a hint repeats it', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'server', id: 'h-hops' }, settleMs: 400, speedHints: [{ kind: 'peerUpload', severity: 'warn' }, { kind: 'relayed', severity: 'warn' }] });
    try {
        const text = p.text();
        assert.equal(count(text, "Limited by Hops's upload, not your connection."), 1);
        assert.equal(count(text, "Hops's upload is the likely bottleneck."), 0, "the hint saying the same is dropped");
        assert.match(text, /Relayed through Tailscale: expect lower speed/, 'other hints stay');
    } finally {
        p.close();
    }
});

function setHash(query) {
    window.location.hash = '#/configurationpage?name=Shoal&' + query;
}

test('8: a page Jellyfin restores is built again from fresh data for the view in the address', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'layouts' }, settleMs: 350 });
    try {
        const reads = () => p.mock.calls.filter((c) => c.method === 'GET' && c.path === 'Layouts/Libraries').length;
        const before = reads();
        const firstScreen = p.handle.screen();
        page(p).dispatchEvent(new window.CustomEvent('viewhide', { bubbles: true }));
        setHash('view=layouts');
        page(p).dispatchEvent(new window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: true } }));
        await p.wait(250);
        assert.ok(reads() > before, 'read again');
        assert.notEqual(p.handle.screen(), firstScreen, 'a new screen, not the old one with old data');
        assert.equal(p.page.querySelector('.shoal-root').dataset.view, 'layouts');
    } finally {
        p.close();
        window.location.hash = '';
    }
});

test('8: a hash change inside the same page switches to that view and reads its data', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 350 });
    try {
        setHash('view=libraries');
        window.dispatchEvent(new window.HashChangeEvent('hashchange'));
        await p.wait(250);
        assert.equal(p.page.querySelector('.shoal-root').dataset.view, 'libraries');
        assert.ok(p.mock.calls.some((c) => c.method === 'GET' && c.path === 'Libraries'));
        assert.match(visibleText(p.page.querySelector('[data-shoal-view="libraries"]')), /Shoal Movies/);
        // The same address again does nothing.
        const n = p.mock.calls.length;
        window.dispatchEvent(new window.HashChangeEvent('hashchange'));
        await p.wait(100);
        assert.equal(p.mock.calls.filter((c, i) => i >= n && c.path === 'Libraries').length, 0);
    } finally {
        p.close();
        window.location.hash = '';
    }
});

test('8: a hidden page and another plugin\'s address are left alone; a restored pairing wizard keeps its place', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 350 });
    try {
        page(p).classList.add('hide');
        setHash('view=settings');
        window.dispatchEvent(new window.HashChangeEvent('hashchange'));
        await p.wait(100);
        assert.equal(p.page.querySelector('.shoal-root').dataset.view, 'overview');
        page(p).classList.remove('hide');
        window.location.hash = '#/configurationpage?name=Other&view=settings';
        window.dispatchEvent(new window.HashChangeEvent('hashchange'));
        await p.wait(100);
        assert.equal(p.page.querySelector('.shoal-root').dataset.view, 'overview');
    } finally {
        p.close();
        window.location.hash = '';
    }

    const w = await mountPage({ scenario: 'default', params: { view: 'pair', dir: 'watch' }, settleMs: 350 });
    try {
        const wizard = w.handle.screen();
        setHash('view=pair&dir=watch');
        page(w).dispatchEvent(new window.CustomEvent('viewshow', { bubbles: true, detail: { isRestored: true } }));
        await w.wait(150);
        assert.equal(w.handle.screen(), wizard);
    } finally {
        w.close();
        window.location.hash = '';
    }
});

function page(p) {
    return p.page;
}
