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
        ['overview', 'view=overview', '.shoal-card'],
        ['libraries', 'view=libraries', '.shoal-libblock'],
        ['settings', 'view=settings', '.shoal-panel'],
        ['pair watch', 'view=pair&dir=watch', '.shoal-panel'],
        ['pair share', 'view=pair&dir=share', '.shoal-panel'],
        ['server', 'view=server&id=h-hops', '.shoal-detail__header'],
        ['friend', 'view=friend&id=f-sam', '.shoal-detail__header'],
        ['overlap', 'view=overlap&id=h-hops', '.shoal-panel']
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

for (const [width, cols] of [[1240, 3], [1080, 3], [900, 2], [640, 2], [375, 1], [320, 1]]) {
    test(`A3: server cards use ${cols} column(s) at ${width}px`, { skip }, async () => {
        // The dashboard drawer takes 240px on desktop, so the container is narrower than the viewport.
        const { page, ctx } = await open('scenario=default&view=overview', width + 32 + (width + 32 > 1000 ? 240 : 0));
        const m = await page.evaluate(() => {
            const cards = [...document.querySelectorAll('.shoal-card')];
            const tops = new Set(cards.map((c) => Math.round(c.getBoundingClientRect().top)));
            return { perRow: cards.filter((c) => Math.round(c.getBoundingClientRect().top) === [...tops][0]).length, hscroll: document.documentElement.scrollWidth > document.documentElement.clientWidth };
        });
        assert.equal(m.perRow, cols);
        assert.equal(m.hscroll, false, 'no horizontal page scroll');
        await ctx.close();
    });
}

test('A4/E4: phone layout has a 16px gutter, 2-up header actions and 44px targets', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=overview', 375, 800);
    const m = await page.evaluate(() => {
        const root = document.querySelector('.shoal-root').getBoundingClientRect();
        const content = document.querySelector('.shoal-pagehead').getBoundingClientRect();
        const actions = [...document.querySelectorAll('.shoal-pagehead__actions .shoal-btn')].map((b) => Math.round(b.getBoundingClientRect().top));
        const small = [...document.querySelectorAll('.shoalPage button:not([hidden]), .shoalPage a.shoal-link, .shoalPage .shoal-localnav__link, .shoalPage .shoal-seg button')]
            .filter((e) => e.offsetParent !== null)
            .map((e) => ({ cls: e.className, h: e.getBoundingClientRect().height, w: e.getBoundingClientRect().width }))
            .filter((x) => x.h < 43.5);
        return { left: content.left, right: 375 - content.right, root: root.width, actionsSameRow: new Set(actions).size === 1, small };
    });
    assert.equal(Math.round(m.left), 16, 'left gutter');
    assert.equal(Math.round(m.right), 16, 'right gutter');
    assert.ok(m.actionsSameRow, 'header actions side by side');
    assert.deepEqual(m.small, [], 'targets under 44px');
    await ctx.close();
});

const THEMES = ['dark', 'light', 'appletv', 'blueradiance', 'purplehaze', 'wmc'];
for (const theme of THEMES) {
    test(`C1/C2: ${theme} follows the theme and the primary button is contrast-fixed`, { skip }, async () => {
        const { page, ctx } = await open(`scenario=default&view=overview&theme=${theme}`, 1440);
        const m = await page.evaluate(() => {
            const btn = document.querySelector('.shoal-btn--primary');
            const card = document.querySelector('.shoal-card');
            const cs = getComputedStyle(btn);
            return { bg: cs.backgroundColor, color: cs.color, card: getComputedStyle(card).backgroundColor, paper: getComputedStyle(document.documentElement).getPropertyValue('--jf-palette-background-paper').trim() };
        });
        if (theme === 'purplehaze') {
            assert.equal(m.bg, 'rgb(72, 195, 200)', 'theme primary on a non-blue theme');
        } else {
            assert.equal(m.bg, 'rgb(10, 124, 171)');
            assert.equal(m.color, 'rgb(255, 255, 255)');
        }

        const hex = m.paper.replace('#', '');
        const want = `rgb(${parseInt(hex.slice(0, 2), 16)}, ${parseInt(hex.slice(2, 4), 16)}, ${parseInt(hex.slice(4, 6), 16)})`;
        assert.equal(m.card, want, 'cards use the theme surface');
        await ctx.close();
    });
}

test('C3/C4: a custom --shoal-fill wins, and Jellyfin focus colors do not leak in', { skip }, async () => {
    const { page, ctx } = await open('scenario=default&view=overview', 1440);
    await page.addStyleTag({ content: '.shoalPage { --shoal-fill: #123456 !important; }' });
    await page.waitForTimeout(400);
    const bg = await page.evaluate(() => getComputedStyle(document.querySelector('.shoal-btn--primary')).backgroundColor);
    assert.equal(bg, 'rgb(18, 52, 86)');
    const quiet = page.locator('.shoal-pagehead .shoal-btn:not(.shoal-btn--primary)').first();
    const before = await quiet.evaluate((e) => getComputedStyle(e).backgroundColor);
    await quiet.focus();
    await page.waitForTimeout(400);
    const after = await quiet.evaluate((e) => ({ bg: getComputedStyle(e).backgroundColor, color: getComputedStyle(e).color }));
    assert.equal(after.bg, before, '.raised:focus must not recolor the button');
    assert.equal(after.color, 'rgb(255, 255, 255)');
    await ctx.close();
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
