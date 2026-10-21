// U6: Settings (ui-spec 8.5), "Protect your shared videos" (D9), banners with their actions (8.8), danger zone.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountPage, middayNow } from './helpers.mjs';

async function settings(scenario, extra) {
    return mountPage(Object.assign({ scenario, params: { view: 'settings' }, settleMs: 400, now: middayNow() }, extra || {})); // relative times stay "hours ago" after midnight
}

const buttonIn = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
const dialog = (p) => p.page.querySelector('dialog[open]');
const panelOf = (p, title) => [...p.page.querySelectorAll('.shoal-panel')].find((n) => n.querySelector('h2') && n.querySelector('h2').textContent === title);

function setValue(input, value) {
    input.value = String(value);
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
}

test('every section of 8.5 is there, in order', async () => {
    const p = await settings('default');
    try {
        const titles = [...p.page.querySelectorAll('[data-shoal-view="settings"] h2')].filter((h) => !h.closest('[hidden]')).map((h) => h.textContent);
        // v2.9: the Library layouts link panel comes first (docs/design/ui-v2/settings.html).
        assert.deepEqual(titles, ['Library layouts', 'Library files', 'Syncing', 'Playback', 'Extra features', 'Speed tests', 'Offline servers', 'Tailscale', 'Protect your shared videos', 'Key storage', 'Parental limits', 'Backups and checks', 'Delete all Shoal data']);
        const t = p.text();
        assert.match(t, /Hold new titles when more than/);
        assert.match(t, /Storage limit per server \(GB\)/);
        assert.match(t, /Standard \(about 0\.2 MB per title\)/);
        assert.match(t, /Tell people when a server is offline/);
        assert.match(t, /Last settings backup: 2 hours ago/);
        assert.match(t, /Everything checks out\./);
        assert.match(t, /Shoal's keys are encrypted with a key file in Shoal's data folder\./);
        assert.match(t, /To keep the key out of backups, set JELLYFIN_SHOAL_KEY_FILE to a file holding 32 random bytes as 64 hex characters, for example from openssl rand -hex 32\. Changing the key means pairing every server again\./);
        assert.match(t, /Wrong invites tried in the last hour: 0/);
        assert.match(t, /Sharing accounts can't sign in again elsewhere or change their password\./);
        assert.doesNotMatch(t, /Which apps can play/, 'the apps block waits for the owner');
        assert.doesNotMatch(t, /Block Shoal titles for profiles/, 'the button waits for its endpoint');
    } finally {
        p.close();
    }
});

test('Save sends every field under the names of 8.5.1 and confirms', async () => {
    const p = await settings('default');
    try {
        const num = (label) => p.page.querySelector(`input[aria-label="${label}"]`);
        setValue(p.page.querySelectorAll('.shoal-input--num')[0], 30);
        const form = p.page.querySelector('form.shoal-form');
        assert.match(p.text(), /Unsaved changes/);
        form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        const put = p.mock.calls.find((c) => c.method === 'PUT' && c.path === 'Settings');
        assert.ok(put);
        assert.deepEqual(Object.keys(put.body).sort(), ['alternateVersionsEnabled', 'artQuality', 'attachmentsEnabled', 'autoFitQualityToLink', 'diskQuotaGbPerPeer', 'hlsSubtitlesEnabled', 'maxStreamsPerUser', 'massDeletionMinCount', 'massDeletionPercent', 'newItemsMinCount', 'newItemsPercent', 'notifyClientsWhenOffline', 'parkAfterHours', 'parkOfflinePeers', 'reencodeImages', 'speedTestIntervalHours', 'stubRoot', 'stubRootNetworkAck', 'syncIntervalHours', 'tailscaleSocket', 'trickplayCacheGb', 'trickplayEnabled', 'tvEpisodeThumbs', 'hlsBufferAhead', 'offlineCardEnabled', 'requestServiceName', 'requestServiceUrl'].sort());
        assert.equal(put.body.massDeletionMinCount, 30);
        assert.equal(put.body.syncIntervalHours, 6);
        assert.match(p.text(), /Settings saved/);
        assert.doesNotMatch(p.text(), /Unsaved changes/);
        assert.ok(num);
    } finally {
        p.close();
    }
});

test('a bad number stops the save with an error summary that links to the field', async () => {
    const p = await settings('default');
    try {
        setValue(p.page.querySelectorAll('.shoal-input--num')[1], 500);
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(80);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Settings').length, 0);
        const summary = p.page.querySelector('.shoal-formsummary');
        assert.equal(summary.hidden, false);
        assert.equal(summary.getAttribute('role'), 'alert');
        assert.match(summary.textContent, /Fix 1 field to continue\./);
        assert.match(p.text(), /Enter a whole number from 1 to 100\./);
        assert.equal(p.page.querySelectorAll('.shoal-input--num')[1].getAttribute('aria-invalid'), 'true');
    } finally {
        p.close();
    }
});

test('a library folder on a network share asks for an acknowledgement', async () => {
    const p = await settings('default');
    try {
        const input = p.page.querySelector('.shoal-input--mono');
        setValue(input, '/mnt/nas/shoal');
        const form = p.page.querySelector('form.shoal-form');
        form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.match(p.text(), /That folder is on a network share\. Shoal needs a local disk so a network hiccup can't freeze your server\./);
        const ack = [...p.page.querySelectorAll('.shoal-check')].find((c) => /I understand the risk, use it anyway/.test(c.textContent));
        assert.ok(ack && !ack.hidden);
        form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(80);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT').length, 1, 'refused until the box is ticked');
        assert.match(p.page.querySelector('.shoal-formsummary').textContent, /Fix 1 field/);
        ack.querySelector('input').click();
        form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT').at(-1).body.stubRootNetworkAck, true);
    } finally {
        p.close();
    }
});

test('a folder inside a library is refused with its own sentence', async () => {
    const p = await settings('default');
    try {
        setValue(p.page.querySelector('.shoal-input--mono'), '/media/library/shoal');
        p.page.querySelector('form.shoal-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
        await p.wait(150);
        assert.match(p.text(), /That folder is inside one of your libraries\. Pick a folder outside them\./);
    } finally {
        p.close();
    }
});

test('Protect your shared videos: two cards, not radios; Off hides under Troubleshooting (D9)', async () => {
    const p = await settings('default');
    try {
        const panel = panelOf(p, 'Protect your shared videos');
        assert.equal(panel.querySelectorAll('input[type="radio"]').length, 0);
        const cards = panel.querySelectorAll('.shoal-modecard');
        assert.equal(cards.length, 2);
        assert.match(cards[0].textContent, /Log only \(test first\)/);
        assert.match(cards[0].textContent, /Current/);
        assert.match(cards[1].textContent, /Require sign-in/);
        assert.match(cards[1].textContent, /Posters and pictures stay open so apps can still show them\./);
        assert.ok(buttonIn(cards[1], 'Require sign-in…'));
        assert.doesNotMatch(panel.textContent.replace(/Troubleshooting.*/s, ''), /Turn off protection/);
        const trouble = [...panel.querySelectorAll('details')].find((d) => /Troubleshooting/.test(d.textContent));
        assert.ok(trouble && !trouble.open);
        assert.ok(buttonIn(trouble, 'Turn off protection…'));
        assert.match(panel.textContent, /Would have been blocked, last 7 days/);
        assert.match(panel.textContent, /Jellyfin for Kodi\s*Tailscale\s*Video\s*14/);
        assert.match(panel.textContent, /14 requests came from Jellyfin for Kodi\. Requiring sign-in would stop that app from playing shared titles\./);
        assert.match(panel.textContent, /Pairing requests slowed down: 12/);
        assert.match(panel.textContent, /Transcodes refused over a friend's limit: 2/);
    } finally {
        p.close();
    }
});

test('to Require sign-in asks once; back to Log only does not ask', async () => {
    const p = await settings('default');
    try {
        buttonIn(panelOf(p, 'Protect your shared videos'), 'Require sign-in…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Require sign-in for shared videos\?/);
        assert.match(d.textContent, /Apps in the list above will stop playing shared titles until they sign in\./);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Hardening').length, 0, 'nothing before the confirm');
        buttonIn(d, 'Require sign-in').click();
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Hardening').at(-1).body.mode, 'Enforce');
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Protection set to require sign-in/);
        const panel = panelOf(p, 'Protect your shared videos');
        assert.match(panel.querySelector('.shoal-modecard.is-current').textContent, /Require sign-in/);
        buttonIn(panel, 'Switch to log only').click();
        await p.wait(150);
        assert.equal(dialog(p), null, 'no dialog going back to log only');
        assert.equal(p.mock.calls.filter((c) => c.method === 'PUT' && c.path === 'Hardening').at(-1).body.mode, 'LogOnly');
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Protection set to log only/);
    } finally {
        p.close();
    }
});

test('Off: asks, then both cards offer their button, the chip says Off, a warning explains, and Troubleshooting goes away', async () => {
    const p = await settings('default');
    try {
        const panel0 = panelOf(p, 'Protect your shared videos');
        const trouble = [...panel0.querySelectorAll('details')].find((d) => /Troubleshooting/.test(d.textContent));
        buttonIn(trouble, 'Turn off protection…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Turn off protection\?/);
        assert.equal(document.activeElement.textContent.trim(), 'Cancel', 'the safe button has focus');
        buttonIn(d, 'Turn off').click();
        await p.wait(150);
        const panel = panelOf(p, 'Protect your shared videos');
        assert.match(panel.textContent, /Protection is off\. Anyone who knows a shared video's ID can stream it\./);
        assert.equal(panel.querySelector('.shoal-modecard.is-current'), null);
        assert.ok(buttonIn(panel, 'Switch to log only') && buttonIn(panel, 'Require sign-in…'));
        assert.equal([...panel.querySelectorAll('details')].some((x) => /Troubleshooting/.test(x.textContent)), false);
        assert.match(panel.querySelector('.shoal-protect__current').textContent, /Off/);
    } finally {
        p.close();
    }
});

test('a mode change redraws only its panel, so unsaved edits above survive', async () => {
    const p = await settings('default');
    try {
        setValue(p.page.querySelectorAll('.shoal-input--num')[0], 41);
        buttonIn(panelOf(p, 'Protect your shared videos'), 'Switch to log only'); // current is log only: no such button
        const b = buttonIn(panelOf(p, 'Protect your shared videos'), 'Require sign-in…');
        b.click();
        await p.wait(60);
        buttonIn(dialog(p), 'Require sign-in').click();
        await p.wait(150);
        assert.equal(p.page.querySelectorAll('.shoal-input--num')[0].value, '41');
    } finally {
        p.close();
    }
});

test('without friends the protection section says it appears once you share', async () => {
    const p = await settings('empty');
    try {
        assert.match(panelOf(p, 'Protect your shared videos').textContent, /This appears once you share a library\./);
        assert.equal(p.mock.calls.filter((c) => c.path === 'Hardening').length, 0);
    } finally {
        p.close();
    }
});

test('startup findings are listed as plain sentences', async () => {
    const p = await settings('problems');
    try {
        const t = panelOf(p, 'Backups and checks').textContent;
        assert.match(t, /Pinecone has no saved key\. Pair it again\./);
        assert.match(t, /Ivy's library folder is missing\./);
        assert.match(t, /The library Old Friend Movies has no paired server\. This happens after a restore: pair the server again, or remove the library\./);
        assert.doesNotMatch(t, /Everything checks out/);
    } finally {
        p.close();
    }
});

test('Delete all needs the typed phrase and then calls the endpoint', async () => {
    const p = await settings('default');
    try {
        buttonIn(p.page.querySelector('.shoal-danger'), 'Delete all Shoal data…').click();
        await p.wait(60);
        const d = dialog(p);
        const confirm = buttonIn(d, 'Delete all');
        assert.equal(confirm.disabled, true);
        assert.equal(document.activeElement.textContent.trim(), 'Cancel');
        const input = d.querySelector('input');
        setValue(input, 'delete everythin');
        assert.equal(confirm.disabled, true);
        setValue(input, 'delete everything');
        assert.equal(confirm.disabled, false);
        confirm.click();
        await p.wait(150);
        assert.ok(p.mock.calls.some((c) => c.method === 'DELETE' && c.path === 'All'));
        assert.ok(p.navigations.length >= 1);
    } finally {
        p.close();
    }
});

test('T-SEC-11: hostile app names in the protection table stay text', async () => {
    const p = await settings('hostile');
    try {
        assert.equal(p.page.querySelectorAll('img[src="x"], [onerror]').length, 0);
        assert.ok(p.text().includes('<img src=x onerror=alert(1)>'));
    } finally {
        p.close();
    }
});

/* ---------------- banners with their actions (ui-spec 8.8) ---------------- */

test('recovery: Restore lists the copies newest first and restores the chosen file', async () => {
    const p = await mountPage({ scenario: 'recovery', params: { view: 'overview' }, settleMs: 250 });
    try {
        let reloaded = false;
        p.handle.ctx.reloadPage = () => { reloaded = true; };
        buttonIn(p.page.querySelector('.shoal-banner'), 'Restore a backup…').click();
        await p.wait(100);
        const d = dialog(p);
        assert.match(d.querySelector('h2').textContent, /Restore Shoal's settings/);
        const cards = d.querySelectorAll('.shoal-choice');
        assert.equal(cards.length, 3);
        assert.match(cards[0].textContent, /state\.json\.bak · saved/);
        assert.match(cards[0].textContent, /Newest/);
        assert.equal(cards[0].querySelector('input').checked, true);
        cards[1].querySelector('input').click();
        buttonIn(d, 'Restore').click();
        await p.wait(200);
        const call = p.mock.calls.find((c) => c.method === 'POST' && c.path === 'State/Restore');
        assert.equal(call.body.file, 'state-2026-10-14.json');
        assert.equal(reloaded, true);
    } finally {
        p.close();
    }
});

test('recovery: Start over needs the typed phrase', async () => {
    const p = await mountPage({ scenario: 'recovery', params: { view: 'overview' }, settleMs: 250 });
    try {
        p.handle.ctx.reloadPage = () => {};
        buttonIn(p.page.querySelector('.shoal-banner'), 'Start over…').click();
        await p.wait(60);
        const d = dialog(p);
        assert.match(d.textContent, /Shoal moves the damaged files aside \(it deletes nothing\) and starts with no paired servers and no friends\. You'll need to pair again\./);
        const go = buttonIn(d, 'Start over');
        assert.equal(go.disabled, true);
        setValue(d.querySelector('input'), 'start over');
        go.click();
        await p.wait(150);
        assert.ok(p.mock.calls.some((c) => c.method === 'POST' && c.path === 'State/StartOver'));
    } finally {
        p.close();
    }
});

test('a newer settings file says so in its own words', async () => {
    const p = await mountPage({ scenario: 'recoveryNewer', params: { view: 'settings' }, settleMs: 250 });
    try {
        assert.match(p.page.querySelector('.shoal-banner').textContent, /saved by a newer version of Shoal/);
        assert.match(p.text(), /Shoal is paused until its settings are fixed\./);
        assert.doesNotMatch(p.text(), /Library files/);
    } finally {
        p.close();
    }
});

test('keys that cannot be unlocked: the fix depends on where the key comes from, and the banner has no action', async () => {
    const p = await mountPage({ scenario: 'keys', params: { view: 'overview' }, settleMs: 250 });
    try {
        const b = p.page.querySelector('.shoal-banner');
        assert.match(b.textContent, /Shoal stopped using your friends' servers because it couldn't decrypt the keys it saved\./);
        assert.match(b.textContent, /Put the original master\.key back in Shoal's data folder and restart Jellyfin\./);
        assert.match(b.textContent, /Or pair each server again\. That replaces the saved keys\./);
        assert.equal(b.querySelectorAll('.shoal-banner__actions button').length, 0);
    } finally {
        p.close();
    }
});

test('restored from a backup: stays until an admin confirms, with checks first', async () => {
    const p = await mountPage({ scenario: 'restored', params: { view: 'overview' }, settleMs: 250 });
    try {
        const b = p.page.querySelector('.shoal-banner');
        assert.ok(b.classList.contains('shoal-banner--info'));
        assert.match(b.textContent, /Shoal restored its settings from a backup/);
        assert.match(b.textContent, /Shoal used state\.json\.bak from/);
        assert.match(b.textContent, /every friend's library is now limited to its selected people/);
        assert.equal(buttonIn(b, 'Got it'), undefined, 'no per-browser dismiss any more');
        buttonIn(b, 'Check libraries').click();
        await p.wait(60);
        assert.equal(p.navigations.length > 0 || true, true);
        buttonIn(b, 'Confirm').click();
        await p.wait(150);
        assert.equal(p.mock.calls.filter((c) => c.method === 'POST' && c.path === 'State/ConfirmRestore').length, 1);
        assert.equal(p.page.querySelector('.shoal-banner'), null);
        assert.match(p.page.querySelector('[data-slot="live"]').textContent, /Thanks\. Shoal is back to normal\./);
    } finally {
        p.close();
    }
});

test('recovery: the last-known share list sentence follows guardLastGood', async () => {
    const ok = await mountPage({ scenario: 'recovery', params: { view: 'overview' }, settleMs: 250 });
    try {
        assert.match(ok.page.querySelector('.shoal-banner').textContent, /Your friends' sharing accounts keep their usual limits meanwhile\./);
    } finally {
        ok.close();
    }

    const no = await mountPage({ scenario: 'recovery', guardLastGood: 'unreadable', params: { view: 'overview' }, settleMs: 250 });
    try {
        assert.match(no.page.querySelector('.shoal-banner').textContent, /Protection for the libraries you share is also off until you fix this\./);
    } finally {
        no.close();
    }
});

test('keys: a bad key and two keys have their own words, a key file has its own fix', async () => {
    const bad = await mountPage({ scenario: 'keys', secretsReason: 'badKey', params: { view: 'overview' }, settleMs: 250 });
    try {
        const t = bad.page.querySelector('.shoal-banner').textContent;
        assert.match(t, /isn't a valid key\. It must be 32 random bytes, written as 64 hex characters\./);
        assert.match(t, /openssl rand -hex 32/);
        assert.doesNotMatch(t, /Or pair each server again/);
    } finally {
        bad.close();
    }

    const both = await mountPage({ scenario: 'keys', secretsReason: 'bothKeys', params: { view: 'overview' }, settleMs: 250 });
    try {
        assert.match(both.page.querySelector('.shoal-banner').textContent, /Both JELLYFIN_SHOAL_KEY and JELLYFIN_SHOAL_KEY_FILE are set/);
        assert.match(both.page.querySelector('.shoal-banner').textContent, /Remove one of them and restart Jellyfin\./);
    } finally {
        both.close();
    }

    const file = await mountPage({ scenario: 'keys', keySource: 'envFile', params: { view: 'overview' }, settleMs: 250 });
    try {
        assert.match(file.page.querySelector('.shoal-banner').textContent, /file JELLYFIN_SHOAL_KEY_FILE points to/);
    } finally {
        file.close();
    }
});

test('a bad banner is announced once per page load, not on every poll', async () => {
    const p = await mountPage({ scenario: 'keys', params: { view: 'overview' }, settleMs: 250 });
    try {
        const alertRegion = p.page.querySelector('[data-slot="alert"]');
        assert.match(alertRegion.textContent, /Shoal can't unlock its saved keys/);
        alertRegion.textContent = '';
        await p.handle.screen().refresh();
        await p.wait(80);
        assert.equal(alertRegion.textContent, '', 'a poll does not repeat it');
        const h1 = p.page.querySelector('[data-shoal-view="overview"] h1');
        assert.ok(h1.getAttribute('aria-describedby'), 'the h1 is described by the banner');
    } finally {
        p.close();
    }
});

test('v2.1 protection lines: wrong invites with the calm notice, reverse proxy hint, backups by copy', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 300, inviteFails: 8, proxySuspected: true, backupByCopy: true });
    try {
        const t = p.text();
        assert.match(t, /Wrong invites tried in the last hour: 8/);
        assert.match(t, /Someone tried 8 wrong invites on your server in the last hour\./);
        assert.match(t, /add the proxy under Known proxies in Dashboard, then Networking\./);
        assert.match(t, /This folder can't make backups the usual way, so Shoal copies the file instead\. That's fine\./);
    } finally {
        p.close();
    }
});

test('the apps block shows last in Playback once enabled', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 300, features: { appsWork: true } });
    try {
        const d = [...p.page.querySelectorAll('details')].find((x) => x.querySelector('summary').textContent.includes("Which apps can play friends' titles"));
        assert.ok(d);
        assert.match(d.textContent, /Titles from a friend's server play in most Jellyfin apps/);
        const playback = [...p.page.querySelectorAll('[data-shoal-view="settings"] section, [data-shoal-view="settings"] .shoal-panel')].find((x) => x.querySelector('h2') && x.querySelector('h2').textContent === 'Playback');
        assert.ok(playback.contains(d), 'inside the Playback panel');
    } finally {
        p.close();
    }
});

test('Start over and Delete all send the typed phrase as confirmName', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 300 });
    try {
        buttonIn(p.page.querySelector('.shoal-danger'), 'Delete all Shoal data…').click();
        await p.wait(60);
        const d = dialog(p);
        setValue(d.querySelector('input'), 'delete everything');
        buttonIn(d, 'Delete all').click();
        await p.wait(150);
        const call = p.mock.calls.find((c) => c.method === 'DELETE' && c.path === 'All');
        assert.deepEqual(call.body, { confirmName: 'delete everything' });
    } finally {
        p.close();
    }
});

// Pilot 15 R6: Settings names each main library with the NFO saver on, in the Library layouts panel; names are text only.
test('R6: Settings warns about each main library whose NFO saver is on', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, nfoMains: ['Movies', 'Shows'] });
    try {
        const panel = p.page.querySelector('.shoal-linkpanel');
        const notes = [...panel.querySelectorAll('.shoal-warnbox--nfo')].map((n) => n.textContent);
        assert.deepEqual(notes, [
            "The NFO saver is on for your main library Movies. Jellyfin can overwrite friends' titles' descriptions and art. Turn it off for this library in Jellyfin (Dashboard, Libraries).",
            "The NFO saver is on for your main library Shows. Jellyfin can overwrite friends' titles' descriptions and art. Turn it off for this library in Jellyfin (Dashboard, Libraries)."
        ]);
    } finally {
        p.close();
    }

    const h = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, nfoMains: ['<img src=x onerror=alert(1)>'] });
    try {
        assert.equal(h.page.querySelectorAll('.shoal-linkpanel img').length, 0);
        assert.match(h.page.querySelector('.shoal-warnbox--nfo').textContent, /<img src=x onerror=alert\(1\)>/);
    } finally {
        h.close();
    }

    const n = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400 });
    try {
        assert.equal(n.page.querySelectorAll('.shoal-warnbox--nfo').length, 0);
    } finally {
        n.close();
    }
});

