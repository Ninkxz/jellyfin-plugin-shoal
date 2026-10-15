// Contrast evidence (ui-spec 12.1): the Shoal tokens in shoal.css, composited over the theme surfaces, meet WCAG AA.
import test from 'node:test';
import assert from 'node:assert/strict';
import { css } from './helpers.mjs';

function parseColor(c) {
    c = c.trim().toLowerCase();
    let m = /^#([0-9a-f]{6})$/.exec(c);
    if (m) {
        return { r: parseInt(m[1].slice(0, 2), 16), g: parseInt(m[1].slice(2, 4), 16), b: parseInt(m[1].slice(4, 6), 16), a: 1 };
    }

    m = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+))?\s*\)$/.exec(c);
    if (m) {
        return { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] };
    }

    throw new Error('color: ' + c);
}

/** var(--x, #abc) -> #abc. */
function plain(c) {
    const m = /^var\([^,]+,\s*(.+)\)$/.exec(c.trim());
    return m ? m[1] : c;
}

function over(fg, bg) {
    return { r: fg.r * fg.a + bg.r * (1 - fg.a), g: fg.g * fg.a + bg.g * (1 - fg.a), b: fg.b * fg.a + bg.b * (1 - fg.a), a: 1 };
}

function lum(c) {
    const f = (v) => {
        const s = v / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}

function ratio(a, b) {
    const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
}

/** Reads `--shoal-name: value;` declarations from the first rule whose selector contains `selector`. */
function tokens(selector) {
    const start = css.indexOf(selector);
    assert.ok(start >= 0, selector);
    const open = css.indexOf('{', start);
    const close = css.indexOf('}', open);
    const out = {};
    for (const m of css.slice(open, close).matchAll(/--shoal-([a-z0-9-]+):\s*([^;]+);/g)) {
        out[m[1]] = m[2].trim();
    }

    return out;
}

const dark = { surface: '#202020', text: '#ffffff', set: tokens('/* Dark signal set') };
const darkTokens = tokens('.shoalPage {');
const lightTokens = tokens('html[data-theme="light"] .shoalPage, html[data-theme="appletv"] .shoalPage, .shoalPage.shoal-light');

const SETS = [
    { name: 'dark', surface: parseColor('#202020'), text: parseColor('#ffffff'), t: darkTokens },
    { name: 'light', surface: parseColor('#e8e8e8'), text: parseColor('#000000'), t: Object.assign({}, darkTokens, lightTokens) }
];

for (const set of SETS) {
    test(set.name + ': status colors are readable on the surface and on their chip backgrounds', () => {
        for (const tone of ['ok', 'warn', 'bad', 'off']) {
            const fg = parseColor(set.t[tone]);
            const chipBg = over(parseColor(set.t[tone + '-bg']), set.surface);
            assert.ok(ratio(fg, set.surface) >= 4.5, tone + ' on surface ' + ratio(fg, set.surface).toFixed(2));
            assert.ok(ratio(fg, chipBg) >= 4.5, tone + ' on chip ' + ratio(fg, chipBg).toFixed(2));
        }
    });

    test(set.name + ': muted text, links and the control border', () => {
        const muted = over(parseColor(plain(set.t.muted)), set.surface);
        assert.ok(ratio(muted, set.surface) >= 4.5, 'muted ' + ratio(muted, set.surface).toFixed(2));
        const link = parseColor(plain(set.t.link));
        assert.ok(ratio(link, set.surface) >= 4.5, 'link ' + ratio(link, set.surface).toFixed(2));
        const ink = set.name === 'dark' ? parseColor('#ffffff') : parseColor('#000000');
        const border = over({ ...ink, a: set.name === 'dark' ? 0.38 : 0.45 }, set.surface);
        assert.ok(ratio(border, set.surface) >= 3, 'control border ' + ratio(border, set.surface).toFixed(2));
        const hint = over(parseColor(set.t['hint-warn-bg']), set.surface);
        assert.ok(ratio(set.text, hint) >= 4.5, 'text on warn hint');
    });
}

test('the primary button: white on #0a7cab, hover and pressed (ui-spec 12.1)', () => {
    const white = parseColor('#ffffff');
    assert.ok(ratio(white, parseColor('#0a7cab')) >= 4.5);
    assert.ok(ratio(white, parseColor('#086c95')) >= 5.8);
    assert.ok(ratio(white, parseColor('#075a7d')) >= 7.5);
    assert.ok(ratio(white, parseColor('#c62828')) >= 5.5, 'danger');
    assert.ok(ratio(parseColor('#f2f2f2'), over(parseColor('rgba(12,12,12,.84)'), parseColor('#ffffff'))) >= 11, 'pill over a white image');
});

test('the stylesheet is plain ASCII, so it survives a response without a charset', () => {
    assert.equal(/[^\x00-\x7f]/.test(css), false);
});
