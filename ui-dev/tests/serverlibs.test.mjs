// 2.9.3 U6 and U5: one entry per friend on the Overview; the server page adds a friend's libraries together and shows who really can see each one.
// Library rows are built from the golden files the backend writes (tests/contracts/peers.detail.json, libraries.get.json).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, golden, visibleText } from './helpers.mjs';

const clone = (v) => JSON.parse(JSON.stringify(v));
const NOBODY = { mode: 'Selected', selectedUserIds: [], includeRestricted: false };
const EVERYONE = { mode: 'Everyone', selectedUserIds: [], includeRestricted: false };
const ONLY_RICH = { mode: 'Selected', selectedUserIds: ['u-rich'], includeRestricted: false };

/** Rows for one server in the golden shape: each def picks the golden row of its type and sets names, counts and access. */
function rowsFrom(handle, serverName, defs) {
    const tmpl = golden('peers.detail', 200).body.libraries;
    return defs.map((d, i) => {
        const base = clone(tmpl.find((r) => r.type === d.type));
        return Object.assign(base, {
            mappingKey: handle + '.lib-' + i, serverHandle: handle, serverName, localLibraryName: d.name, remoteName: d.name,
            count: d.local, remoteCount: d.local, localCount: d.local, hiddenByDedup: d.hidden || 0,
            access: d.access || NOBODY,
            effectiveAccess: d.effective === undefined ? EVERYONE : d.effective,
            libKey: d.libKey === undefined ? (d.type === 'movies' ? 'lk-shoalmov' : 'lk-shoaltv') : d.libKey,
            libraryName: d.libraryName === undefined ? (d.type === 'movies' ? "Friends' movies" : "Friends' TV shows") : d.libraryName,
            placement: d.placement || 'Shoal', mainLibraryName: d.main === undefined ? null : d.main
        });
    });
}

// a friend's shape (NOTES-2.9.3 item 1): 9 libraries, 3 movies and 6 TV, one with titles that are also on another server.
const NINE = [
    { type: 'movies', name: 'Movies', local: 500 }, { type: 'movies', name: '4K Movies', local: 120, hidden: 30 }, { type: 'movies', name: 'Kids Movies', local: 80 },
    { type: 'tvshows', name: 'TV', local: 200 }, { type: 'tvshows', name: 'Anime', local: 60 }, { type: 'tvshows', name: 'Kids TV', local: 40, hidden: 11 },
    { type: 'tvshows', name: 'Docs', local: 25 }, { type: 'tvshows', name: '4K TV', local: 10 }, { type: 'tvshows', name: 'Old TV', local: 5 }
];

async function page(params, extra) {
    const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
    return mountPage(Object.assign({ scenario: 'default', params, settleMs: 300, now: clock.now }, extra || {}));
}

const librariesSection = (p) => [...p.page.querySelectorAll('[data-shoal-view="server"] section')].find((s) => /Libraries from/.test(s.textContent));

test('golden rows: the fixture keeps every field of the backend row', () => {
    const want = Object.keys(golden('peers.detail', 200).body.libraries[0]).sort();
    for (const r of rowsFrom('h-hops', 'Hops', NINE)) {
        assert.deepEqual(Object.keys(r).sort(), want);
    }

    assert.deepEqual(Object.keys(golden('libraries.get', 200).body.mappings[0]).sort(), want);
});

/* ---------------- U6 ---------------- */

test('ui.server.libraries.totalEqualsOverviewCount: the header adds the 9 libraries and matches the Overview row', async () => {
    const rows = rowsFrom('h-hops', 'Hops', NINE);
    const o = await page({ view: 'overview' }, { libraryRows: rows });
    let overviewCount;
    try {
        const row = [...o.page.querySelectorAll('[data-slot="cards"] > *')].find((n) => /Hops/.test(n.textContent));
        overviewCount = Number(/([\d,]+) titles/.exec(visibleText(row))[1].replace(/,/g, ''));
    } finally {
        o.close();
    }

    assert.equal(overviewCount, 1040);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rows });
    try {
        const sec = librariesSection(p);
        assert.equal(sec.querySelector('.shoal-libtotals').textContent, 'Connected to Hops: 1,040 titles in 9 libraries');
        assert.equal(sec.querySelectorAll('.shoal-row--library').length, 9, 'each library keeps its own row');
        assert.match(visibleText(sec), /41 more are also on another server\./);
    } finally {
        p.close();
    }
});

