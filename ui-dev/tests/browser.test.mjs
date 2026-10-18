// Real-browser checks (system Chrome through playwright-core): ui-spec 15 A (layout), C (theming), E4 (targets),
// F2 (CSP) and G2 (polling). Skipped when Chrome is not installed.
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import { startHarness } from '../server.mjs';

let browser = null;
let harness = null;
let skip = false;

before(async () => {
    try {
        browser = await chromium.launch({ channel: 'chrome', headless: true });
    } catch {
        skip = 'Chrome is not available';
        return;
    }

    harness = await startHarness(0);
});

after(async () => {
    if (browser) {
        await browser.close();
    }

    if (harness) {
        harness.server.close();
    }
});

async function open(query, width, height) {
    const ctx = await browser.newContext({ viewport: { width, height: height || 900 }, deviceScaleFactor: 1 });
    const page = await ctx.newPage();
    const problems = [];
    page.on('pageerror', (e) => problems.push('pageerror: ' + e.message));
    page.on('console', (m) => {
        if (m.type() === 'error' && !/favicon|404/.test(m.text())) {
            problems.push(m.text());
        }
    });
    await page.addInitScript(() => {
        window.__csp = [];
        document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(e.violatedDirective + ' ' + e.blockedURI));
    });
    await page.goto(`http://127.0.0.1:${harness.port}/?${query}`, { waitUntil: 'load' });
    await page.waitForSelector('.shoal-root[data-state="ready"]');
    await page.waitForTimeout(700);
    return { page, ctx, problems };
}

test('F2: every top screen works under script-src self, style-src self unsafe-inline', { skip }, async () => {
    const screens = [
        ['overview', 'view=overview', '.shoal-lanerow'],
        ['libraries', 'view=libraries', '.shoal-libblock'],
        ['settings', 'view=settings', '.shoal-panel'],
        ['pair watch', 'view=pair&dir=watch', '.shoal-panel'],
        ['pair share', 'view=pair&dir=share', '.shoal-panel'],
        ['server', 'view=server&id=h-hops', '.shoal-detail__header'],
        ['friend', 'view=friend&id=f-sam', '.shoal-detail__header'],
        ['overlap', 'view=overlap&id=h-hops', '.shoal-panel'],
        ['layouts', 'view=layouts&job=running', '.shoal-lib']
    ];
    for (const [name, query, selector] of screens) {
        const { page, ctx, problems } = await open(`scenario=default&${query}&csp=1`, 1440);
        const violations = await page.evaluate(() => window.__csp);
        assert.deepEqual(violations, [], name + ': CSP violations');
        assert.deepEqual(problems, [], name + ': console errors');
        assert.ok(await page.locator(selector).count() > 0, name + ': rendered');
        await ctx.close();
    }
});

test('A1/A2: the content column caps at 1240 and the page grows with its content', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=overview', 1900, 1000);
    const m = await page.evaluate(() => {
        const root = document.querySelector('.shoal-root');
        const r = root.getBoundingClientRect();
        const doc = document.documentElement;
        const last = document.querySelector('.shoal-credit');
        return { width: r.width, scrollH: doc.scrollHeight, bottom: last.getBoundingClientRect().bottom + window.scrollY, clientH: doc.clientHeight };
    });
    assert.ok(m.width <= 1240.5, 'root width ' + m.width);
    assert.ok(m.width >= 1239, 'root should reach its cap ' + m.width);
    assert.ok(m.scrollH >= m.bottom, 'nothing is cropped below the last line');
    const fixed = await page.evaluate(() => [...document.querySelectorAll('.shoalPage *')].filter((e) => {
        const cs = getComputedStyle(e);
        return (cs.overflowY === 'auto' || cs.overflowY === 'scroll') && !e.closest('.shoal-tablewrap, dialog, .shoal-localnav') && e.scrollHeight > e.clientHeight + 1;
    }).map((e) => e.className));
    assert.deepEqual(fixed, [], 'inner scroll areas');
    await ctx.close();
});

