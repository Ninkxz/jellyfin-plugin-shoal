// Pilot 14 UI-LAYOUT-STALE (7a): on a v2.9 server Libraries & access lists the libraries that really exist (GET Layouts/Libraries), not the old
// per-friend mappings a layout already removed. A server without layouts keeps the per-friend list (libraries.test.mjs).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, visibleText } from './helpers.mjs';

async function libraries(extra) {
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'libraries' }, settleMs: 350 }, extra || {}));
}

const blocks = (p) => [...p.page.querySelectorAll('.shoal-libblock')];
const block = (p, name) => blocks(p).find((b) => b.querySelector('.shoal-row__title').textContent === name);
const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const live = (p) => p.page.querySelector('[data-slot="live"]').textContent;

test('the list is the layout libraries, in their order, with type, friends and who can see each', async () => {
    const p = await libraries();
    try {
        assert.deepEqual(blocks(p).map((b) => b.querySelector('.shoal-row__title').textContent), ['Movies', 'Hops Movies', 'Shoal Movies', 'Hops TV Shows', 'Shoal TV Shows']);
        assert.equal(block(p, 'Granite Movies'), undefined, 'an old per-friend mapping name is not listed');
        assert.match(visibleText(block(p, 'Shoal TV Shows')), /TV Shows from Fernwood and Granite/);
        assert.match(visibleText(block(p, 'Movies')), /Added into one of your own libraries\./);
        assert.match(visibleText(block(p, 'Shoal Movies')), /Sam/);
        assert.equal(block(p, 'Shoal Movies').querySelector('.shoal-seg'), null, 'no per-mapping switch');
        assert.equal(buttonIn(block(p, 'Hops Movies'), 'Change who can see this').getAttribute('aria-label'), 'Change who can see Hops Movies');
        assert.equal(p.mock.calls.filter((c) => c.method === 'GET' && c.path === 'Layouts/Libraries').length, 1);
    } finally {
        p.close();
    }
});

test('changing who can see a library saves through PUT Layouts/Libraries/{libKey}, names who lost access, and reloads', async () => {
    const p = await libraries();
    try {
        buttonIn(block(p, 'Shoal Movies'), 'Change who can see this').click();
        await p.wait(30);
        const dlg = dialog(p);
        assert.equal(dlg.querySelector('h2').textContent, 'Who can see Shoal Movies?');
        const sam = [...dlg.querySelectorAll('.shoal-picker__row')].find((r) => r.querySelector('.shoal-check__label').textContent === 'Sam');
        sam.querySelector('input').click();
        buttonIn(dlg, 'Save').click();
        await p.wait(120);
        const put = p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Layouts/Libraries/lk-shoalmov').at(-1);
        assert.deepEqual(put.body, { access: { mode: 'Selected', selectedUserIds: ['u-rich'], includeRestricted: false, ratingsAcknowledged: false } });
        assert.equal(live(p), 'Sam can no longer see Shoal Movies.');
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && /\/Access$/.test(c.path)).length, 0, 'the old per-mapping route is not used');
        assert.doesNotMatch(visibleText(block(p, 'Shoal Movies')), /Sam/);
        assert.equal(document.activeElement, buttonIn(block(p, 'Shoal Movies'), 'Change who can see this'));
    } finally {
        p.close();
    }
});

test('an older server without layouts keeps the per-friend list', async () => {
    const p = await libraries({ layoutsRoute: false });
    try {
        assert.ok(block(p, 'Granite Movies'));
        assert.ok(block(p, 'Hops Movies').querySelector('.shoal-seg'));
    } finally {
        p.close();
    }
});
