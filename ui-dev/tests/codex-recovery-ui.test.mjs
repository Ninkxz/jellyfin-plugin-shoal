import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

const message = "Shoal cannot read this server's saved library list. Sync and library changes are paused to keep existing files safe. Restore a known-good Shoal backup before trying again.";
const button = (root, label) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === label);

test('overlapping episode ranges explain the hold without offering an override', async () => {
    const p = await mountPage({ params: { view: 'server', id: 'h-fern' }, episodeRangeConflict: true, settleMs: 250 });
    try {
        const alert = [...p.page.querySelectorAll('.shoal-alert')].find((e) => e.textContent.includes('Episode numbering overlaps'));
        assert.ok(alert);
        assert.ok(alert.classList.contains('shoal-hint--warn'));
        assert.ok(alert.textContent.includes('Approving additions cannot override this check.'));
        assert.equal(alert.querySelectorAll('button, a').length, 0);
        assert.ok(p.mock.calls.every((c) => c.method === 'GET'));
    } finally { p.close(); }
});

test('catalog recovery alert names the safe recovery step with no fake restore or retry action', async () => {
    const p = await mountPage({ params: { view: 'server', id: 'h-fern' }, catalogRecovery: true, settleMs: 250 });
    try {
        const alert = [...p.page.querySelectorAll('.shoal-alert')].find((e) => e.textContent.includes('Saved library list needs recovery'));
        assert.ok(alert);
        assert.ok(alert.classList.contains('shoal-hint--bad'));
        assert.ok(alert.textContent.includes(message));
        assert.equal(alert.querySelectorAll('button, a').length, 0);
        assert.ok(p.mock.calls.every((c) => c.method === 'GET'));
    } finally { p.close(); }
});

test('Overview exposes the catalog recovery alert through its existing attention link to server alerts', async () => {
    const p = await mountPage({ params: { view: 'overview' }, catalogRecovery: true, settleMs: 250 });
    try {
        const row = [...p.page.querySelectorAll('.shoal-row')].find((e) => e.querySelector('[data-slot="nametext"]')?.textContent === 'Fernwood');
        assert.ok(row);
        const link = row.parentElement.querySelector('[data-slot="hold"] a');
        assert.ok(link);
        assert.match(link.href, /view=server/);
        assert.match(link.href, /id=h-fern/);
        assert.match(link.href, /focus=alerts/);
        assert.equal(row.dataset.attention, '1');
    } finally { p.close(); }
});

test('catalogUnreadable refusal explains recovery without repeating the rejected write', async () => {
    const p = await mountPage({ params: { view: 'server', id: 'h-fern' }, refuse: [{ re: /^POST Peers\/h-fern\/Sync$/, status: 409, error: 'catalogUnreadable' }], settleMs: 250 });
    try {
        button(p.page.querySelector('.shoal-detail__header'), 'Sync now').click();
        await p.wait(150);
        const bar = p.page.querySelector('.shoal-errorbar');
        assert.ok(bar.textContent.includes(message));
        assert.equal(button(bar, 'Try again'), undefined);
        assert.equal(button(bar, 'Reload page data'), undefined);
        await p.wait(80);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'Peers/h-fern/Sync').length, 1);
    } finally { p.close(); }
});
