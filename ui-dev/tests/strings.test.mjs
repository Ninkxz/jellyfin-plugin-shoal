// Copy rules (ui-spec 11, 15 D2): every string the page asks for exists, and the banned words never appear.
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadShoal, js, html } from './helpers.mjs';

const mod = await loadShoal();

test('every literal copy key used in shoal.js exists in STRINGS', () => {
    const missing = new Set();
    for (const m of js.matchAll(/\bt(?:Rich)?\(\s*'([A-Za-z0-9_?]+)'/g)) {
        if (!m[1].endsWith('_') && mod.STRINGS[m[1]] === undefined) {
            missing.add(m[1]);
        }
    }

    assert.deepEqual([...missing], []);
});

test('every data-t hook in shoal.html exists in STRINGS', () => {
    const missing = [];
    for (const m of html.matchAll(/data-t(?:-aria|-ph)?="([A-Za-z0-9_]+)"/g)) {
        if (mod.STRINGS[m[1]] === undefined) {
            missing.push(m[1]);
        }
    }

    assert.deepEqual(missing, []);
});

test('dynamic copy keys have their full family (verdicts, ranges, drift kinds ...)', () => {
    const families = {
        verdictWord_: ['smooth', 'mayBuffer', 'needsTranscode', 'willBuffer'],
        addr_: ['tailnet', 'lan', 'public'],
        range_: ['24h', '7d', '30d'],
        rangeLabel_: ['24h', '7d', '30d'],
        ladderClass_: ['remux4k', 'remux1080', '4k', '1080p', '1080pTranscode', '720p']
    };
    for (const [prefix, names] of Object.entries(families)) {
        for (const n of names) {
            assert.notEqual(mod.STRINGS[prefix + n], undefined, prefix + n);
        }
    }
});

test('words the UI never uses (ui-spec 11) do not appear in any copy', () => {
    const banned = [/\bpeers?\b/i, /\bfederat/i, /\bSAS\b/, /LogOnly/, /Enforce/, /access mode/i, /\bdrift\b/i, /recovery mode/i, /\bsecrets\b/i, /share user/i, /check string/i, /\bhttp (error|status)\b/i];
    const bad = [];
    for (const [key, value] of Object.entries(mod.STRINGS)) {
        for (const v of [].concat(value)) {
            for (const re of banned) {
                if (re.test(String(v).replace(/secrets\.enc|master\.key/g, ''))) {
                    bad.push(key + ' -> ' + re);
                }
            }
        }
    }

    assert.deepEqual(bad, []);
});

test('copy placeholders are balanced and plural pairs have two entries', () => {
    for (const [key, value] of Object.entries(mod.STRINGS)) {
        if (Array.isArray(value)) {
            assert.equal(value.length, 2, key + ' plural pair');
        }

        for (const v of [].concat(value)) {
            assert.equal((String(v).match(/\{/g) || []).length, (String(v).match(/\}/g) || []).length, key + ' braces');
            assert.equal((String(v).match(/\*\*/g) || []).length % 2, 0, key + ' bold markers');
        }
    }
});

test('safety-code copy matches the v2 formats', () => {
    assert.match(mod.STRINGS.safetyCodeLabel, /Safety code/);
    assert.equal(mod.STRINGS.pickerEveryone, 'Select all'); // ui-spec 8.4.3: the picker's top row
    assert.equal(mod.STRINGS.modeSelected, 'Only selected users');
    assert.equal(mod.STRINGS.modeEveryone, 'Everyone on this server');
});

test('no copy key is defined twice (the later one would silently win)', () => {
    const start = js.indexOf('const STRINGS = {');
    const end = js.indexOf('\n};', start);
    const keys = [...js.slice(start, end).matchAll(/^\s{4}"?([A-Za-z0-9_?]+)"?:/gm)].map((m) => m[1]);
    const seen = new Set();
    const dup = keys.filter((k) => (seen.has(k) ? true : (seen.add(k), false)));
    assert.deepEqual(dup, []);
});

test('every copy key is used somewhere (a dead string can be revived by a later edit; Lumen S5)', () => {
    const start = js.indexOf('const STRINGS = {');
    const end = js.indexOf('\n};', start);
    const code = js.slice(0, start) + js.slice(end);
    const prefixes = new Set([...code.matchAll(/'([A-Za-z0-9_]+)'\s*\+/g)].map((m) => m[1]));
    for (const m of html.matchAll(/data-t(?:-aria|-ph)?="([A-Za-z0-9_]+)"/g)) {
        prefixes.add(m[1]);
    }

    const dead = Object.keys(mod.STRINGS).filter((key) => {
        if (code.includes("'" + key + "'") || code.includes('"' + key + '"') || html.includes('"' + key + '"')) {
            return false;
        }

        return ![...prefixes].some((p) => key === p || (p.length >= 3 && key.startsWith(p)));
    });
    assert.deepEqual(dead, []);
});

// Pairing v2 (docs/ui-spec-pairing-v2.md rule 7 and section 12): no hyphen, en dash, em dash or arrow in any sentence of the pair and share flows.
test('pairing v2 strings have no dashes or arrows (placeholders and the literal shoal1 aside)', () => {
    const bad = [];
    for (const [key, value] of Object.entries(mod.STRINGS)) {
        if (!key.startsWith('pv2')) {
            continue;
        }

        for (const text of Array.isArray(value) ? value : [value]) {
            if (/[-\u2013\u2014]|>|\u2192/.test(text.replace(/\{\w+\}/g, ''))) {
                bad.push(key);
            }
        }
    }

    assert.deepEqual(bad, []);
});

test('pairing v2 strings are all present for the steps the spec names', () => {
    for (const key of ['pv2WhoYou', 'pv2WhoFriend', 'pv2WhoWait', 'pv2CodesTitle', 'pv2Under3', 'pv2InviteTitle', 'pv2CodeTitle', 'installTitle', 'pv2ShReachTitle', 'pv2ShSendTitle']) {
        assert.equal(typeof mod.STRINGS[key], 'string', key);
    }
});

test('the friend steps (fs_) have no dash, arrow, or numbered cross reference either (Lumen 03 M3)', () => {
    const bad = [];
    for (const [key, value] of Object.entries(mod.STRINGS)) {
        if (key.startsWith('fs_') && /[\u2013\u2014>]|\d-(digit|character)|shoal-friend|in step \d|from step \d/.test(String(value).replace(/\{\w+\}/g, ''))) {
            bad.push(key);
        }
    }

    assert.deepEqual(bad, []);
});
