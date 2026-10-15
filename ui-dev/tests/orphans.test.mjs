// Leftover sharing accounts (Warden 08 M7): the Overview lists them, the admin deletes one by typing its name, Shoal never does it by itself.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

async function overview(extra) {
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'overview' }, settleMs: 300 }, extra || {}));
}

test('with no leftover accounts nothing is shown', async () => {
    const p = await overview();
    try {
        assert.doesNotMatch(p.text(), /Leftover sharing accounts/);
        assert.equal(p.page.querySelectorAll('.shoal-orphans__row').length, 0);
    } finally {
        p.close();
    }
});

test('leftover accounts are listed with their state and a Delete button each; nothing is deleted by itself', async () => {
    const p = await overview({ orphans: true });
    try {
        const t = p.text();
        assert.match(t, /Leftover sharing accounts/);
        assert.match(t, /Shoal never deletes them by itself/);
        const rows = [...p.page.querySelectorAll('.shoal-orphans__row')];
        assert.equal(rows.length, 2);
        assert.match(rows[0].textContent, /shoal-riley-3f2a/);
        assert.match(rows[0].textContent, /Can sign in/);
        assert.match(rows[1].textContent, /shoal-old-91bc/);
        assert.match(rows[1].textContent, /Disabled/);
        assert.equal(calls(p, 'DELETE', /OrphanAccounts/).length, 0);
        assert.equal(rows[0].querySelector('button').getAttribute('aria-label'), 'Delete shoal-riley-3f2a');
    } finally {
        p.close();
    }
});

test('delete: the button stays off until the exact name is typed, then the account goes and the list refreshes', async () => {
    const p = await overview({ orphans: true });
    try {
        const first = p.page.querySelector('.shoal-orphans__row button');
        first.click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Delete the account shoal-riley-3f2a\?/);
        const confirm = buttonIn(d, 'Delete account');
        assert.equal(confirm.disabled, true);
        const typed = d.querySelector('input');
        setValue(typed, 'shoal-riley');
        assert.equal(confirm.disabled, true);
        setValue(typed, 'SHOAL-RILEY-3F2A');
        assert.equal(confirm.disabled, true, 'case matters');
        setValue(typed, 'shoal-riley-3f2a');
        assert.equal(confirm.disabled, false);
        confirm.click();
        await p.wait(300);
        const sent = calls(p, 'DELETE', /OrphanAccounts\//);
        assert.equal(sent.length, 1);
        assert.match(sent[0].path, /OrphanAccounts\/9a1e0000-0000-4000-8000-000000000001$/);
        const rows = [...p.page.querySelectorAll('.shoal-orphans__row')];
        assert.equal(rows.length, 1);
        assert.match(rows[0].textContent, /shoal-old-91bc/);
    } finally {
        p.close();
    }
});

test('delete: cancelling sends nothing', async () => {
    const p = await overview({ orphans: true });
    try {
        p.page.querySelector('.shoal-orphans__row button').click();
        await p.wait(60);
        buttonIn(dialog(p), 'Cancel').click();
        await p.wait(60);
        assert.equal(calls(p, 'DELETE', /OrphanAccounts/).length, 0);
        assert.equal(p.page.querySelectorAll('.shoal-orphans__row').length, 2);
    } finally {
        p.close();
    }
});

test('an account name is only ever text (a hostile name does not become markup)', async () => {
    const p = await overview({ orphans: true, scenario: 'hostile' });
    try {
        const row = p.page.querySelector('.shoal-orphans__row');
        assert.ok(row.textContent.includes('<'), 'the name is shown as typed');
        assert.equal(row.querySelectorAll('img, script, svg[onload]').length, 0);
    } finally {
        p.close();
    }
});
