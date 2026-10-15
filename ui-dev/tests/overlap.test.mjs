// Overlap report (ui-spec 8.6) against the mock admin API.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage } from './helpers.mjs';

test('the report has the sentence, the by-quality table with totals and the update line', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'overlap', id: 'h-hops' }, settleMs: 350 });
    try {
        const t = p.text();
        assert.match(t, /Your copy always plays when you both have a title\./);
        assert.match(t, /By quality/);
        assert.match(t, /Updated after the last full sync, /);
        const table = p.page.querySelector('[data-shoal-view="overlap"] table');
        assert.ok(table);
        const rows = [...table.querySelectorAll('tbody tr')].map((r) => r.querySelector('th, td').textContent.trim());
        assert.deepEqual(rows, ['4K', '1080p', '720p', 'SD', 'Total']);
        assert.match(table.textContent, /New from Hops/);
        assert.match(t, /Hops's copy is better than yours for 14 titles\./);
        assert.match(t, /Alternate versions are coming in a later version\./);
    } finally {
        p.close();
    }
});

test('with no report yet it says when one appears', async () => {
    const p = await mountPage({ scenario: 'nothingShared', params: { view: 'overlap', id: 'h-hops' }, settleMs: 350 });
    try {
        assert.match(p.text(), /No overlap report yet\. It appears after the first full sync\./);
    } finally {
        p.close();
    }
});
