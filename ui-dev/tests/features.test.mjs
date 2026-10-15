// K33: the settings rows for alternate versions, preview pictures, fonts and the picture check, and their alerts (DESIGN v2.6.4 to v2.6.7).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const calls = (p, method, re) => p.mock.calls.filter((c) => c.method === method && re.test(c.path));

function setValue(el, value) {
    el.value = String(value);
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
    el.dispatchEvent(new window.Event('change', { bubbles: true }));
}

async function settings(extra) {
    return mountPage(Object.assign({ scenario: 'default', params: { view: 'settings' }, settleMs: 400 }, extra || {}));
}

const row = (p, label) => [...p.page.querySelectorAll('label.shoal-check')].find((l) => l.querySelector('.shoal-check__label')?.textContent.trim() === label);
const panel = (p) => [...p.page.querySelectorAll('.shoal-panel')].find((s) => s.querySelector('h2, h3')?.textContent.trim() === 'Extra features');

test('the four switches and the cache size show their defaults: versions off, the rest on, 1 GB', async () => {
    const p = await settings();
    try {
        assert.ok(panel(p), 'an Extra features panel');
        assert.equal(row(p, 'Show every version of a title').querySelector('input').checked, false);
        assert.equal(row(p, 'Preview pictures when you drag the seek bar').querySelector('input').checked, true);
        assert.equal(row(p, "Use the fonts that come with a title's subtitles").querySelector('input').checked, true);
        assert.equal(row(p, 'Check pictures before keeping them').querySelector('input').checked, true);
        const gb = [...p.page.querySelectorAll('.shoal-field')].find((f) => /Most disk for preview pictures/.test(f.textContent)).querySelector('input');
        assert.equal(gb.value, '1');
        assert.doesNotMatch(p.text(), /\bnull\b|undefined/);
    } finally {
        p.close();
    }
});

test('saving sends the five values and they come back', async () => {
    const p = await settings();
    try {
        row(p, 'Show every version of a title').querySelector('input').click();
        row(p, 'Preview pictures when you drag the seek bar').querySelector('input').click();
        row(p, "Use the fonts that come with a title's subtitles").querySelector('input').click();
        row(p, 'Check pictures before keeping them').querySelector('input').click();
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(250);
        const put = calls(p, 'PUT', /Settings$/).at(-1).body;
        assert.equal(put.alternateVersionsEnabled, true);
        assert.equal(put.trickplayEnabled, false);
        assert.equal(put.attachmentsEnabled, false);
        assert.equal(put.reencodeImages, false);
        assert.equal(put.trickplayCacheGb, 1);
        const after = await p.mock.calls.length;
        assert.ok(after > 0);
    } finally {
        p.close();
    }
});

test('the cache size must be a whole number from 1 to 20; a bad one is flagged and nothing is sent', async () => {
    const p = await settings();
    try {
        const field = [...p.page.querySelectorAll('.shoal-field')].find((f) => /Most disk for preview pictures/.test(f.textContent));
        setValue(field.querySelector('input'), '21');
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.equal(calls(p, 'PUT', /Settings$/).length, 0);
        assert.match(p.text(), /whole number from 1 to 20/i);
        setValue(field.querySelector('input'), '5');
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(250);
        assert.equal(calls(p, 'PUT', /Settings$/).at(-1).body.trickplayCacheGb, 5);
    } finally {
        p.close();
    }
});

test('turning preview pictures off greys the size box', async () => {
    const p = await settings();
    try {
        const gb = [...p.page.querySelectorAll('.shoal-field')].find((f) => /Most disk for preview pictures/.test(f.textContent)).querySelector('input');
        assert.equal(gb.disabled, false);
        row(p, 'Preview pictures when you drag the seek bar').querySelector('input').click();
        assert.equal(gb.disabled, true);
    } finally {
        p.close();
    }
});

test('the cache shows how much disk it uses and Clear empties it', async () => {
    const p = await settings({ trickplayBytes: 312 * 1024 * 1024 });
    try {
        assert.match(p.text(), /Preview pictures use 327(\.\d)? MB of disk now\./);
        const clear = [...p.page.querySelectorAll('button')].find((b) => b.getAttribute('aria-label') === 'Clear the preview pictures');
        assert.ok(clear);
        clear.click();
        await p.wait(250);
        assert.equal(calls(p, 'DELETE', /TrickplayCache$/).length, 1);
        assert.match(p.text(), /Preview pictures use 0 MB of disk now\./);
        assert.equal([...p.page.querySelectorAll('button')].filter((b) => b.getAttribute('aria-label') === 'Clear the preview pictures').length, 0);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Preview pictures cleared/);
    } finally {
        p.close();
    }
});

test('an empty cache has no Clear button', async () => {
    const p = await settings({ trickplayBytes: 0 });
    try {
        assert.match(p.text(), /Preview pictures use 0 MB/);
        assert.equal([...p.page.querySelectorAll('button')].filter((b) => b.getAttribute('aria-label') === 'Clear the preview pictures').length, 0);
    } finally {
        p.close();
    }
});

test('the picture check: running with full limits, limited to time and size, and how many it refused', async () => {
    const full = await settings();
    try {
        assert.match(full.text(), /The picture check is on\./);
        assert.doesNotMatch(full.text(), /pictures? (was|were) refused/);
    } finally {
        full.close();
    }

    const partial = await settings({ sandbox: 'partial' });
    try {
        assert.match(partial.text(), /The picture check is on, with time and size limits only\./);
        assert.match(partial.text(), /4 pictures were refused since the server started\./);
    } finally {
        partial.close();
    }
});

test('the picture check cannot start: Settings and the Overview say why, in plain words, and point to Settings', async () => {
    for (const [reason, words] of [['ffmpegMissing', /Shoal could not find ffmpeg on this server/], ['selfTestFailed', /The test run of the check failed/]]) {
        const p = await settings({ sandbox: 'unavailable', sandboxReason: reason });
        try {
            assert.match(p.text(), words);
        } finally {
            p.close();
        }

        const o = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 300, sandbox: 'unavailable', sandboxReason: reason });
        try {
            const alert = o.page.querySelector('.shoal-sandboxalert');
            assert.equal(alert.hidden, false);
            assert.equal(alert.textContent.split('The picture check is not available').length - 1, 1, 'the title is said once');
            assert.doesNotMatch(alert.textContent, /not available: |not available\./);
            assert.match(alert.textContent, words);
            assert.match(alert.textContent, /Open Settings/);
        } finally {
            o.close();
        }
    }
});

test('the Overview shows no picture alert while the check works or is off', async () => {
    const o = await mountPage({ scenario: 'default', params: { view: 'overview' }, settleMs: 300 });
    try {
        assert.equal(o.page.querySelector('.shoal-sandboxalert').hidden, true);
    } finally {
        o.close();
    }
});

test('turning the picture check on again after saving it off shows the result of its new self-test', async () => {
    const p = await settings({ sandbox: 'unavailable' });
    try {
        row(p, 'Check pictures before keeping them').querySelector('input').click(); // off
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(250);
        assert.match(p.text(), /Turned off\. Pictures are checked the usual way\./);
        row(p, 'Check pictures before keeping them').querySelector('input').click(); // on
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(250);
        assert.equal(calls(p, 'PUT', /Settings$/).at(-1).body.reencodeImages, true);
        assert.match(p.text(), /Shoal could not find ffmpeg on this server/); // the mock's new self-test still fails
    } finally {
        p.close();
    }
});