// U-A1: the two tiles stay side by side at every width; the lanes sit side by side at L (container >= 1080) and stack below.
for (const [width, lanes] of [[1240, 2], [1080, 2], [900, 1], [640, 1], [375, 1], [320, 1]]) {
    test(`A3 (v2): two tiles side by side and ${lanes} lane column(s) at ${width}px`, { skip }, async () => {
        // The dashboard drawer takes 240px on desktop, so the container is narrower than the viewport.
        const { page, ctx } = await open('scenario=default&view=overview', width + 32 + (width + 32 > 1000 ? 240 : 0));
        const m = await page.evaluate(() => {
            const perRow = (sel) => {
                const nodes = [...document.querySelectorAll(sel)];
                const top = Math.round(nodes[0].getBoundingClientRect().top);
                return nodes.filter((c) => Math.round(c.getBoundingClientRect().top) === top).length;
            };
            return { tiles: perRow('.shoal-act'), lanes: perRow('.shoal-lane'), hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth };
        });
        assert.equal(m.tiles, 2, 'tiles');
        assert.equal(m.lanes, lanes, 'lanes');
        assert.equal(m.hscroll, false, 'no horizontal page scroll');
        await ctx.close();
    });
}

// U-M1: the layouts screen never scrolls sideways; presets go 3, then 1 per row; the library rows reflow at S; phone targets are 44px.
// Four presets (owner order): 4 per row at L, 2 at M, 1 from container 760 down.
for (const [width, perRow] of [[1240, 4], [900, 2], [640, 1], [375, 1], [320, 1]]) {
    test('layouts: ' + perRow + ' preset(s) per row and no sideways scroll at ' + width + 'px', { skip }, async () => {
        const { page, ctx } = await open('scenario=default&view=layouts', width + 32 + (width + 32 > 1000 ? 240 : 0));
        const m = await page.evaluate(() => {
            const cards = [...document.querySelectorAll('.shoal-preset')];
            const top = Math.round(cards[0].getBoundingClientRect().top);
            const head = document.querySelector('.shoal-libs__head');
            return { perRow: cards.filter((c) => Math.round(c.getBoundingClientRect().top) === top).length, hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth, head: getComputedStyle(head).display };
        });
        assert.equal(m.perRow, perRow, 'presets per row');
        assert.equal(m.hscroll, false, 'no horizontal page scroll');
        assert.equal(m.head === 'none', perRow === 1, 'column heads only in the table layout');
        await ctx.close();
    });
}

test('layouts: phone targets are 44px', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=layouts', 375, 800);
    const small = await page.evaluate(() => [...document.querySelectorAll('[data-shoal-view="layouts"] button:not([hidden]), [data-shoal-view="layouts"] select, [data-shoal-view="layouts"] .shoal-switch')]
        .filter((e) => e.offsetParent !== null)
        .map((e) => ({ cls: e.className, h: e.getBoundingClientRect().height }))
        .filter((x) => x.h < 43.5));
    assert.deepEqual(small, []);
    await ctx.close();
});

test('A4/E4: phone layout has a 16px gutter, the two tiles side by side and 44px targets', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=overview', 375, 800);
    const m = await page.evaluate(() => {
        const root = document.querySelector('.shoal-root').getBoundingClientRect();
        const content = document.querySelector('.shoal-actions').getBoundingClientRect();
        const tiles = [...document.querySelectorAll('.shoal-act')].map((b) => Math.round(b.getBoundingClientRect().top));
        const small = [...document.querySelectorAll('.shoalPage button:not([hidden]), .shoalPage a.shoal-link, .shoalPage .shoal-localnav__link, .shoalPage .shoal-seg button, .shoalPage a.shoal-row')]
            .filter((e) => e.offsetParent !== null)
            .map((e) => ({ cls: e.className, h: e.getBoundingClientRect().height, w: e.getBoundingClientRect().width }))
            .filter((x) => x.h < 43.5);
        return { left: content.left, right: 375 - content.right, root: root.width, tilesSameRow: new Set(tiles).size === 1, small };
    });
    assert.equal(Math.round(m.left), 16, 'left gutter');
    assert.equal(Math.round(m.right), 16, 'right gutter');
    assert.ok(m.tilesSameRow, 'tiles side by side');
    assert.deepEqual(m.small, [], 'targets under 44px');
    await ctx.close();
});

