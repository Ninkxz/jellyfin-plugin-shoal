import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock } from './helpers.mjs';

const button = (root, label) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === label);
const libraries = (extra) => mountPage({ params: { view: 'libraries' }, settleMs: 200, ...extra });

test('latest speed names the measurement time, not the page fetch time; missing samples have no invented timestamp', async () => {
    const clock = makeClock(Date.UTC(2026, 9, 22, 12));
    const p = await mountPage({ params: { view: 'server', id: 'h-fern' }, now: clock.now, settleMs: 250 });
    try {
        const kpi = p.page.querySelector('.shoal-speed .shoal-kpi');
        assert.equal(kpi.querySelector('dt').textContent, 'Last measured');
        assert.equal(kpi.querySelector('time').dateTime, '2026-10-22T11:42:00.000Z');
        assert.match(kpi.querySelector('time').textContent, /18 min ago/);
        assert.ok(kpi.querySelector('time').title.length > 10);
        clock.advance(5 * 60000);
        await p.handle.screen().refresh();
        await p.wait(60);
        assert.equal(p.page.querySelector('.shoal-speed .shoal-kpi time').dateTime, '2026-10-22T11:42:00.000Z');
    } finally { p.close(); }

    const empty = await mountPage({ scenario: 'nothingShared', params: { view: 'server', id: 'h-fern' }, settleMs: 250 });
    try {
        assert.match(empty.page.querySelector('.shoal-speed .shoal-kpi').textContent, /Measurement time unknown/);
        assert.equal(empty.page.querySelector('.shoal-speed .shoal-kpi time'), null);
    } finally { empty.close(); }
});

