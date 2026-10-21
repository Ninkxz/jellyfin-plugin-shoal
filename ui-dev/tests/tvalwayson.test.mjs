// Owner, v2.9.1: TV shows from friends are a key feature, not a toggle. No screen offers a TV switch or tells the admin to turn TV on.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, loadShoal } from './helpers.mjs';

const ENABLE_TV = /turn (them|TV|TV shows)[^.]* on|TV shows are turned off|Show TV series from friends|enable TV|TV is off|Turn on to let Shoal sync TV/i;

test('no copy string offers a TV switch or tells the admin to enable TV', async () => {
    const { STRINGS } = await loadShoal();
    for (const [key, value] of Object.entries(STRINGS)) {
        for (const text of [].concat(value)) {
            assert.doesNotMatch(String(text), ENABLE_TV, key);
        }
    }

    assert.equal(STRINGS.fTvShows, undefined);
    assert.equal(STRINGS.fTvShowsNote, undefined);
    assert.equal(STRINGS.libTvOff, undefined);
});

test('Settings, Libraries & access, Library layouts (presets, Combine) and the pair screens never mention enabling TV', async () => {
    const views = [
        [{ view: 'settings' }, {}], [{ view: 'libraries' }, {}], [{ view: 'layouts' }, { combine: 'noTv' }], [{ view: 'pair', dir: 'watch' }, {}], [{ view: 'pair', dir: 'share' }, {}],
        [{ view: 'server', id: 'h-hops' }, {}]
    ];
    for (const [params, extra] of views) {
        const p = await mountPage(Object.assign({ scenario: 'default', params, settleMs: 350 }, extra));
        try {
            if (params.view === 'layouts') {
                const card = [...p.page.querySelectorAll('.shoal-preset')].find((c) => c.querySelector('h3 > span').textContent === 'Combine into my libraries');
                [...card.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Preview').click();
                await p.wait(100);
            }

            assert.doesNotMatch(p.text(), ENABLE_TV, JSON.stringify(params));
            if (params.view === 'settings') {
                assert.equal([...p.page.querySelectorAll('label.shoal-check')].some((l) => /TV series from friends/.test(l.textContent)), false, 'no TV toggle');
            }
        } finally {
            p.close();
        }
    }
});