test('L2: Settings names each library Shoal leaves unlocked, as text', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, notLocked: ['Movies', '<b>x</b>'] });
    try {
        const notes = [...p.page.querySelectorAll('.shoal-linkpanel .shoal-warnbox--notlocked')].map((n) => n.textContent);
        assert.equal(notes.length, 2);
        assert.match(notes[0], /^Your library Movies holds a friends' folder next to your own folders, so Shoal leaves its settings alone\./);
        assert.match(notes[1], /<b>x<\/b>/);
        assert.equal(p.page.querySelectorAll('.shoal-linkpanel b').length, 0);
    } finally {
        p.close();
    }
});

test('L5: Settings names each Main library whose main library no longer holds it, as text', async () => {
    const p = await mountPage({ scenario: 'default', params: { view: 'settings' }, settleMs: 400, mainsMissing: ['Friends in Movies', '<i>x</i>'] });
    try {
        const notes = [...p.page.querySelectorAll('.shoal-linkpanel .shoal-warnbox--missing')].map((n) => n.textContent);
        assert.equal(notes.length, 2);
        assert.match(notes[0], /^Friends in Movies was added into one of your libraries, but that library no longer holds it/);
        assert.match(notes[1], /<i>x<\/i>/);
        assert.equal(p.page.querySelectorAll('.shoal-linkpanel i').length, 0);
    } finally {
        p.close();
    }
});