test('a failed Shares read never claims there are no shares; retry performs reads only', async () => {
    const p = await libraries({ failPaths: [/^GET Shares$/] });
    try {
        assert.match(p.text(), /Couldn't load You share/);
        assert.ok(!p.text().includes(p.mod.STRINGS.noSharing));
        assert.equal(p.page.querySelectorAll('.shoal-row--share').length, 0);
        p.mock.set({ failPaths: [] });
        const before = p.mock.calls.length;
        button(p.page.querySelector('[data-shoal-view="libraries"] .shoal-loaderror'), 'Try again').click();
        await p.wait(150);
        assert.ok(p.page.querySelector('.shoal-row--share'));
        assert.equal(p.page.querySelector('[data-shoal-view="libraries"] .shoal-loaderror'), null);
        assert.ok(p.mock.calls.slice(before).every((c) => c.method === 'GET'));
    } finally { p.close(); }
});

test('failed refresh keeps known shares and warns; a Layouts outage cannot masquerade as a legacy empty list', async () => {
    const p = await libraries();
    try {
        const names = [...p.page.querySelectorAll('.shoal-row--share .shoal-row__title')].map((e) => e.textContent);
        p.mock.set({ failPaths: [/^GET Shares$/, /^GET Layouts\/Libraries$/] });
        p.handle.screen().show();
        await p.wait(150);
        assert.deepEqual([...p.page.querySelectorAll('.shoal-row--share .shoal-row__title')].map((e) => e.textContent), names);
        assert.match(p.text(), /Previously loaded information may be out of date/);
        assert.equal(p.page.querySelectorAll('[data-shoal-view="libraries"] .shoal-loaderror').length, 2);
    } finally { p.close(); }

    const outage = await libraries({ failPaths: [/^GET Layouts\/Libraries$/] });
    try {
        assert.match(outage.text(), /Couldn't load Library layouts/);
        assert.ok(!outage.text().includes(outage.mod.STRINGS.noLibrariesYet));
        assert.equal(outage.page.querySelectorAll('.shoal-group').length, 0);
    } finally { outage.close(); }
});

test('series grouping warning uses distinct server library names as text and changes no settings', async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const p = await libraries({ notMergingSeries: ['Shows', 'Shows', hostile, null] });
    try {
        const warnings = p.page.querySelectorAll('.shoal-warnbox--seriesgrouping');
        assert.equal(warnings.length, 2);
        assert.match(warnings[0].textContent, /Series grouping is off in Shows.*extra episodes from friends can stay hidden/);
        assert.ok(warnings[1].textContent.includes(hostile));
        assert.equal(warnings[1].querySelector('img'), null);
        assert.ok(p.mock.calls.every((c) => c.method === 'GET'));
    } finally { p.close(); }
});

test('unexpected errors offer technical disclosure and a safe read retry without exposing exception contents', async () => {
    const p = await libraries();
    try {
        window.dispatchEvent(new window.ErrorEvent('error', { error: new TypeError('private-token-in-url') }));
        const bar = p.page.querySelector('.shoal-errorbar');
        assert.match(bar.textContent, /check its current setting before trying again/);
        assert.equal(bar.querySelector('details').open, false);
        assert.equal(bar.querySelector('details p').textContent, 'TypeError');
        assert.ok(!bar.textContent.includes('private-token-in-url'));
        const before = p.mock.calls.length;
        button(bar, 'Reload page data').click();
        await p.wait(150);
        assert.ok(p.mock.calls.slice(before).length > 0);
        assert.ok(p.mock.calls.slice(before).every((c) => c.method === 'GET'));
        assert.equal(p.page.querySelector('.shoal-errorbar'), null);
    } finally { p.close(); }
});

test('library access and holds precede priority, quality and speed controls', async () => {
    const p = await mountPage({ params: { view: 'server', id: 'h-fern' }, scenario: 'problems', quality: true, playOrder: true, settleMs: 250 });
    try {
        const diagnostics = p.page.querySelector('.shoal-detail-grid');
        const headings = [...p.page.querySelectorAll('[data-shoal-view="server"] h2')];
        const librariesHeading = headings.find((h) => /Libraries/.test(h.textContent));
        assert.ok(librariesHeading);
        assert.ok(librariesHeading.compareDocumentPosition(diagnostics) & 4);
    } finally { p.close(); }
});

test('Keep My Place defaults on, explains its local scope, and persists changes only when Settings is saved', async () => {
    const p = await mountPage({ params: { view: 'settings' }, settleMs: 250 });
    const toggle = () => {
        const label = [...p.page.querySelectorAll('.shoal-check__label')].find((e) => e.textContent.trim() === 'Keep My Place');
        return label && label.closest('label').querySelector('input');
    };
    try {
        assert.ok(toggle());
        assert.equal(toggle().checked, true);
        assert.match(p.text(), /same user on this server\. Existing local progress is kept\. No watch history is sent to friends/);
        toggle().click();
        await p.wait(30);
        assert.ok(!p.mock.calls.some((c) => c.method === 'PUT' && c.path === 'Settings'));
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').at(-1).body.keepMyPlaceEnabled, false);
        assert.equal(toggle().checked, false);
        toggle().click();
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').at(-1).body.keepMyPlaceEnabled, true);
    } finally { p.close(); }
});

test('leaving Keep My Place unsaved and returning preserves an explicit saved off setting without a write', async () => {
    const p = await mountPage({ params: { view: 'settings' }, keepMyPlaceEnabled: false, settleMs: 250 });
    const toggle = () => [...p.page.querySelectorAll('.shoal-check__label')].find((e) => e.textContent.trim() === 'Keep My Place').closest('label').querySelector('input');
    try {
        assert.equal(toggle().checked, false);
        toggle().click();
        for (const view of ['overview', 'settings']) {
            window.location.hash = '#/configurationpage?name=Shoal&view=' + view;
            window.dispatchEvent(new window.HashChangeEvent('hashchange'));
            await p.wait(250);
        }
        assert.equal(toggle().checked, false);
        assert.ok(!p.mock.calls.some((c) => c.method === 'PUT' && c.path === 'Settings'));
    } finally {
        p.close();
        window.location.hash = '';
    }
});

test('Pause caveat only claims linked sharing remains available when that share is active; Resume explains current pause', async () => {
    for (const [state, expected] of [['Active', true], ['Paused', false]]) {
        const p = await mountPage({ params: { view: 'server', id: 'h-hops' }, twoWayShare: state, settleMs: 250 });
        try {
            const pause = button(p.page.querySelector('.shoal-detail__header'), 'Pause');
            assert.ok(pause);
            assert.equal(pause.parentElement.textContent.includes('Hops will still watch your libraries.'), expected);
            button(p.page.querySelector('.shoal-detail__header'), 'Remove server…').click();
            await p.wait(60);
            assert.equal(p.page.querySelector('dialog[open]').textContent.includes('Hops will still watch your libraries.'), expected);
        } finally { p.close(); }
    }
    const paused = await mountPage({ scenario: 'problems', params: { view: 'server', id: 'h-maple' }, settleMs: 250 });
    try {
        const resume = button(paused.page.querySelector('.shoal-detail__header'), 'Resume');
        assert.ok(resume);
        assert.match(resume.parentElement.textContent, /This server is paused\. Titles stay listed here but won't play until you resume/);
        const help = paused.page.ownerDocument.getElementById(resume.getAttribute('aria-describedby'));
        assert.ok(help, 'Resume names its visible explanation for assistive technology');
        assert.match(help.textContent, /This server is paused/);
    } finally { paused.close(); }
});