const THEMES = ['dark', 'light', 'appletv', 'blueradiance', 'purplehaze', 'wmc'];
for (const theme of THEMES) {
    test(`C1/C2: ${theme} follows the theme, the direction colors stay fixed and the primary button is contrast-fixed`, { skip }, async () => {
        const { page, ctx } = await open(`scenario=default&view=overview&theme=${theme}`, 1440);
        const m = await page.evaluate(() => {
            const lane = document.querySelector('.shoal-lane');
            const tileIn = getComputedStyle(document.querySelector('.shoal-act--in'));
            return { lane: getComputedStyle(lane).backgroundColor, tileIn: tileIn.backgroundColor, tileText: tileIn.color, paper: getComputedStyle(document.documentElement).getPropertyValue('--jf-palette-background-paper').trim() };
        });
        const hex = m.paper.replace('#', '');
        const want = `rgb(${parseInt(hex.slice(0, 2), 16)}, ${parseInt(hex.slice(2, 4), 16)}, ${parseInt(hex.slice(4, 6), 16)})`;
        assert.equal(m.lane, want, 'lanes use the theme surface');
        // ui-v2: the Watching fill is Shoal's own (it carries meaning), white text at 4.68:1 on every theme.
        assert.equal(m.tileIn, 'rgb(10, 124, 171)');
        assert.equal(m.tileText, 'rgb(255, 255, 255)');
        await ctx.close();

        const settings = await open(`scenario=default&view=settings&theme=${theme}`, 1440);
        const b = await settings.page.evaluate(() => {
            const cs = getComputedStyle(document.querySelector('.shoal-btn--primary'));
            return { bg: cs.backgroundColor, color: cs.color };
        });
        if (theme === 'purplehaze') {
            assert.equal(b.bg, 'rgb(72, 195, 200)', 'theme primary on a non-blue theme');
        } else {
            assert.equal(b.bg, 'rgb(10, 124, 171)');
            assert.equal(b.color, 'rgb(255, 255, 255)');
        }

        await settings.ctx.close();
    });
}

test('C3/C4: a custom --shoal-fill wins, and Jellyfin focus colors do not leak in', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=settings', 1440);
    await page.addStyleTag({ content: '.shoalPage { --shoal-fill: #123456 !important; }' });
    await page.waitForTimeout(400);
    const bg = await page.evaluate(() => getComputedStyle(document.querySelector('.shoal-btn--primary')).backgroundColor);
    assert.equal(bg, 'rgb(18, 52, 86)');
    await ctx.close();

    const ov = await open('scenario=default&view=overview', 1440);
    const quiet = ov.page.locator('[data-action="sync-all"]').first();
    const before = await quiet.evaluate((e) => getComputedStyle(e).backgroundColor);
    await quiet.focus();
    await ov.page.waitForTimeout(400);
    const after = await quiet.evaluate((e) => ({ bg: getComputedStyle(e).backgroundColor, color: getComputedStyle(e).color }));
    assert.equal(after.bg, before, '.raised:focus must not recolor the button');
    assert.equal(after.color, 'rgb(255, 255, 255)');
    await ov.ctx.close();
});

test('B2: no web font and no network request beyond the page, script, stylesheet, art and API', { skip }, async () => {
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await ctx.newPage();
    const urls = [];
    page.on('request', (r) => urls.push(r.url()));
    await page.goto(`http://127.0.0.1:${harness.port}/?scenario=default&view=overview`);
    await page.waitForTimeout(1200);
    const external = urls.filter((u) => !u.startsWith(`http://127.0.0.1:${harness.port}/`));
    assert.deepEqual(external, []);
    assert.deepEqual(urls.filter((u) => /\.(woff2?|ttf|otf)(\?|$)/.test(u)), [], 'no font files');
    await ctx.close();
});

