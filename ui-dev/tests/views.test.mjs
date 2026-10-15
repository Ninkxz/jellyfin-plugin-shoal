// Lumen M3: no screen ever prints "null", "undefined", "NaN" or an unfilled {placeholder}, in any view of any scenario.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, makeClock, JUNK } from './helpers.mjs';

const VIEWS = [
    ['overview', {}], ['libraries', {}], ['settings', {}], ['pair', { dir: 'watch' }], ['pair', { dir: 'share' }],
    ['server', { id: 'h-hops' }], ['server', { id: 'h-fern' }], ['server', { id: 'h-gran' }], ['friend', { id: 'f-sam' }], ['friend', { id: 'f-jo' }], ['overlap', { id: 'h-hops' }]
];
const SCENARIOS = ['default', 'empty', 'friendsOnly', 'problems', 'keys', 'restored', 'recovery', 'recoveryNewer', 'oneServer', 'nothingShared'];

for (const scenario of SCENARIOS) {
    test('no junk text in any view of the ' + scenario + ' scenario', async () => {
        for (const [view, extra] of VIEWS) {
            const p = await mountPage({ scenario, params: Object.assign({ view }, extra), settleMs: 250, features: { appsWork: true } });
            try {
                assert.doesNotMatch(p.text(), JUNK, scenario + ' / ' + view + ' ' + JSON.stringify(extra));
            } finally {
                p.close();
            }
        }
    });
}

for (const [scenario, ids] of [['problems', ['h-hops', 'h-fern', 'h-gran', 'h-pine', 'h-ivy', 'h-cedar', 'h-maple', 'h-new', 'h-stop', 'h-alder', 'h-elm']]]) {
    test('no junk text on every server and friend of the ' + scenario + ' scenario', async () => {
        for (const id of ids) {
            const p = await mountPage({ scenario, params: { view: 'server', id }, settleMs: 250 });
            try {
                assert.doesNotMatch(p.text(), JUNK, 'server ' + id);
            } finally {
                p.close();
            }
        }

        for (const id of ['f-sam', 'f-jo', 'f-lee']) {
            const p = await mountPage({ scenario, params: { view: 'friend', id }, settleMs: 250 });
            try {
                assert.doesNotMatch(p.text(), JUNK, 'friend ' + id);
            } finally {
                p.close();
            }
        }
    });
}

// Warden W5-1: the real server writes absent values as null, not as missing. Every view must look the same either way.
test('null instead of absent changes nothing on any view', async () => {
    const same = async (view, extra, scenario) => {
        const params = Object.assign({ view }, extra);
        // One frozen clock for both runs: with the real one the two mounts straddle a minute boundary now and then (Offline from 5:36 vs 5:37).
        const clock = makeClock(Date.UTC(2026, 9, 15, 12, 0, 0));
        const a = await mountPage({ scenario, params, settleMs: 500, now: clock.now });
        const b = await mountPage({ scenario, params, settleMs: 500, nulls: true, now: clock.now });
        try {
            assert.doesNotMatch(b.text(), JUNK, 'null run: ' + view + ' ' + scenario);
            assert.equal(b.text(), a.text(), view + ' ' + JSON.stringify(extra) + ' ' + scenario);
        } finally {
            a.close();
            b.close();
        }
    };

    for (const scenario of ['default', 'problems']) {
        for (const [view, extra] of VIEWS) {
            await same(view, extra, scenario);
        }
    }

    for (const id of ['h-cedar', 'h-pine', 'h-maple', 'h-stop', 'h-alder', 'h-elm']) {
        await same('server', { id }, 'problems');
    }
});