test('ui.server.libraries.groupedByType: Movies then TV shows, each with a subtotal; Remove stays on every row', async () => {
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rowsFrom('h-hops', 'Hops', NINE) });
    try {
        const groups = [...librariesSection(p).querySelectorAll('.shoal-libgroup')];
        assert.deepEqual(groups.map((g) => g.dataset.type), ['movies', 'tvshows']);
        assert.equal(visibleText(groups[0].querySelector('.shoal-libgroup__head')), 'Movies 700 titles in 3 libraries');
        assert.equal(visibleText(groups[1].querySelector('.shoal-libgroup__head')), 'TV shows 340 titles in 6 libraries');
        assert.deepEqual([...groups[1].querySelectorAll('.shoal-row__title')].map((n) => n.textContent), ['TV', 'Anime', 'Kids TV', 'Docs', '4K TV', 'Old TV']);
        for (const g of groups) {
            for (const li of g.querySelectorAll('.shoal-row--library')) {
                assert.ok([...li.querySelectorAll('button')].some((b) => b.textContent.trim() === 'Remove…'), 'Remove kept');
            }
        }
    } finally {
        p.close();
    }
});

test('server page: one library says "1 title in 1 library"; a row without a type (older server) is not grouped', async () => {
    const one = rowsFrom('h-hops', 'Hops', [{ type: 'movies', name: 'Movies', local: 1 }]);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: one });
    try {
        assert.equal(librariesSection(p).querySelector('.shoal-libtotals').textContent, 'Connected to Hops: 1 title in 1 library');
    } finally {
        p.close();
    }

    const old = rowsFrom('h-hops', 'Hops', NINE.slice(0, 2)).map((r) => {
        delete r.type;
        return r;
    });
    const q = await page({ view: 'server', id: 'h-hops' }, { libraryRows: old });
    try {
        const sec = librariesSection(q);
        assert.equal(sec.querySelector('.shoal-libgroup__head'), null);
        assert.equal(sec.querySelectorAll('.shoal-row--library').length, 2);
        assert.equal(sec.querySelector('.shoal-libtotals').textContent, 'Connected to Hops: 620 titles in 2 libraries');
    } finally {
        q.close();
    }
});

/** Visible text with every closed disclosure showing only its summary, as a person sees it. */
function closedText(node) {
    const copy = node.cloneNode(true);
    for (const d of copy.querySelectorAll('details:not([open])')) {
        for (const c of [...d.children]) {
            if (c.tagName.toLowerCase() !== 'summary') {
                c.remove();
            }
        }
    }

    return visibleText(copy);
}

test('ui.overview.oneEntryPerFriend: Watching names each server once; Which server plays first is closed with its count', async () => {
    const p = await page({ view: 'overview' }, { playOrder: true });
    try {
        const view = p.page.querySelector('[data-shoal-view="overview"]');
        const box = view.querySelector('[data-slot="playorderbox"]');
        assert.equal(box.tagName.toLowerCase(), 'details');
        assert.equal(box.open, false);
        assert.equal(visibleText(box.querySelector('summary')), 'Which server plays first (3) Watching');
        assert.equal(box.querySelector('.shoal-row__art, img'), null, 'no row art inside it');
        const watching = view.querySelector('[data-slot="cards"]').closest('section');
        const text = closedText(watching) + ' ' + closedText(view.querySelector('[data-section="playorder"]'));
        for (const name of ['Hops', 'Fernwood', 'Granite']) {
            assert.equal(text.split(name).length - 1, 1, name + ' once: ' + text);
        }

        box.open = true;
        assert.equal(box.querySelectorAll('ol.shoal-prio > li').length, 3);
    } finally {
        p.close();
    }
});

/* ---------------- U5 ---------------- */