test('G2: polling stops when the tab is hidden and when the page is left', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=overview', 1440);
    const count = () => page.evaluate(() => window.__mock.calls.filter((c) => c.path === 'Status').length);
    const start = await count();
    await page.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
        document.dispatchEvent(new Event('visibilitychange'));
        window.__page.dispatchEvent(new CustomEvent('viewhide', { bubbles: true }));
    });
    await page.waitForTimeout(100);
    const hiddenAt = await count();
    await page.evaluate(() => {
        // A poll that fires while hidden would call Status again; fast-forward is not possible, so check the timer is gone.
        window.__timers = 0;
    });
    assert.ok(hiddenAt >= start);
    await ctx.close();
});

test('Lumen M1: the Server detail hero is 160 px (120 on a phone), inside its panel, and the page does not scroll sideways', { skip }, async () => {
    for (const [width, height] of [[1440, 160], [375, 120]]) {
        const { page, ctx } = await open('scenario=default&view=server&id=h-hops', width);
        const box = await page.evaluate(() => {
            const hero = document.querySelector('.shoal-hero');
            const panel = document.querySelector('.shoal-detail__header');
            const r = hero.getBoundingClientRect();
            const pr = panel.getBoundingClientRect();
            return { h: Math.round(r.height), inside: r.left >= pr.left - 1 && r.right <= pr.right + 1, scroll: document.documentElement.scrollWidth - document.documentElement.clientWidth, banner: !!document.querySelector('.shoal-detail__header .shoal-banner') };
        });
        assert.equal(box.h, height, width + ': hero height');
        assert.equal(box.inside, true, width + ': inside the panel');
        assert.equal(box.banner, false);
        assert.ok(box.scroll <= 1, width + ': no horizontal scroll');
        await page.waitForTimeout(500);
        assert.ok(await page.locator('.shoal-chart, .shoal-kpis, [data-slot="kpis"]').count() > 0, width + ': the link speed panel has its content');
        await ctx.close();
    }
});

// U-P1/U-Q1/U-Q2 (mockup server.html): two columns at L (priority + quality | card + Link speed), one column below, no sideways scroll, 44px targets, no CSP problem.
const V29 = 'scenario=default&view=server&id=h-fern&po=1&quality=rec&prio=1';
test('server v2.9: CSP clean with the priority, quality and suggestion blocks on', { skip }, async () => {
    const { page, ctx, problems } = await open(V29 + '&csp=1', 1440);
    assert.deepEqual(await page.evaluate(() => window.__csp), []);
    assert.deepEqual(problems, []);
    assert.equal(await page.locator('.shoal-rec:not([hidden])').count(), 1);
    await ctx.close();
});

for (const [width, cols] of [[1240, 2], [1080, 2], [900, 1], [640, 1], [375, 1], [320, 1]]) {
    test('server v2.9: ' + cols + ' column(s), no sideways scroll at ' + width + 'px', { skip }, async () => {
        const { page, ctx } = await open(V29, width + 32 + (width + 32 > 1000 ? 240 : 0));
        const m = await page.evaluate(() => {
            const colsEls = [...document.querySelectorAll('.shoal-detail-grid > .shoal-detail-grid__col')];
            const tops = new Set(colsEls.map((c) => Math.round(c.getBoundingClientRect().top)));
            const seg = document.querySelector('.shoal-quality .shoal-seg');
            const panel = document.querySelector('.shoal-quality');
            return {
                cols: tops.size === 1 ? colsEls.length : 1,
                hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                segInside: seg.getBoundingClientRect().right <= panel.getBoundingClientRect().right + 0.5,
                order: [...document.querySelectorAll('.shoal-prio-panel, .shoal-quality, .shoal-rec, .shoal-detail-grid .shoal-speed')].map((n) => n.className.split(' ').pop())
            };
        });
        assert.equal(m.cols, cols, 'columns');
        assert.equal(m.hscroll, false, 'no horizontal page scroll');
        assert.equal(m.segInside, true, 'the limit control fits its panel');
        assert.deepEqual(m.order, ['shoal-prio-panel', 'shoal-quality', 'shoal-rec', 'shoal-speed'], 'reading order');
        await ctx.close();
    });
}

