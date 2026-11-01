// 2.10.4 Lumen F1: the preview lives only on Library management's Overlap tab now; the overlap report shows the delete section.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, golden } from './helpers.mjs';

const box = (p) => p.page.querySelector('[data-local-removal-preview]');
const action = (p, name) => box(p).querySelector('[data-action="local-preview-' + name + '"]');
const fixture = () => golden('local-removal-preview.get', 200);
const mount = (extra) => mountPage({ params: { view: 'manage', id: 'h-hops' }, localRemovalPreviewGolden: fixture(), settleMs: 160, ...extra });

test('golden preview shows blocked movies, no destructive controls, and only GET requests', async () => {
    const p = await mount();
    try {
        assert.match(box(p).textContent, /Nothing is deleted/);
        assert.match(box(p).textContent, /Space reclaimed by this preview: 0 bytes/);
        assert.match(box(p).textContent, /Sintel \(2010\)/);
        assert.match(box(p).textContent, /Not verified safe to remove/);
        assert.doesNotMatch(box(p).textContent, /Rows may refer to the same local copy/);
        // The only form controls are the title filter (search) and the sort select (2.10.3 T4); neither sends anything.
        assert.equal(box(p).querySelectorAll('input:not([data-local-preview-filter]), select:not([data-local-preview-filter]), .shoal-btn--danger').length, 0);
        assert.deepEqual([...box(p).querySelectorAll('[data-local-preview-filter]')].map((n) => n.type), ['search', 'select-one']);
        assert.equal(action(p, 'previous').disabled, true);
        assert.equal(action(p, 'next').disabled, true);
        const calls = p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview'));
        assert.equal(calls.length, 1);
        assert.equal(calls[0].method, 'GET');
        assert.match(calls[0].path, /\?offset=0&limit=50$/);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally { p.close(); }
});

test('hostile movie title remains text and creates no injected element', async () => {
    const reply = fixture();
    reply.body.items[0].title = '<img src=x onerror=alert(1)>';
    const p = await mount({ localRemovalPreviewGolden: reply });
    try {
        assert.match(box(p).textContent, /<img src=x onerror=alert\(1\)>/);
        assert.equal(box(p).querySelectorAll('img, script').length, 0);
    } finally { p.close(); }
});

for (const [name, change] of [
    ['enabled removal', (b) => { b.removalEnabled = true; }],
    ['safe row', (b) => { b.items[0].safeToRemove = true; }],
    ['missing blocker', (b) => { b.items[0].blockers.pop(); }],
    ['claimed reclaimed space', (b) => { b.reclaimableBytes = 8000; }],
    ['unbounded page', (b) => { b.limit = 1000; }],
    ['inconsistent paging', (b) => { b.hasMore = true; }],
    ['more rows than total', (b) => { b.total = 0; }],
    ['incomplete page claiming more rows', (b) => { b.total = 2; b.hasMore = true; }]
]) {
    test('fails closed for ' + name, async () => {
        const reply = fixture();
        change(reply.body);
        const p = await mount({ localRemovalPreviewGolden: reply });
        try {
            assert.match(box(p).textContent, /preview could not be verified/);
            assert.equal(box(p).querySelector('table'), null);
            assert.equal(action(p, 'refresh').disabled, false);
        } finally { p.close(); }
    });
}

test('golden never-synced and disabled states do not show overlap rows', async () => {
    for (const [name, text] of [['empty', /No completed catalog sync yet/], ['disabled', /This friend is disabled/]]) {
        const p = await mount({ localRemovalPreviewGolden: golden('local-removal-preview.' + name, 200) });
        try {
            assert.match(box(p).textContent, text);
            assert.equal(box(p).querySelector('table'), null);
        } finally { p.close(); }
    }
});

test('recovery refusal stays separate from an empty preview and refresh can retry', async () => {
    const p = await mount({ refuse: [{ re: /^GET Peers\/[^/]+\/LocalRemovalPreview$/, status: 409, error: 'recovery' }] });
    try {
        assert.match(box(p).textContent, /Saved library recovery must finish/);
        p.mock.set({ refuse: [] });
        action(p, 'refresh').click();
        await p.wait(80);
        assert.match(box(p).textContent, /Sintel/);
        assert.equal(p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview')).length, 2);
    } finally { p.close(); }
});

test('older server reports unavailable without a misleading no-overlap result', async () => {
    const p = await mount({ localRemovalPreviewGolden: null });
    try {
        assert.match(box(p).textContent, /does not provide a local copy removal preview yet/);
        assert.doesNotMatch(box(p).textContent, /No cached movie overlaps/);
    } finally { p.close(); }
});

test('next and previous pages request bounded offsets and replace rows', async () => {
    const first = fixture();
    first.body.items = Array.from({ length: 50 }, (_, i) => ({ ...first.body.items[0], title: 'Movie ' + i }));
    first.body.total = 51;
    first.body.hasMore = true;
    const next = fixture();
    next.body.offset = 50;
    next.body.total = 51;
    next.body.items[0].title = 'Last movie';
    const p = await mount({ localRemovalPreviewGolden: { 0: first, 50: next } });
    try {
        assert.equal(box(p).querySelectorAll('tbody tr').length, 50);
        action(p, 'next').click();
        await p.wait(80);
        assert.match(box(p).textContent, /Last movie/);
        assert.doesNotMatch(box(p).textContent, /Movie 0/);
        assert.equal(action(p, 'next').disabled, true);
        assert.equal(action(p, 'previous').disabled, false);
        action(p, 'previous').click();
        await p.wait(80);
        assert.equal(box(p).querySelectorAll('tbody tr').length, 50);
        assert.deepEqual(p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview')).map((c) => c.path.match(/offset=(\d+)/)[1]), ['0', '50', '0']);
    } finally { p.close(); }
});

test('late response for a previous friend cannot replace the new friend preview', async () => {
    const p = await mount();
    const original = p.mock.handle.bind(p.mock);
    let release;
    p.mock.handle = async (method, path, body) => {
        if (path.includes('h-hops/LocalRemovalPreview')) {
            await new Promise((resolve) => { release = resolve; });
            const reply = fixture();
            reply.body.items[0].title = 'Stale old friend';
            return { ...reply, headers: {} };
        }
        return original(method, path, body);
    };
    try {
        action(p, 'refresh').click();
        await p.wait(30);
        const select = p.page.querySelector('[data-slot="manageselect"]');
        select.value = 'h-fern';
        select.dispatchEvent(new window.Event('change', { bubbles: true }));
        await p.wait(80);
        assert.match(box(p).textContent, /Sintel/);
        release();
        await p.wait(60);
        assert.doesNotMatch(box(p).textContent, /Stale old friend/);
        assert.equal(p.page.querySelectorAll('[data-local-removal-preview]').length, 1);
    } finally { if (release) release(); p.close(); }
});

// 2.10.3 T4 (Lumen section 1): a real table, title search and size sort on the page's rows, a normal-size pager on the right.
const threeRows = () => {
    const reply = fixture();
    const base = reply.body.items[0];
    reply.body.items = [
        { ...base, title: 'Sintel', year: 2010, localBytesEstimate: 8000 },
        { ...base, title: 'Big Buck Bunny', year: null, localBytesEstimate: 30000 },
        { ...base, title: 'Tears of Steel', year: 2012, localBytesEstimate: 2000 }
    ];
    reply.body.total = 3;
    return reply;
};
const titles = (p) => [...box(p).querySelectorAll('tbody th[scope="row"]')].map((th) => th.textContent);

test('T4: a real table with caption, column and row headers, right-aligned sizes and the status said once above it (Lumen F4)', async () => {
    const p = await mount({ localRemovalPreviewGolden: threeRows() });
    try {
        const table = box(p).querySelector('table');
        assert.equal(table.querySelector('caption').textContent, 'Local copy removal preview. No row is verified safe to remove.');
        assert.deepEqual([...table.querySelectorAll('thead th')].map((th) => [th.getAttribute('scope'), th.textContent]), [['col', 'Title'], ['col', 'Size (estimate)']]);
        // Size, largest first by default, and the column says so.
        assert.deepEqual(titles(p), ['Big Buck Bunny', 'Sintel (2010)', 'Tears of Steel (2012)']);
        assert.equal(table.querySelectorAll('thead th')[1].getAttribute('aria-sort'), 'descending');
        assert.equal(table.querySelectorAll('thead th')[0].hasAttribute('aria-sort'), false);
        const first = table.querySelector('tbody tr');
        assert.equal(first.querySelector('th').getAttribute('title'), 'Big Buck Bunny');
        assert.ok(first.querySelectorAll('td')[0].classList.contains('is-num'));
        assert.equal(table.querySelectorAll('.shoal-chip').length, 0, 'no chip repeated on every row');
        const status = box(p).querySelector('.shoal-lrstatus');
        assert.equal(status.querySelectorAll('.shoal-chip').length, 1);
        assert.equal(status.querySelector('.shoal-chip').textContent, 'Not verified safe to remove');
        assert.ok(status.querySelector('.shoal-chip svg use'), 'icon plus text, not colour alone');
        assert.match(status.textContent, /This applies to every row below./);
        // The blockers are one sentence above the table, not repeated per row.
        assert.equal(box(p).textContent.split('still need verification').length - 1, 1);
    } finally { p.close(); }
});

test('T4: the title filter and the sort work on this page only, announce the count, and offer Clear filter when nothing matches', async () => {
    const p = await mount({ localRemovalPreviewGolden: threeRows() });
    try {
        const input = box(p).querySelector('input[type="search"]');
        const sort = box(p).querySelector('select[data-local-preview-filter]');
        assert.equal(box(p).querySelector('label[for="' + input.id + '"]').textContent, 'Filter titles');
        assert.equal(box(p).querySelector('label[for="' + sort.id + '"]').textContent, 'Sort');
        input.value = 'el';
        input.dispatchEvent(new window.Event('input', { bubbles: true }));
        assert.deepEqual(titles(p), ['Sintel (2010)', 'Tears of Steel (2012)']);
        assert.equal(p.page.querySelector('[data-slot="live"]').textContent, '2 rows on this page match.');
        sort.value = 'smallest';
        sort.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.deepEqual(titles(p), ['Tears of Steel (2012)', 'Sintel (2010)']);
        assert.equal(box(p).querySelectorAll('thead th')[1].getAttribute('aria-sort'), 'ascending');
        sort.value = 'title';
        sort.dispatchEvent(new window.Event('change', { bubbles: true }));
        assert.deepEqual(titles(p), ['Sintel (2010)', 'Tears of Steel (2012)']);
        assert.equal(box(p).querySelectorAll('thead th')[0].getAttribute('aria-sort'), 'ascending');
        input.value = 'zzz';
        input.dispatchEvent(new window.Event('input', { bubbles: true }));
        assert.equal(box(p).querySelector('table'), null);
        assert.match(box(p).textContent, /No copies match\./);
        assert.equal(p.page.querySelector('[data-slot="live"]').textContent, '0 rows on this page match.');
        box(p).querySelector('[data-action="local-preview-clear"]').click();
        assert.equal(input.value, '');
        assert.equal(titles(p).length, 3);
        // Filtering and sorting never call the server.
        assert.equal(p.mock.calls.filter((c) => c.path.includes('/LocalRemovalPreview')).length, 1);
        assert.equal(p.mock.calls.filter((c) => c.method !== 'GET').length, 0);
    } finally { p.close(); }
});

test('T4: the pager is normal size, says the page, and the filter row hides when there are no rows', async () => {
    const first = fixture();
    first.body.items = Array.from({ length: 50 }, (_, i) => ({ ...first.body.items[0], title: 'Movie ' + i }));
    first.body.total = 51;
    first.body.hasMore = true;
    const p = await mount({ localRemovalPreviewGolden: { 0: first, 50: fixture() } });
    try {
        const pager = box(p).querySelector('.shoal-pager');
        assert.equal(pager.getAttribute('role'), 'group');
        assert.deepEqual([...pager.querySelectorAll('button')].map((b) => [b.textContent.trim(), b.classList.contains('shoal-btn--small')]), [['Previous', false], ['Next', false]]);
        assert.match(pager.textContent, /Page 1 of 2/);
        assert.match(box(p).querySelector('.shoal-lrfoot').textContent, /Showing 1 to 50 of 51 cached overlap rows\./);
        assert.equal(box(p).querySelector('.shoal-lrtools').hidden, false);
    } finally { p.close(); }

    const q = await mount({ localRemovalPreviewGolden: golden('local-removal-preview.empty', 200) });
    try {
        assert.equal(box(q).querySelector('.shoal-lrtools').hidden, true);
    } finally { q.close(); }
});