const rowOf = (p, name) => [...p.page.querySelectorAll('[data-shoal-view="server"] .shoal-row--library')].find((r) => r.querySelector('.shoal-row__title').textContent === name);
const accessText = (row) => row.querySelector('.shoal-accessline').textContent.trim();
const changeLink = (row) => [...row.querySelectorAll('a')].find((a) => a.textContent === 'Change who can see this');

test('ui.server.libraryRow.showsSharedLibraryAccess: the shared library decides, never the old mapping text', async () => {
    // a friend's case (NOTES-2.9.3 item 3): the mapping says nobody (or only the admin), the shared library says everyone.
    const rows = rowsFrom('h-hops', 'Hops', [{ type: 'movies', name: 'Movies', local: 5, access: ONLY_RICH }, { type: 'tvshows', name: 'TV', local: 7, access: NOBODY }]);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rows });
    try {
        assert.equal(accessText(rowOf(p, 'Movies')), "Everyone on this server, in Friends' movies");
        assert.equal(accessText(rowOf(p, 'TV')), "Everyone on this server, in Friends' TV shows");
        assert.doesNotMatch(visibleText(librariesSection(p)), /Hidden from everyone|Only you/);
    } finally {
        p.close();
    }
});

test('ui.server.libraryRow.changeOpensSharedLibrary: the link opens Libraries & access at that library and focuses its button', async () => {
    const rows = rowsFrom('h-hops', 'Hops', [{ type: 'movies', name: 'Movies', local: 5 }]);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rows });
    let query;
    try {
        const a = changeLink(rowOf(p, 'Movies'));
        assert.equal(a.dataset.shoalTo, 'libraries');
        query = JSON.parse(a.dataset.shoalQuery);
        assert.deepEqual(query, { focus: 'lib-lk-shoalmov' });
        a.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
        await p.wait(30);
        assert.match(p.navigations.at(-1), /focus=lib-lk-shoalmov/);
    } finally {
        p.close();
    }

    const q = await page(Object.assign({ view: 'libraries' }, query), { libraryRows: rows });
    try {
        await q.wait(150);
        assert.equal(document.activeElement && document.activeElement.dataset.fkey, 'who:lk-shoalmov');
        assert.ok(document.activeElement.closest('#lib-lk-shoalmov'));
    } finally {
        q.close();
    }
});

test('ui.server.libraryRow.legacyKeepsOwnAccess: a row no shared library feeds shows its own access and links to itself', async () => {
    const rows = rowsFrom('h-hops', 'Hops', [{ type: 'movies', name: 'Old Movies', local: 5, placement: 'Legacy', libKey: null, libraryName: 'Old Movies', access: ONLY_RICH, effective: ONLY_RICH }]);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rows });
    try {
        const row = rowOf(p, 'Old Movies');
        assert.equal(accessText(row), 'Only you');
        assert.deepEqual(JSON.parse(changeLink(row).dataset.shoalQuery), { focus: 'lib-h-hops.lib-0' });
    } finally {
        p.close();
    }

    // An older server sends none of the 2.9.3 fields: the row reads its own access, as before.
    const old = rows.map((r) => {
        for (const k of ['type', 'effectiveAccess', 'libKey', 'libraryName', 'placement', 'mainLibraryName']) {
            delete r[k];
        }

        return Object.assign(r, { localLibraryName: 'Older' });
    });
    const q = await page({ view: 'server', id: 'h-hops' }, { libraryRows: old });
    try {
        assert.equal(accessText(rowOf(q, 'Older')), 'Only you');
    } finally {
        q.close();
    }
});

test('ui.server.libraryRow.mainNamesMainLibrary: a Main placement names the main library too', async () => {
    const rows = rowsFrom('h-hops', 'Hops', [{ type: 'tvshows', name: 'Anime', local: 5, placement: 'Main', libraryName: "Friends' TV", main: 'Main Shows' }]);
    const p = await page({ view: 'server', id: 'h-hops' }, { libraryRows: rows });
    try {
        assert.equal(accessText(rowOf(p, 'Anime')), "Everyone on this server, in Friends' TV, inside Main Shows");
    } finally {
        p.close();
    }
});