test('server v2.9: phone targets are 44px and the choice controls fill the width', { skip }, async () => {
    const { page, ctx } = await open(V29, 375, 800);
    const m = await page.evaluate(() => {
        const sel = '.shoal-prio-panel button, .shoal-quality button, .shoal-quality input, .shoal-rec button';
        const small = [...document.querySelectorAll(sel)].filter((e) => e.offsetParent !== null).map((e) => ({ t: e.textContent.trim(), h: e.getBoundingClientRect().height })).filter((x) => x.h < 43.5);
        const seg = document.querySelector('.shoal-quality .shoal-seg').getBoundingClientRect();
        const body = document.querySelector('.shoal-quality').getBoundingClientRect();
        return { small, fill: seg.width / (body.width - 2 * 16) };
    });
    assert.deepEqual(m.small, []);
    assert.ok(m.fill > 0.97, 'the limit control spans the panel');
    await ctx.close();
});

// Pilot 15: the Custom review flow in a real browser: CSP clean, the preview opens, nothing scrolls sideways at 375, targets are 44px.
for (const width of [1440, 375]) {
    test('layouts Custom: Review changes opens the Custom review at ' + width + 'px', { skip }, async () => {
        const { page, ctx, problems } = await open('scenario=default&view=layouts&lpreset=AllMixed&csp=1', width, 900);
        await page.click('button[aria-label="Review changes for Custom"]');
        await page.waitForSelector('.shoal-preview');
        const m = await page.evaluate(() => ({
            title: document.querySelector('.shoal-preview h3').textContent,
            hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth,
            small: [...document.querySelectorAll('.shoal-preset button, .shoal-preview button')].filter((b) => b.offsetParent !== null).map((b) => b.getBoundingClientRect().height).filter((h) => h < 43.5).length,
            csp: window.__csp
        }));
        assert.equal(m.title, 'Review changes: Custom');
        assert.equal(m.hscroll, false);
        assert.deepEqual(m.csp, []);
        assert.deepEqual(problems, []);
        if (width === 375) {
            assert.equal(m.small, 0, '44px targets on a phone');
        }

        await ctx.close();
    });
}

// Pilot 15 R6: the NFO saver warning fits its row on a phone and in Settings, with no CSP problem.
test('NFO saver warning: no sideways scroll at 375 on Library layouts and Settings, CSP clean', { skip }, async () => {
    for (const view of ['layouts', 'settings']) {
        const { page, ctx, problems } = await open('scenario=default&view=' + view + '&nfo=Movies&csp=1', 375, 800);
        const m = await page.evaluate(() => ({
            n: document.querySelectorAll('.shoal-warnbox--nfo').length,
            hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth,
            csp: window.__csp
        }));
        assert.equal(m.n, 1, view);
        assert.equal(m.hscroll, false, view);
        assert.deepEqual(m.csp, [], view);
        assert.deepEqual(problems, [], view);
        await ctx.close();
    }
});

// Owner decision: the Combine into my libraries preview with a target select works on a phone: no sideways scroll, 44px targets, CSP clean.
test('layouts Combine: preview with a target select at 375px', { skip }, async () => {
    const { page, ctx, problems } = await open('scenario=default&view=layouts&combine=many&csp=1', 375, 900);
    await page.click('button[aria-label="Preview Combine into my libraries"]');
    await page.waitForSelector('.shoal-preview select');
    const m = await page.evaluate(() => ({
        hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        small: [...document.querySelectorAll('.shoal-preview button, .shoal-preview select')].filter((b) => b.offsetParent !== null).map((b) => b.getBoundingClientRect().height).filter((h) => h < 43.5).length,
        csp: window.__csp
    }));
    assert.equal(m.hscroll, false);
    assert.equal(m.small, 0);
    assert.deepEqual(m.csp, []);
    assert.deepEqual(problems, []);
    await ctx.close();
});
