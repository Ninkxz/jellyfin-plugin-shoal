/*
 * In-memory stand-in for Shoal/Admin/* (DESIGN 9.6, ui-spec 13), used by the development harness and
 * the render tests. It is the only place that knows the mock data; shoal.js has no alternate source.
 *
 * Shapes follow ui-spec 13 with the v2 names (serverIdPrefix, pairing session, access/drift, ...).
 * Shapes DESIGN does not define yet are marked "proposed" in docs/kiro2-02-ui.md.
 *
 * Plain ES module, no Node or browser APIs, so jsdom tests and the browser harness share it.
 */

export const SCENARIOS = [
    'default', 'empty', 'friendsOnly', 'problems', 'recovery', 'recoveryNewer', 'keys', 'restored',
    'hostile', 'error', 'slow', 'oneServer', 'nothingShared'
];

export const XSS = '<img src=x onerror=alert(1)>';

const MIN = 60000;
const HOUR = 3600000;
const DAY = 86400000;

function iso(ms) {
    return new Date(ms).toISOString();
}

/** Small deterministic generator so charts look the same on every load. */
function rng(seed) {
    let s = seed >>> 0 || 1;
    return () => {
        s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
        return s / 4294967296;
    };
}

function clone(v) {
    return v === undefined ? undefined : JSON.parse(JSON.stringify(v));
}

function fail(status, error, headers) {
    return { status, body: error ? { error } : {}, headers: headers || {} };
}

function ok(body, status) {
    return { status: status || 200, body: body === undefined ? {} : body, headers: {} };
}

const NEEDED = { remux4k: 70, remux1080: 35, '4k': 30, '1080p': 15, '1080pTranscode': 8, '720p': 5 };

function ladderFor(mbps, transcodeOk) {
    return Object.entries(NEEDED).map(([cls, needed]) => {
        let verdict;
        if (mbps >= needed * 1.5) {
            verdict = 'smooth';
        } else if (mbps >= needed) {
            verdict = 'mayBuffer';
        } else if (transcodeOk && cls !== '720p' && cls !== '1080pTranscode' && mbps >= 8) {
            verdict = 'needsTranscode';
        } else {
            verdict = 'willBuffer';
        }

        return { cls, verdict, neededMbps: needed };
    });
}

function verdictSummary(mbps) {
    const l = ladderFor(mbps, true);
    const by = Object.fromEntries(l.map((r) => [r.cls, r.verdict]));
    return [
        { cls: '1080p', verdict: by['1080p'] },
        { cls: '4k', verdict: by['4k'] },
        { cls: 'remux', verdict: by.remux4k }
    ];
}

export function createMock(options) {
    const opt = Object.assign({ scenario: 'default', now: () => Date.now(), delayMs: 0, pairing: 'ok', probeError: '' }, options || {});
    const now = () => opt.now();
    const calls = [];
    const hostile = opt.scenario === 'hostile';
    const X = (label) => (hostile ? XSS : label);

    /* ---------------- local users ---------------- */
    const users = [
        { id: 'u-rich', name: hostile ? XSS : 'Rich', imageTag: 'a1', isAdmin: true, isDisabled: false, allLibraries: true, managedByShoal: false, restricted: false },
        { id: 'u-sam', name: hostile ? XSS + ' sam' : 'Sam', imageTag: null, isAdmin: false, isDisabled: false, allLibraries: true, managedByShoal: false, restricted: false },
        { id: 'u-alex', name: 'Alex', imageTag: null, isAdmin: false, isDisabled: false, allLibraries: false, managedByShoal: false, restricted: false },
        { id: 'u-kit', name: 'Kit', imageTag: null, isAdmin: false, isDisabled: false, allLibraries: false, managedByShoal: true, restricted: true },
        { id: 'u-mum', name: 'Mum', imageTag: null, isAdmin: false, isDisabled: false, allLibraries: true, managedByShoal: false, restricted: false },
        { id: 'u-jo', name: 'Jo', imageTag: null, isAdmin: true, isDisabled: true, allLibraries: true, managedByShoal: false, restricted: false },
        { id: 'u-share-sam', name: 'shoal-sam', imageTag: null, isAdmin: false, isDisabled: false, allLibraries: false, managedByShoal: false, restricted: false, isShareUser: true }
    ];

    // opt.guidIds: local account ids as the real server writes them (GUIDs), to prove none reaches the page.
    const GUID_IDS = { 'u-rich': '6f1c2a90-1b2c-4d3e-8f40-0a1b2c3d4e5f', 'u-sam': '7a2d3b01-2c3d-4e4f-9051-1b2c3d4e5f60', 'u-alex': '8b3e4c12-3d4e-4f50-a162-2c3d4e5f6071', 'u-kit': '9c4f5d23-4e5f-4061-b273-3d4e5f607182', 'u-mum': 'ad506e34-5f60-4172-8384-4e5f60718293', 'u-jo': 'be617f45-6071-4283-9495-5f60718293a4', 'u-share-sam': 'cf728056-7182-4394-a5a6-60718293a4b5' };
    const gid = (id) => (opt.guidIds && GUID_IDS[id]) || id;
    if (opt.guidIds) {
        users.forEach((u) => { u.id = gid(u.id); });
    }

    const self = { homeName: 'Rich’s Jellyfin', currentUserId: gid('u-rich') };

    /* ---------------- servers ---------------- */
    const t0 = now();
    const mkLatest = (key, title, year) => ({ localItemId: 'item-' + key, title: X(title), year, backdropTag: 'bd-' + key, primaryTag: 'pr-' + key });

    const servers = new Map();
    const detail = new Map();
    // lastSyncSkipReason: a Sync now after the last real sync did not run (Pilot 24 SKIPPED-INVISIBLE); the real time and result stay.
    const skipped = (ls) => opt.lastSyncSkipReason ? Object.assign(ls, { skippedUtc: iso(t0 - 1 * MIN), skipReason: opt.lastSyncSkipReason }) : ls;

    function addServer(def) {
        const s = Object.assign({
            proxy: false, pairingError: null, paired: true, path: 'direct', derpRegion: null, rttMs: 38, mbps: 42, jitterMs: 4, lossPct: 0,
            offlineSinceUtc: null, nextCheckUtc: null, syncing: false, titleCount: 0, alertCount: 0, heldRemovals: 0, heldAdditions: 0,
            activeStreams: { n: 0, max: 4 }, hints: [], latest: null,
            lastSync: skipped({ atUtc: iso(t0 - 12 * MIN), result: 'ok', added: 3, removed: 0 })
        }, def);
        s.verdictSummary = def.verdictSummary || (s.mbps ? verdictSummary(s.mbps) : []);
        servers.set(s.handle, s);
        return s;
    }

    if (!['empty', 'friendsOnly'].includes(opt.scenario)) {
        addServer({
            handle: 'h-hops', displayName: X('Hops'), url: 'http://hops-jellyfin.tail1234.ts.net:8096', network: 'Tailnet', status: 'Online',
            path: 'direct', rttMs: 38, mbps: 42, titleCount: 1204, latest: mkLatest('hops-1', 'Sintel', 2010)
        });
        if (opt.scenario !== 'oneServer') {
            addServer({
                handle: 'h-fern', displayName: X('Fernwood'), url: 'https://fernwood.example.net', network: 'Public', status: 'Online',
                path: 'derp', derpRegion: 'Frankfurt', rttMs: 96, mbps: 17, jitterMs: 9, titleCount: 386, activeStreams: { n: 1, max: 4 },
                latest: mkLatest('fern-1', 'Tears of Steel', 2012),
                hints: [{ kind: 'relayed', severity: 'warn' }]
            });
            addServer({
                handle: 'h-gran', displayName: X('Granite'), url: 'http://192.168.1.40:8096', network: 'LAN', status: 'Offline',
                path: 'unknown', rttMs: null, mbps: null, titleCount: 612, offlineSinceUtc: iso(t0 - 2 * HOUR - 20 * MIN), nextCheckUtc: iso(t0 + 4 * MIN),
                latest: mkLatest('gran-1', 'Big Buck Bunny', 2008), lastSync: { atUtc: iso(t0 - 3 * HOUR), result: 'ok', added: 0, removed: 0 }
            });
        }
    }

    if (opt.scenario === 'nothingShared') {
        servers.forEach((s) => { s.titleCount = 0; s.latest = null; });
    }

    if (opt.scenario === 'problems') {
        servers.clear();
        addServer({ handle: 'h-hops', displayName: 'Hops', url: 'http://hops-jellyfin.tail1234.ts.net:8096', network: 'Tailnet', status: 'Online', path: 'direct', titleCount: 1204, syncing: true, latest: mkLatest('hops-1', 'Sintel', 2010), heldRemovals: 140, heldAdditions: 0, alertCount: 3 });
        addServer({ handle: 'h-fern', displayName: 'Fernwood', url: 'https://fernwood.example.net', network: 'Public', status: 'Degraded', path: 'derp?', rttMs: 140, mbps: 9, jitterMs: 62, lossPct: 4, titleCount: 386, latest: mkLatest('fern-1', 'Tears of Steel', 2012), heldAdditions: 600, alertCount: 1 });
        addServer({ handle: 'h-gran', displayName: 'Granite', url: 'http://192.168.1.40:8096', network: 'LAN', status: 'Offline', path: 'unknown', rttMs: null, mbps: null, titleCount: 612, offlineSinceUtc: iso(t0 - 2 * HOUR), nextCheckUtc: iso(t0 + 4 * MIN), latest: mkLatest('gran-1', 'Big Buck Bunny', 2008) });
        addServer({ handle: 'h-pine', displayName: 'Pinecone', url: 'https://pinecone.example.net', network: 'Public', status: 'AuthFailed', path: 'unknown', rttMs: null, mbps: null, titleCount: 90, latest: mkLatest('pine-1', 'Spring', 2019) });
        addServer({ handle: 'h-ivy', displayName: 'Ivy', url: 'http://ivy.tail1234.ts.net:8096', network: 'Tailnet', status: 'IdentityMismatch', path: 'unknown', rttMs: null, mbps: null, titleCount: 52, latest: mkLatest('ivy-1', 'Cosmos Laundromat', 2015) });
        addServer({ handle: 'h-cedar', displayName: 'Cedar', url: 'https://cedar.example.net', network: 'Public', status: 'TlsKeyChanged', path: 'unknown', rttMs: null, mbps: null, titleCount: 210, alertCount: 1, latest: mkLatest('cedar-1', 'Agent 327', 2017) });
        addServer({ handle: 'h-maple', displayName: 'Maple', url: 'http://maple.tail1234.ts.net:8096', network: 'Tailnet', status: 'Disabled', path: 'unknown', rttMs: null, mbps: null, titleCount: 77, latest: mkLatest('maple-1', 'Hero', 2018) });
        addServer({ handle: 'h-new', displayName: 'Willow', url: 'http://willow.tail1234.ts.net:8096', network: 'Tailnet', status: 'Pairing', path: 'unknown', rttMs: null, mbps: null, titleCount: 0 });
        addServer({ handle: 'h-stop', displayName: 'Birch', url: 'https://birch.example.net', network: 'Public', status: 'PairingAborted', pairingError: 'badPeerResponse', pairingMethod: 'invite', pairingStoppedUtc: iso(t0 - 20 * MIN), paired: false, path: 'unknown', rttMs: null, mbps: null, titleCount: 0 });
        addServer({ handle: 'h-alder', displayName: 'Alder', url: 'http://alder.tail1234.ts.net:8096', network: 'Tailnet', status: 'PairingAborted', pairingError: 'network', pairingMethod: 'invite', pairingStoppedUtc: iso(t0 - 3 * HOUR), paired: true, path: 'unknown', rttMs: null, mbps: null, titleCount: 340 });
        addServer({ handle: 'h-elm', displayName: 'Elm', url: 'https://elm.example.net', network: 'Public', status: 'PairingAborted', pairingError: 'interrupted', pairingMethod: 'invite', pairingStoppedUtc: iso(t0 - 50 * MIN), paired: false, path: 'unknown', rttMs: null, mbps: null, titleCount: 0 });
    }

    // A server paired the old way (D17, opt.legacy): paused, it needs a re-pair by invite.
    if (opt.dedup && servers.get('h-fern')) {
        Object.assign(servers.get('h-fern'), { hiddenByDedup: 41, dedupWinner: 'Hops' });
    }

    if (opt.legacyHttp) {
        addServer({ handle: 'h-legacyhttp', displayName: 'Plain Friend', url: 'http://plainfriend.example.net:8096', network: 'Public', status: 'Disabled', pairingMethod: 'qc', needsRepair: true, repairBlockedByHttp: true, path: 'unknown', rttMs: null, mbps: null, titleCount: 20 });
    }

    if (opt.legacy) {
        addServer({ handle: 'h-legacy', displayName: 'Old Friend', url: 'http://oldfriend.tail1234.ts.net:8096', network: 'Tailnet', status: 'Disabled', pairingMethod: 'qc', needsRepair: true, path: 'unknown', rttMs: null, mbps: null, titleCount: 50 });
    }

    const recentlyShared = [];
    const posterDefs = [['Sintel', 2010, 15, 'hops-1', 'h-hops', 'Hops'], ['Tears of Steel', 2012, 12, 'fern-1', 'h-fern', 'Fernwood'], ['Big Buck Bunny', 2008, 10, 'gran-1', 'h-gran', 'Granite'], ['Spring', 2019, 8, 'hops-2', 'h-hops', 'Hops'],
        ['Cosmos Laundromat', 2015, 12, 'hops-3', 'h-hops', 'Hops'], ['Agent 327', 2017, 4, 'fern-2', 'h-fern', 'Fernwood'], ['Hero', 2018, 5, 'hops-4', 'h-hops', 'Hops'], ['Caminandes', 2013, 6, 'gran-2', 'h-gran', 'Granite'],
        ['Glass Half', 2015, 3, 'fern-3', 'h-fern', 'Fernwood'], ['Charge', 2022, 4, 'hops-5', 'h-hops', 'Hops'], ['Coffee Run', 2020, 3, 'hops-6', 'h-hops', 'Hops'], ['Wing It!', 2023, 4, 'fern-4', 'h-fern', 'Fernwood']];
    if (servers.size && opt.scenario !== 'nothingShared') {
        posterDefs.forEach(([title, year, runtimeMin, key, serverHandle, serverName], i) => {
            if (servers.has(serverHandle)) {
                recentlyShared.push({ localItemId: 'item-' + key, title: X(title), year, runtimeMin, serverHandle, serverName: X(serverName), primaryTag: 'pr-' + key, kind: i === 3 ? 'yourCopy' : 'shared' });
            }
        });
    }

    /* ---------------- libraries (mappings) ---------------- */
    const mappings = [];
    if (servers.size) {
        const mk = (key, name, serverHandle, serverName, count, access, extra) => Object.assign({
            mappingKey: serverHandle + '.' + key, localLibraryName: X(name), remoteName: 'Movies', serverHandle, serverName: X(serverName), count, // Pilot 19 D1: the real id is "<handle>.<key>"
            lastSync: skipped({ atUtc: iso(t0 - 12 * MIN), result: opt.libraryLastSyncResult || 'ok', added: 3, removed: 0 }), heldRemovals: 0, heldAdditions: 0, // libraryLastSyncResult: a SyncResultKind word read from CatalogTypes.cs by the test
            access, drift: []
        }, extra || {});
        if (servers.has('h-hops')) {
            mappings.push(mk('m-hops-movies', 'Hops Movies', 'h-hops', 'Hops', 1204, { mode: 'Selected', selectedUserIds: ['u-rich'], includeRestricted: false }));
        }

        if (servers.has('h-fern')) {
            mappings.push(mk('m-fern-movies', 'Fernwood Movies', 'h-fern', 'Fernwood', 386, { mode: 'Everyone', selectedUserIds: ['u-rich', 'u-sam'], includeRestricted: false },
                Object.assign({ heldAdditions: opt.scenario === 'problems' ? 600 : 0 }, opt.splitCounts ? { remoteCount: 390, localCount: 386 } : {})));
        }

        if (servers.has('h-gran')) {
            mappings.push(mk('m-gran-movies', 'Granite Movies', 'h-gran', 'Granite', 612, { mode: 'Selected', selectedUserIds: [], includeRestricted: false }, {
                lastSync: { atUtc: iso(t0 - 3 * HOUR), result: 'ok', added: 0, removed: 0 },
                drift: [{ userId: 'u-alex', kind: 'extraAccess' }, { userId: 'u-kit', kind: 'managed' }]
            }));
        }

        if (opt.scenario === 'problems') {
            mappings.forEach((m) => {
                if (m.serverHandle === 'h-hops') {
                    m.heldRemovals = 140;
                }
            });
        }
    }

    // opt.libraryRows (2.9.3 U5/U6): rows built by the test from the golden files tests/contracts/peers.detail.json and libraries.get.json (the mock has
    // no fs). They replace that server's mappings in GET Peers/{handle} and GET Libraries, and the server's titleCount is their localCount sum, as in
    // the backend (AdminStatusTests.TitleCount_EqualsSumOfLibraryLocalCounts).
    if (Array.isArray(opt.libraryRows)) {
        const handles = new Set(opt.libraryRows.map((r) => r.serverHandle));
        for (let i = mappings.length - 1; i >= 0; i--) {
            if (handles.has(mappings[i].serverHandle)) {
                mappings.splice(i, 1);
            }
        }

        mappings.push(...clone(opt.libraryRows));
        for (const h of handles) {
            if (servers.has(h)) {
                servers.get(h).titleCount = opt.libraryRows.filter((r) => r.serverHandle === h).reduce((n, r) => n + (r.localCount || 0), 0);
            }
        }
    }

    /* ---------------- friends (sharing side) ---------------- */
    const shares = new Map();
    function addShare(def) {
        shares.set(def.handle, Object.assign({
            status: 'Active', libraryIds: ['lib-movies'], maxBitrateMbps: 20, maxConcurrentTranscodes: 4, shareUserName: 'shoal-' + def.handle.replace('f-', ''),
            sinceUtc: iso(t0 - 9 * DAY), lastSeenUtc: iso(t0 - 4 * MIN), playingNow: 0, transcodingNow: 0, unexpectedDeviceCount: 0,
            invite: { state: 'none' }, approval: { state: 'none' },
            lastRotatedUtc: null, rotationCount: 0, rotationFailed: false,
            devices: []
        }, def));
    }

    if (opt.scenario !== 'empty') {
        addShare({
            handle: 'f-sam', friendName: hostile ? XSS : 'Sam', playingNow: opt.nobodyPlaying ? 0 : 2, transcodingNow: opt.nobodyPlaying ? 0 : 1,
            devices: [{ deviceHandle: 'd-1', name: hostile ? XSS : 'Shoal: Sam’s Jellyfin (K7QM)', app: 'Shoal', version: '0.1.0', firstSeenUtc: iso(t0 - 9 * DAY), lastSeenUtc: iso(t0 - 4 * MIN), expected: true, signedOut: false }]
        });
        addShare({
            handle: 'f-jo', friendName: 'Jo', status: 'Paused', libraryIds: ['lib-movies', 'lib-kids'], maxBitrateMbps: null, lastSeenUtc: iso(t0 - 3 * DAY), unexpectedDeviceCount: opt.scenario === 'problems' ? 2 : 1,
            devices: [
                { deviceHandle: 'd-2', name: 'Shoal: Jo’s Jellyfin (4RZP)', app: 'Shoal', version: '0.1.0', firstSeenUtc: iso(t0 - 20 * DAY), lastSeenUtc: iso(t0 - 3 * DAY), expected: true, signedOut: false },
                { deviceHandle: 'd-3', name: hostile ? XSS : 'Chrome on Windows', app: 'Jellyfin Web', version: '12.1.0', firstSeenUtc: iso(t0 - 3 * DAY), lastSeenUtc: iso(t0 - 3 * DAY), expected: false, signedOut: true }
            ]
        });
        if (opt.scenario === 'problems') {
            addShare({ handle: 'f-lee', friendName: 'Lee', status: 'AccessRemoved', libraryIds: ['lib-movies'], lastSeenUtc: iso(t0 - 10 * DAY) });
            const sam = shares.get('f-sam');
            sam.approval = { state: 'pending', homeName: hostile ? XSS : 'Sam’s Jellyfin', homeServerIdPrefix: '3fa91c7e', addressClass: 'tailnet', triesLeft: 3 };
            sam.invite = { state: 'active', expiresUtc: iso(t0 + 9 * MIN) };
        }
    }

    /* ---------------- two-way friends and token rotation (DESIGN v2.6.1, v2.6.2) ---------------- */
    const ROLL_UP = ['AuthFailed', 'IdentityMismatch', 'TlsKeyChanged', 'PairingAborted', 'Offline', 'Degraded', 'Paused', 'Online'];
    const rollUp = (a, b) => {
        const rank = (x) => (x === null || x === undefined ? 99 : ROLL_UP.indexOf(x) < 0 ? ROLL_UP.indexOf('Degraded') : ROLL_UP.indexOf(x));
        return rank(a) <= rank(b) ? (a ?? b) : b;
    };

    // Sam's server is Hops: the same person on both sides (linked by the server id prefix in the real thing).
    const linkedPairs = opt.twoWay === false ? [] : [['f-sam', 'h-hops']];
    if (opt.twoWayShare && shares.get('f-sam')) {
        shares.get('f-sam').status = opt.twoWayShare;
    }

    if (shares.get('f-sam') && (opt.shareRotationFailed || opt.shareRotatedDaysAgo)) {
        shares.get('f-sam').rotationFailed = !!opt.shareRotationFailed;
        shares.get('f-sam').lastRotatedUtc = opt.shareRotatedDaysAgo ? iso(t0 - opt.shareRotatedDaysAgo * DAY) : null;
        shares.get('f-sam').rotationCount = opt.shareRotatedDaysAgo ? 2 : 0;
    }

    if (opt.twoWayServer && servers.get('h-hops')) {
        servers.get('h-hops').status = opt.twoWayServer;
    }

    const rotationState = new Map([['h-fern', { days: 0, lastRotatedUtc: iso(t0 - 12 * DAY), error: opt.rotationError || null, overdue: !!opt.rotationOverdue }]]);
    const shareStatusWord = (f) => (f.status === 'Active' ? 'Online' : f.status === 'Paused' ? 'Paused' : 'AuthFailed');

    function shareSide(f) {
        return {
            shareHandle: f.handle, status: f.status, libraryCount: f.libraryIds.length, deviceCount: f.devices.length, unexpectedDeviceCount: f.unexpectedDeviceCount,
            playingNow: f.playingNow, transcodingNow: f.transcodingNow, lastSeenUtc: f.lastSeenUtc
        };
    }

    function friendLinks() {
        const out = [];
        const usedShares = new Set();
        for (const s of servers.values()) {
            const pair = linkedPairs.find((p) => p[1] === s.handle && shares.has(p[0]));
            const f = pair ? shares.get(pair[0]) : null;
            if (f) {
                usedShares.add(f.handle);
            }

            out.push({
                friendHandle: 'fl-' + s.handle + (f ? '-' + f.handle : ''), name: s.displayName, linked: !!f,
                status: f ? rollUp(s.status, shareStatusWord(f)) : s.status,
                watch: { peerHandle: s.handle, status: s.status, titles: s.titleCount, activeStreams: s.activeStreams.n },
                share: f ? shareSide(f) : null
            });
        }

        for (const f of shares.values()) {
            if (!usedShares.has(f.handle)) {
                out.push({ friendHandle: 'fl-' + f.handle, name: f.friendName, linked: false, status: shareStatusWord(f), watch: null, share: shareSide(f) });
            }
        }

        return out;
    }

    function rotationOf(s) {
        const supported = (s.pairingMethod || 'invite') === 'invite';
        const r = rotationState.get(s.handle) || { days: 0, lastRotatedUtc: null, error: null, overdue: false };
        const base = r.lastRotatedUtc ? Date.parse(r.lastRotatedUtc) : t0 - 9 * DAY;
        return {
            supported, days: supported ? r.days : 0, lastRotatedUtc: supported ? r.lastRotatedUtc : null,
            nextDueUtc: supported && r.days > 0 ? iso(base + r.days * DAY) : null, error: supported ? r.error : null, overdue: supported && r.overdue
        };
    }

    const ownLibraries = [
        { id: 'lib-movies', name: hostile ? XSS : 'Movies', type: 'movies', count: 2480, selectable: true },
        { id: 'lib-kids', name: 'Kids', type: 'movies', count: 310, selectable: true },
        { id: 'lib-tv', name: 'TV shows', type: 'tvshows', count: 120, selectable: true }, // ShareService.Available: only friends' libraries are not selectable
        { id: 'lib-friend', name: hostile ? XSS : 'Hops Movies', type: 'movies', count: 1204, selectable: false, shareable: false, reason: 'federated' }
    ];

    /* ---------------- settings, protection, state ---------------- */
    const settings = {
        stubRoot: '/var/lib/jellyfin/shoal/library', stubRootCheck: 'ok', syncIntervalHours: 6,
        massDeletionMinCount: 25, massDeletionPercent: 10, newItemsMinCount: 500, newItemsPercent: 20,
        diskQuotaGbPerPeer: 2, artQuality: 'Standard', autoFitQualityToLink: true, maxStreamsPerUser: 4, hlsSubtitlesEnabled: true,
        speedTestIntervalHours: 6, notifyClientsWhenOffline: true, parkOfflinePeers: false, parkAfterHours: 0.5,
        tailscaleSocket: 'auto', tailscaleDetected: true, keySource: 'file', secretsVolumeUnsafe: false,
        hlsBufferAhead: 6, offlineCardEnabled: true, requestServiceName: 'Seerr', requestServiceUrl: '', alternateVersionsEnabled: false, tvShowsEnabled: true, tvEpisodeThumbs: true, trickplayEnabled: true, trickplayCacheGb: 1, trickplayCacheBytes: opt.trickplayBytes ?? 312 * 1024 * 1024, attachmentsEnabled: true, reencodeImages: true,
        imageSandbox: opt.sandbox === 'unavailable' ? { state: 'unavailable', limits: 'partial', reason: opt.sandboxReason || 'ffmpegMissing', rejected: 0 }
            : opt.sandbox === 'partial' ? { state: 'active', limits: 'partial', reason: null, rejected: 4 } : { state: 'active', limits: 'full', reason: null, rejected: 0 }
    };

    // Sharing accounts no share uses (Warden 08 M7): the Overview lists them, the admin deletes them.
    const orphans = new Map((opt.orphans ? [
        { id: '9a1e0000-0000-4000-8000-000000000001', name: hostile ? XSS : 'shoal-riley-3f2a', isDisabled: false },
        { id: '9a1e0000-0000-4000-8000-000000000002', name: 'shoal-old-91bc', isDisabled: true }
    ] : []).map((a) => [a.id, a]));

    const hardening = {
        mode: 'LogOnly', modes: ['LogOnly', 'Enforce', 'Off'], sinceUtc: iso(t0 - 9 * DAY),
        counters: [
            { app: hostile ? XSS : 'Jellyfin for Kodi', network: 'tailnet', kind: 'stream', count: 14 },
            { app: 'VLC', network: 'internet', kind: 'stream', count: 3 }
        ],
        guardErrors: 0, shieldHits: 12, transcodeCapHits: 2,
        inviteFailures: { lastHour: opt.inviteFails || 0, notice: (opt.inviteFails || 0) >= 5 }, proxySuspected: !!opt.proxySuspected
    };

    const stateInfo = {
        mode: 'Normal', files: [
            { file: 'state.json.bak', writtenUtc: iso(t0 - 2 * HOUR), kind: 'bak' },
            { file: 'state-2026-10-14.json', writtenUtc: iso(t0 - 1 * DAY), kind: 'daily' },
            { file: 'state-2026-10-13.json', writtenUtc: iso(t0 - 2 * DAY), kind: 'daily' }
        ],
        lastBackupUtc: iso(t0 - 2 * HOUR), findings: [], backupByCopy: !!opt.backupByCopy
    };
    if (opt.orphanFolders) {
        stateInfo.findings = Array.from({ length: opt.orphanFolders }, (_, i) => ({ kind: 'orphanFolder', library: ('0123456789abcdef0123456789abcd' + String(10 + i)) }));
    }

    if (opt.scenario === 'problems') {
        stateInfo.findings = [
            { kind: 'missingKey', peerHandle: 'h-pine', serverName: 'Pinecone' },
            { kind: 'missingFolder', peerHandle: 'h-ivy', serverName: 'Ivy' },
            { kind: 'orphanLibrary', library: 'Old Friend Movies' }
        ];
    }

    const banner = { mode: 'Normal', reason: null, restoredFrom: null };
    let secretsUnreadable = false;
    if (opt.scenario === 'recovery') {
        banner.mode = 'Recovery';
        banner.reason = 'unreadable';
        banner.guardLastGood = opt.guardLastGood || 'ok';
        stateInfo.mode = 'Recovery';
    } else if (opt.scenario === 'recoveryNewer') {
        banner.mode = 'Recovery';
        banner.reason = 'newerSchema';
        banner.guardLastGood = opt.guardLastGood || 'unreadable';
        stateInfo.mode = 'Recovery';
    } else if (opt.scenario === 'keys') {
        secretsUnreadable = true;
    } else if (opt.scenario === 'restored') {
        banner.restoredFrom = { file: 'state.json.bak', atUtc: iso(t0 - 5 * HOUR), needsConfirm: true };
    }

    const cooldownUntil = new Map();

    /* ---------------- derived: alerts and server detail ---------------- */
    function alertsFor(s) {
        const a = [];
        const ex = opt.noExamples ? [] : [X('Sintel'), X('Spring'), X('Hero')];
        if (s.heldRemovals) {
            a.push({ kind: 'heldRemovals', severity: 'warn', count: s.heldRemovals, examples: ex });
        }

        if (s.heldAdditions) {
            a.push({ kind: 'heldAdditions', severity: 'warn', count: s.heldAdditions, examples: ex });
        }

        if (s.status === 'AuthFailed') {
            a.push({ kind: 'authFailed', severity: 'bad' });
        }

        if (s.status === 'IdentityMismatch') {
            a.push({ kind: 'identityMismatch', severity: 'bad' });
        }

        if (s.status === 'TlsKeyChanged') {
            a.push({ kind: 'tlsKeyChanged', severity: 'bad', pinnedKey: 'sha256:9f2c1b7a44d0e8aa3b65c1f0d2e97a18b3c4d5e6f708192a3b4c5d6e7f801234', presentedKey: hostile ? XSS : 'sha256:41ab90cc7d1e2f3a4b5c6d7e8f9091a2b3c4d5e6f708192a3b4c5d6e7f805678' });
        }

        if (opt.scenario === 'problems' && s.handle === 'h-hops') {
            a.push({ kind: 'foreignDevice', severity: 'bad', deviceName: XSS });
            a.push({ kind: 'policy', severity: 'warn', policy: ['allLibraries', 'download'] });
            a.push({ kind: 'mergedVersion', severity: 'warn', localItemId: 'item-hops-1', title: X('Sintel') });
            a.push({ kind: 'diskQuota', severity: 'warn' });
            a.push({ kind: 'entityCap', severity: 'warn' });
            a.push({ kind: 'entryCap', severity: 'bad' });
            a.push({ kind: 'stubFolder', severity: 'bad' });
        }

        return a;
    }

    function serverDetail(s) {
        const d = detail.get(s.handle) || {};
        return Object.assign(clone(s), {
            serverIdPrefix: d.serverIdPrefix || (s.handle.length * 1234567).toString(16).padStart(8, '0').slice(0, 8),
            pairedUtc: s.paired === false ? undefined : iso(t0 - 9 * DAY), pairingMethod: s.pairingMethod || 'invite',
            pairingSession: session && session.peerHandle === s.handle && !session.error ? 'ps-1' : undefined,
            libraries: clone(mappings.filter((x) => x.serverHandle === s.handle)),
            users: clone(users),
            alerts: alertsFor(s),
            usage: { diskBytes: 0.8e9, quotaBytes: 2e9, stubs: s.titleCount, people: 4120, genres: 38, studios: 210 },
            settings: { maxConcurrentStreams: 4, priority: 100, proxyUrl: s.proxy ? 'socks5://127.0.0.1:1055' : '', tlsPinned: !!s.pinned },
            rotation: rotationOf(s),
            link: (() => {
                const pair = linkedPairs.find((p) => p[1] === s.handle && shares.has(p[0]));
                return pair ? friendLinks().find((l) => l.watch && l.watch.peerHandle === s.handle) : undefined;
            })(),
            streams: s.activeStreams.n ? [{ person: X('Sam'), title: X('Tears of Steel'), method: 'Direct', startedUtc: iso(t0 - 22 * MIN), mbps: 14 }] : [],
            updatedUtc: iso(t0 - 5 * MIN)
        });
    }

    function speedFor(s, range) {
        const spanMs = { '24h': DAY, '7d': 7 * DAY, '30d': 30 * DAY }[range] || DAY;
        const r = rng(s.handle.length * 977 + (range === '24h' ? 1 : range === '7d' ? 2 : 3));
        const samples = [];
        const online = !['Offline', 'AuthFailed', 'IdentityMismatch', 'TlsKeyChanged', 'Disabled', 'Pairing', 'PairingAborted'].includes(s.status);
        const base = s.mbps || 12;
        const stepMs = range === '24h' ? 6 * HOUR : range === '7d' ? 6 * HOUR : 12 * HOUR;
        if (opt.scenario === 'nothingShared' || s.status === 'Pairing' || s.status === 'PairingAborted') {
            return { samples: [], offline: [], routeChanges: [], kpis: {}, ladder: [], bottleneck: { kind: 'none' }, localApiAvailable: true, cooldowns: {}, route: { kind: 'unknown' }, hints: [] };
        }

        for (let ms = t0 - spanMs + stepMs / 2; ms < t0; ms += stepMs) {
            const v = Math.max(2, base * (0.55 + r() * 0.75));
            samples.push({ t: iso(ms), kind: 'scheduled', mbps: Math.round(v * 10) / 10, rttMedianMs: Math.round((s.rttMs || 60) * (0.9 + r() * 0.3)), rttP90Ms: Math.round((s.rttMs || 60) * 1.4), jitterMs: Math.round(r() * 12), lossPct: 0, path: s.path === 'derp' ? 'derp' : 'direct' });
        }

        if (online) {
            samples.push({ t: iso(t0 - 18 * MIN), kind: 'manual', mbps: s.mbps, rttMedianMs: s.rttMs, rttP90Ms: Math.round(s.rttMs * 1.3), jitterMs: s.jitterMs, lossPct: s.lossPct, path: s.path === 'derp' ? 'derp' : 'direct' });
            samples.push({ t: iso(t0 - 40 * MIN), kind: 'playback', mbps: Math.round(base * 0.9 * 10) / 10, path: 'direct', who: X('Sam'), method: 'direct', segmentWaitMs: 180, pauses: 0 });
            samples.push({ t: iso(t0 - 3 * HOUR), kind: 'deep', mbps: Math.round(base * 1.1 * 10) / 10, rttMedianMs: s.rttMs, rttP90Ms: s.rttMs + 30, jitterMs: 3, lossPct: 0, path: 'direct' });
        } else {
            samples.push({ t: iso(t0 - 2 * HOUR - 30 * MIN), kind: 'scheduled', mbps: null, path: 'unknown', error: 'timeout' });
        }

        samples.sort((a, b) => Date.parse(a.t) - Date.parse(b.t));
        const mbpsList = samples.filter((x) => typeof x.mbps === 'number').map((x) => x.mbps);
        const offline = [];
        if (s.status === 'Offline') {
            offline.push({ fromUtc: iso(t0 - 2 * HOUR - 20 * MIN), toUtc: iso(t0) });
        } else if (online) {
            offline.push({ fromUtc: iso(t0 - spanMs * 0.62), toUtc: iso(t0 - spanMs * 0.62 + 90 * MIN) });
        }

        const bottleneckKind = s.path === 'derp' ? 'relay' : s.status === 'Degraded' ? 'perConnection' : s.handle === 'h-hops' ? 'peerUpload' : 'none';
        return {
            samples, offline, routeChanges: online ? [{ t: iso(t0 - spanMs * 0.3), from: 'direct', to: s.path === 'derp' ? 'derp' : 'direct' }].filter((x) => x.from !== x.to) : [],
            kpis: { nowMbps: online ? s.mbps : null, latencyMs: s.rttMs, jitterMs: s.jitterMs, lossPct: s.lossPct, lowestMbps: mbpsList.length ? Math.min(...mbpsList) : null },
            ladder: online ? ladderFor(s.mbps, true) : [],
            bottleneck: { kind: bottleneckKind, region: s.derpRegion || undefined },
            localApiAvailable: s.network === 'Tailnet',
            cooldowns: {},
            route: s.path === 'derp' ? { kind: 'derp', region: s.derpRegion } : s.path === 'derp?' ? { kind: 'maybe' } : s.path === 'direct' ? { kind: 'direct' } : { kind: 'unknown' },
            // opt.speedHints: the hints a Speed answer carries (Pilot 14: one that repeats the bottleneck)
            hints: opt.speedHints || s.hints || []
        };
    }

    function overlapFor(s) {
        if (!s || opt.scenario === 'nothingShared' || s.titleCount === 0) {
            return { computedUtc: null };
        }

        return {
            computedUtc: iso(t0 - 4 * HOUR), both: { count: 342, localBytes: 1.8e12 }, gain: { count: Math.max(0, s.titleCount - 342), peerBytes: 4.1e12 },
            byQuality: [
                { q: '4k', bothCount: 40, localBytes: 0.9e12, gainCount: 120, peerBytes: 2.4e12 },
                { q: '1080p', bothCount: 210, localBytes: 0.8e12, gainCount: 560, peerBytes: 1.5e12 },
                { q: '720p', bothCount: 80, localBytes: 0.1e12, gainCount: 150, peerBytes: 0.2e12 },
                { q: 'sd', bothCount: 12, localBytes: 0.02e12, gainCount: 32, peerBytes: 0.0e12 }
            ],
            betterCopyCount: s.handle === 'h-hops' ? 14 : undefined,
            // K53 (docs/kiro-v2.9-04.md): absent without groups. opt.backups: 'first' (default with playOrder), 'only' (backup for, plays none first), 'none'.
            backups: opt.playOrder && opt.backups !== 'none' && (s.handle === 'h-hops' || s.handle === 'h-fern')
                ? (opt.backups === 'only' ? { sharedWithOthers: 12, playsFirst: 0, backupFor: 12 } : { sharedWithOthers: 250, playsFirst: 238, backupFor: 12 })
                : undefined,
            // TV shows from friends are always on (v2.9.1); opt.noTvOverlap: a server that counts no series.
            tv: opt.noTvOverlap ? undefined : { sharedSeries: 12, sharedSeriesEpisodes: 310, gainSeries: 8, gainEpisodes: 150 }
        };
    }

    /* ---------------- pairing (ui-spec 8.2, DESIGN 4.2a) ---------------- */
    let probeSeq = 0;
    const probes = new Map();
    // opt.ghost: a finished pairing whose server is no longer there (Pilot 08 N5).
    let session = opt.ghost ? { createdAt: now(), method: 'qc', state: 'done', error: null, displayName: 'Ghost', url: 'http://ghost.tail1234.ts.net:8096', network: 'Tailnet', serverIdPrefix: '3fa91c7e', lifetimeMs: 600000, code: null, safetyCode: 'K7QM', peerHandle: 'h-ghost', rePair: false } : null;
    const addedPeers = new Set();
    const SAFETY = ['requestReplaced', 'codesDidNotMatch', 'serverChanged', 'adminAccount', 'badPeerResponse'];
    const SCENARIO_KIND = { replaced: 'requestReplaced', denied: 'denied' };

    function newCode() {
        return String(100000 + Math.floor(Math.random() * 899999));
    }

    /** The server's own record of a stopped pairing (what the Overview card and the stored view read). */
    function storeStop(s, kind, phase) {
        const stoppedUtc = iso(now());
        let rec = s.peerHandle ? servers.get(s.peerHandle) : null;
        if (!rec) {
            s.peerHandle = 'h-new-' + (++probeSeq);
            rec = addServer({
                handle: s.peerHandle, displayName: s.displayName, url: s.url, network: s.network || 'Tailnet', status: 'PairingAborted', path: 'unknown', rttMs: null, mbps: null, titleCount: 0,
                paired: false
            });
        } else {
            s.prevStatus = s.prevStatus || rec.status;
            rec.status = 'PairingAborted';
        }

        Object.assign(rec, { pairingError: kind, pairingPhase: phase || undefined, pairingMethod: s.method, pairingStoppedUtc: stoppedUtc });
        detail.set(s.peerHandle, Object.assign(detail.get(s.peerHandle) || {}, { serverIdPrefix: s.serverIdPrefix, prevStatus: s.prevStatus }));
    }

    function failSession(s, kind, phase) {
        s.error = kind;
        s.state = 'failed';
        s.phase = phase;
        storeStop(s, kind, phase);
    }

    function pairingView() {
        if (!session) {
            return null;
        }

        const s = session;
        const age = now() - s.createdAt;
        if (!s.error && s.state !== 'done') {
            const wanted = SCENARIO_KIND[opt.pairing] || opt.pairing;
            if (s.state === 'waiting' && age > s.lifetimeMs) {
                failSession(s, 'expired', 'beforeApproval');
            } else if (s.method === 'qc' && s.state === 'gettingCode' && age > 600) {
                s.state = 'waiting';
                s.code = newCode();
            } else if (s.method === 'qc' && s.state === 'waiting') {
                s.peerSilent = opt.pairing === 'silent' && age > 3000 && age < 6000;
                if (age > 5000 && !['ok', 'silent', 'slow'].includes(opt.pairing)) {
                    failSession(s, wanted, wanted === 'interrupted' ? 'beforeApproval' : undefined);
                } else if (age > (opt.pairing === 'silent' ? 7000 : 5000) && opt.pairing !== 'slow') {
                    s.state = 'awaitingConfirm';
                    s.confirmDeadline = now() + 15 * MIN;
                }
            } else if (s.method === 'invite' && s.state === 'waiting' && age > 6000 && opt.pairing !== 'slow') {
                if (opt.pairing === 'ok' || opt.pairing === 'silent') {
                    s.state = 'done';
                    finishPairing();
                } else {
                    failSession(s, wanted, wanted === 'interrupted' ? 'beforeApproval' : undefined);
                }
            } else if (s.state === 'awaitingConfirm' && now() > s.confirmDeadline) {
                failSession(s, 'expired', 'awaitingConfirm');
            }
        }

        // The shape of PairingSessionInfo (K5): step and state are the server's words; the page maps them (REAL_STEPS in shoal.js).
        const step = s.error ? (s.phase === 'awaitingConfirm' ? 'awaitingConfirm' : 'awaitingApproval') : ({ gettingCode: 'gettingCode', waiting: 'awaitingApproval', awaitingConfirm: 'awaitingConfirm', done: 'confirmed' }[s.state] || 'awaitingApproval');
        return {
            direction: 'watch', method: s.method, peerHandle: s.peerHandle || '', step, state: s.error ? 'aborted' : s.state === 'done' ? 'confirmed' : 'running',
            code: s.method === 'qc' && s.state !== 'gettingCode' ? s.code : null,
            safetyCode: (s.method === 'qc' && s.state === 'gettingCode') || (opt.lateCode && age < 3000) ? null : s.safetyCode,
            expiresUtc: opt.noExpiry ? null : iso(s.state === 'awaitingConfirm' ? s.confirmDeadline : s.createdAt + s.lifetimeMs),
            error: s.error || null, phase: s.phase || null, peerSilent: !!s.peerSilent, paired: !!s.rePair, accountWarnings: [], accountNotes: [], startedUtc: iso(s.createdAt)
        };
    }

    function finishPairing() {
        if (!session) {
            return;
        }

        let rec = session.peerHandle ? servers.get(session.peerHandle) : null;
        if (!rec) {
            session.peerHandle = 'h-new-' + (++probeSeq);
            rec = addServer({ handle: session.peerHandle, displayName: session.displayName, url: session.url, network: session.network || 'Tailnet', status: 'Online', path: 'direct', rttMs: 41, mbps: 38, titleCount: 0, syncing: true, latest: null });
        } else {
            Object.assign(rec, { status: 'Online', pairingError: null, path: 'direct', rttMs: 41, mbps: 38, syncing: true });
        }

        addedPeers.add(session.peerHandle);
        rec.syncProgress = { done: 0, total: 1204 };
        detail.set(session.peerHandle, Object.assign(detail.get(session.peerHandle) || {}, { serverIdPrefix: session.serverIdPrefix }));
    }

    /* ---------------- router ---------------- */
    const routes = [];
    const route = (method, re, fn) => routes.push({ method, re: new RegExp('^' + re + '$'), fn });

    route('GET', 'Status', () => {
        const list = Array.from(servers.values()).map((s) => {
            const copy = clone(s);
            copy.alertCount = alertsFor(s).length;
            return copy;
        });
        return ok({
            servers: list,
            recentlyShared: clone(recentlyShared),
            friends: Array.from(shares.values()).map((f) => ({
                handle: f.handle, name: f.friendName, status: f.status, libraryCount: f.libraryIds.length, deviceCount: f.devices.length,
                unexpectedDeviceCount: f.unexpectedDeviceCount, lastSeenUtc: f.lastSeenUtc, playingNow: f.playingNow, transcodingNow: f.transcodingNow
            })),
            cooldowns: cooldownUntil.has('sync') && cooldownUntil.get('sync') > now() ? { syncAllUntilUtc: iso(cooldownUntil.get('sync')) } : {},
            state: clone(banner),
            secretsUnreadable,
            keySource: opt.keySource || settings.keySource,
            secretsReason: secretsUnreadable ? (opt.secretsReason || 'decryptFailed') : undefined,
            homeName: self.homeName,
            inviteFailures: clone(hardening.inviteFailures),
            friendLinks: friendLinks(),
            imageSandbox: clone(settings.imageSandbox),
            intercept: {
                sinceUtc: iso(t0 - 3 * HOUR),
                counters: [
                    { name: 'nonstatic-stream', count: opt.interceptHits || 0 }, { name: 'audio-route', count: 0 }, { name: 'trickplay-m3u8', count: 2 }, { name: 'no-credentials', count: 0 },
                    { name: 'core-hls-route', count: 0 }, { name: 'forbidden-route', count: 1 }, { name: 'long-query', count: 0 }
                ]
            },
            orphanAccounts: Array.from(orphans.values()).map(clone),
            // opt.nfoMains: main libraries holding friends' titles whose NFO saver is on (Pilot 15 R6, docs/kiro-v2.9-09.md item 4)
            mainLibrariesSavingNfo: Array.isArray(opt.nfoMains) ? opt.nfoMains.slice() : [],
            // opt.notLocked: Jellyfin libraries holding a Shoal folder beside other folders (Warden 12 L2)
            shoalLibrariesNotLocked: Array.isArray(opt.notLocked) ? opt.notLocked.slice() : [],
            mainLibrariesMissing: Array.isArray(opt.mainsMissing) ? opt.mainsMissing.slice() : [],
            currentUserId: self.currentUserId,
            imageCredit: opt.scenario === 'default' ? 'Demo art: Blender Foundation open movies (CC BY).' : undefined
        });
    });

    route('DELETE', 'OrphanAccounts/([^/]+)', (m) => {
        const id = decodeURIComponent(m[1]);
        if (!orphans.has(id)) {
            return fail(404, 'notFound');
        }

        orphans.delete(id);
        return ok({});
    });

    route('POST', 'Peers/([^/]+)/Rotate', (m) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (!s) {
            return fail(404, 'notFound');
        }

        const key = 'rotate:' + s.handle;
        if (cooldownUntil.has(key) && cooldownUntil.get(key) > now()) {
            return fail(429, 'rateLimited', { 'Retry-After': String(Math.ceil((cooldownUntil.get(key) - now()) / 1000)) });
        }

        if (!rotationOf(s).supported) {
            return fail(409, 'notSupported');
        }

        if (opt.rotateRefusal) {
            return fail(409, opt.rotateRefusal);
        }

        if (opt.rotateFailure) {
            return fail(502, opt.rotateFailure);
        }

        const r = rotationState.get(s.handle) || { days: 0, error: null, overdue: false };
        r.lastRotatedUtc = iso(now());
        r.error = null;
        r.overdue = false;
        rotationState.set(s.handle, r);
        cooldownUntil.set(key, now() + 10 * MIN);
        return ok({ rotatedUtc: r.lastRotatedUtc });
    });
    route('GET', 'Peers/([^/]+)/Rotation', (m) => {
        const s = servers.get(decodeURIComponent(m[1]));
        return s ? ok(rotationOf(s)) : fail(404, 'notFound');
    });
    route('PUT', 'Peers/([^/]+)/Rotation', (m, body) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (!s) {
            return fail(404, 'notFound');
        }

        if (![0, 30, 60, 90].includes(body && body.days)) {
            return fail(400, 'invalidDays');
        }

        if (body.days > 0 && !rotationOf(s).supported) {
            return fail(409, 'notSupported');
        }

        const r = rotationState.get(s.handle) || { days: 0, lastRotatedUtc: null, error: null, overdue: false };
        r.days = body.days;
        rotationState.set(s.handle, r);
        return ok(rotationOf(s));
    });

    route('POST', 'Sync', () => {
        if (cooldownUntil.has('sync') && cooldownUntil.get('sync') > now()) {
            return fail(429, 'rateLimited', { 'Retry-After': '240' });
        }

        cooldownUntil.set('sync', now() + 5 * MIN);
        servers.forEach((s) => {
            if (s.status === 'Online') {
                s.syncing = true;
            }
        });
        // syncSkipReason: the request does not run, so lastSync keeps the old sync and gains the skip.
        setTimeout(() => servers.forEach((s) => {
            s.syncing = false;
            s.lastSync = opt.syncSkipReason ? Object.assign({}, s.lastSync, { skippedUtc: iso(now()), skipReason: opt.syncSkipReason }) : { atUtc: iso(now()), result: 'ok', added: 2, removed: 1 };
        }), 4000);
        return ok({}, 202);
    });

    route('GET', 'Peers/([^/]+)', (m) => {
        const s = servers.get(decodeURIComponent(m[1]));
        return s ? ok(serverDetail(s)) : fail(404);
    });
    route('PUT', 'Peers/([^/]+)', (m, body) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (!s) {
            return fail(404);
        }

        if (session && session.peerHandle === s.handle && !session.error && (body.url !== s.url || body.proxyUrl)) {
            return fail(409, 'pairingInProgress');
        }

        const hostOf = (u) => { try { return new URL(u).hostname; } catch { return ''; } };
        if (body.url && hostOf(body.url) !== hostOf(s.url)) {
            if (body.confirmName !== s.displayName) {
                return fail(400, 'confirmName');
            }

            s.url = body.url;
            s.status = 'AuthFailed';
        } else if (body.url) {
            s.url = body.url;
        }

        if (body.tlsPinned === true && !s.pinned) {
            if (!body.expectedPin) {
                return fail(400, 'expectedPin');
            }

            const c = certOf(s);
            if (body.expectedPin !== c.pin) {
                return fail(409, 'changedAgain');
            }

            if (!c.chainValid && body.acceptUntrusted !== true) {
                return fail(409, 'untrusted');
            }
        }

        s.pinned = body.tlsPinned === true;
        return ok({});
    });
    route('DELETE', 'Peers/([^/]+)', (m, body) => {
        if (opt.removeError) {
            return fail(409, opt.removeError);
        }

        const h = decodeURIComponent(m[1]);
        const target = servers.get(h);
        if (!target) {
            return fail(404);
        }

        if (!body || body.confirmName !== target.displayName) {
            return fail(400, 'confirmName');
        }

        servers.delete(h);
        for (let i = mappings.length - 1; i >= 0; i -= 1) {
            if (mappings[i].serverHandle === h) {
                mappings.splice(i, 1);
            }
        }

        return ok({});
    });
    route('POST', 'Peers/([^/]+)/Sync', (m) => {
        const key = 'peersync:' + m[1];
        if (cooldownUntil.has(key) && cooldownUntil.get(key) > now()) {
            return fail(429, 'rateLimited', { 'Retry-After': '240' });
        }

        cooldownUntil.set(key, now() + 5 * MIN);
        return ok({}, 202);
    });
    route('POST', 'Peers/([^/]+)/Pause', (m) => {
        if (opt.actionFails) {
            return fail(500, 'server');
        }

        const s = servers.get(decodeURIComponent(m[1]));
        if (s) {
            s.status = 'Disabled';
        }

        return ok({});
    });
    route('POST', 'Peers/([^/]+)/Resume', (m) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (s) {
            s.status = 'Online';
        }

        return ok({});
    });
    // The key a server shows now: 32 bytes as a base64 pin and as colon separated hex pairs. opt.certChainValid=false makes it fail the chain check;
    // opt.certMoves makes the key change between the read and the trust (409 changedAgain).
    const certBytes = (s) => Array.from({ length: 32 }, (_, i) => (s.handle.charCodeAt(i % s.handle.length) * 7 + i * 13 + (s.certGeneration || 0)) % 256);
    const certOf = (s) => {
        const bytes = certBytes(s);
        return {
            pin: typeof Buffer !== 'undefined' ? Buffer.from(bytes).toString('base64') : btoa(String.fromCharCode(...bytes)),
            fingerprint: bytes.map((b) => b.toString(16).padStart(2, '0').toUpperCase()).join(':'),
            chainValid: opt.certChainValid !== false
        };
    };
    route('GET', 'Peers/([^/]+)/Certificate', (m) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (!s) {
            return fail(404, 'notFound');
        }

        if (!/^https:/i.test(s.url)) {
            return fail(400, 'notHttps');
        }

        const c = certOf(s);
        if (opt.certMoves) {
            s.certGeneration = (s.certGeneration || 0) + 1;
        }

        return ok(c);
    });
    route('POST', 'Peers/([^/]+)/TrustCertificate', (m, body) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (s) {
            const c = certOf(s);
            if (!body || body.expectedPin !== c.pin) {
                return fail(409, 'changedAgain');
            }

            if (s.network === 'Public' && !c.chainValid && body.acceptUntrusted !== true) {
                return fail(409, 'untrusted');
            }

            s.status = 'Online';
            s.path = 'direct';
            s.rttMs = 52;
            s.mbps = 31;
        }

        return ok({});
    });
    // Held episodes grouped by series (K40). opt.heldSeries: the number of series on hold (default 0).
    const heldSeries = { removals: [], additions: [] };
    if (opt.heldSeries) {
        heldSeries.removals = [{ series: 'sk-1', title: 'Andor', year: 2022, episodes: 12 }, { series: 'sk-2', title: 'Severance', year: 2022, episodes: 9 }];
        heldSeries.additions = [{ series: 'sk-3', title: 'Silo', year: 2023, episodes: 20 }];
    }

    route('GET', 'Peers/([^/]+)/Holds', (m) => (servers.get(decodeURIComponent(m[1])) ? ok(clone(heldSeries)) : fail(404)));
    route('POST', 'Peers/([^/]+)/Holds/([^/]+)/(Release|Keep)', (m, body, query) => {
        const s = servers.get(decodeURIComponent(m[1]));
        if (query && query.series && m[3] === 'Release') {
            const list = heldSeries[m[2]] || [];
            const at = list.findIndex((r) => r.series === query.series);
            if (at < 0) {
                return fail(404, 'notFound');
            }

            list.splice(at, 1);
            return ok({}, 202);
        }

        if (s) {
            if (m[2] === 'removals') {
                s.heldRemovals = 0;
            } else {
                s.heldAdditions = 0;
            }

            mappings.filter((x) => x.serverHandle === s.handle).forEach((x) => {
                x.heldRemovals = m[2] === 'removals' ? 0 : x.heldRemovals;
                x.heldAdditions = m[2] === 'additions' ? 0 : x.heldAdditions;
            });
        }

        return ok({});
    });
    // opt.remoteLibrariesGolden / opt.addLibraryGolden: { status, body } read by the test from tests/contracts (the mock has no fs), served as is.
    route('GET', 'Peers/([^/]+)/RemoteLibraries', (m) => (opt.remoteLibrariesGolden ? { status: opt.remoteLibrariesGolden.status, body: clone(opt.remoteLibrariesGolden.body), headers: {} } : opt.ghost && decodeURIComponent(m[1]) === 'h-ghost' ? fail(404, 'notFound') : ok([
        { mappingKey: 'rl-movies', name: X('Movies'), type: 'movies', count: 1204, remoteCount: 1204, selectable: true },
        { mappingKey: 'rl-big', name: 'Everything', type: 'movies', count: 8400, selectable: true },
        { mappingKey: 'rl-tv', name: 'TV shows', type: 'tvshows', count: 90, seriesCount: 12, episodeCount: 90, selectable: true }
    ])));
    route('POST', 'Peers/([^/]+)/Libraries', (m, body) => {
        if (opt.addLibraryGolden) {
            return { status: opt.addLibraryGolden.status, body: clone(opt.addLibraryGolden.body), headers: {} };
        }

        if (opt.addLibraryError) {
            return fail(opt.addLibraryError === 'server' ? 500 : 409, opt.addLibraryError);
        }

        const h = decodeURIComponent(m[1]);
        const s = servers.get(h);
        const hidden = [];
        for (const lib of Array.isArray(body) ? body : []) { // the real controller takes a bare array
            mappings.push({
                mappingKey: h + '.' + lib.mappingKey, localLibraryName: lib.localLibraryName, remoteName: lib.localLibraryName, serverHandle: h, serverName: s ? s.displayName : '', count: 0,
                lastSync: null, heldRemovals: 0, heldAdditions: 0, access: { mode: 'Selected', selectedUserIds: lib.selectedUserIds || [], includeRestricted: false }, drift: []
            });
            if (!(lib.selectedUserIds || []).length) {
                hidden.push(lib.localLibraryName);
            }
        }

        return ok({ hiddenLibraries: hidden });
    });
    route('DELETE', 'Peers/([^/]+)/Libraries/([^/]+)', (m) => {
        // As the real controller: the mapping key of that server (a "<handle>." prefix of the same server is accepted, Pilot 19 D1); anything else is 404.
        const h = decodeURIComponent(m[1]);
        const key = decodeURIComponent(m[2]);
        const bare = key.startsWith(h + '.') ? key.slice(h.length + 1) : key;
        const i = mappings.findIndex((x) => x.serverHandle === h && x.mappingKey === h + '.' + bare);
        if (i < 0) {
            return fail(404, 'notFound');
        }

        mappings.splice(i, 1);
        return ok({ removed: true });
    });
    route('GET', 'Peers/([^/]+)/Overlap', (m) => ok(overlapFor(servers.get(decodeURIComponent(m[1])))));

    /* ---------------- quality limits and play order (K53, docs/kiro-v2.9-04.md) ----------------
     * opt.playOrder: GET PlayOrder answers (true, or 'odd' for an unknown reason, 'one' for a single server); without it a bare 404 (older server).
     * opt.quality: GET Peers/{h}/Quality answers (true; 'rec' adds the speed-test card for Fernwood; 'limited' starts Fernwood at 1080p from a suggestion).
     * opt.qualityChanged: the first PUT finds another admin's limit (409 changed). opt.recStale: the first Dismiss is stale.
     * opt.priorityRoute === false: PUT Peers/{h}/Priority answers a bare 404 (an older server). */
    const tiers = new Map([['h-gran', 'Backup']]);
    const limits = new Map();
    const dismissedRec = new Set();
    if (opt.quality === 'limited') {
        limits.set('h-fern', { maxClass: '1080', maxMbps: null, setUtc: iso(t0 - 3 * HOUR), origin: 'Recommendation' });
    }

    const limitOf = (h) => limits.get(h) || { maxClass: 'Off', maxMbps: null, setUtc: null, origin: 'Manual' };
    const effectiveOf = (l) => {
        const box = { 480: [854, 480], 720: [1280, 720], 1080: [1920, 1080], 2160: [3840, 2160] }[l.maxClass];
        const label = { 480: '480p', 720: '720p', 1080: '1080p', 2160: '4K' }[l.maxClass];
        const limited = !!box || l.maxMbps !== null;
        return { maxClass: l.maxClass, maxMbps: l.maxMbps, maxWidth: box ? box[0] : null, maxHeight: box ? box[1] : null, limited, text: limited ? 'Up to ' + [label, l.maxMbps ? l.maxMbps + ' Mbps' : null].filter(Boolean).join(', ') : 'No limit' };
    };
    const CLASS_RANK = { 480: 1, 720: 2, 1080: 3, 2160: 4, Off: 5 };
    function recFor(h) {
        const s = servers.get(h);
        if (opt.quality !== 'rec' || h !== 'h-fern' || !s || dismissedRec.has(h) || CLASS_RANK[limitOf(h).maxClass] <= CLASS_RANK['1080']) {
            return null;
        }

        return { suggestedClass: '1080', slowForClass: '2160', throughputMbps: 17, titlesAbove: 120, titlesKnown: 386, text: 'This link is slow for 4K. Limit ' + s.displayName + ' to 1080p?' };
    }

    function playOrderRowsMock() {
        const order = opt.playOrder === 'one' ? ['h-hops'] : ['h-hops', 'h-fern', 'h-gran', 'h-gone'];
        const reasons = { 'h-hops': 'fastest link', 'h-fern': 'slower link', 'h-gran': opt.playOrder === 'odd' ? 'something new' : 'offline', 'h-gone': 'fastest link' };
        const rank = (h) => ({ Prefer: 0, Normal: 1, Backup: 2 })[tiers.get(h) || 'Normal'];
        const sorted = order.slice().sort((a, b) => rank(a) - rank(b) || order.indexOf(a) - order.indexOf(b));
        return sorted.map((h, i) => {
            const s = servers.get(h);
            const tier = tiers.get(h) || 'Normal';
            const prefix = { Prefer: 'Always prefer', Normal: 'Automatic', Backup: 'Backup only' }[tier];
            return { peerKey: h, name: s ? s.displayName : 'Gone', tier, position: i + 1, count: sorted.length, reason: reasons[h], label: prefix + ': ' + (i + 1) + ' of ' + sorted.length + ' (' + reasons[h] + ')', effective: effectiveOf(limitOf(h)) };
        });
    }

    route('GET', 'PlayOrder', () => (opt.playOrder ? ok({ playOrder: playOrderRowsMock() }) : fail(404)));
    route('PUT', 'Peers/([^/]+)/Priority', (m, body) => {
        const h = decodeURIComponent(m[1]);
        if (opt.priorityRoute === false) {
            return fail(404);
        }

        if (!servers.has(h)) {
            return fail(404, 'notFound');
        }

        if (!body || !['Prefer', 'Normal', 'Backup'].includes(body.tier)) {
            return fail(400, 'invalid');
        }

        if (body.expectedCurrent !== (tiers.get(h) || 'Normal')) {
            return { status: 409, body: { error: 'changed', current: tiers.get(h) || 'Normal' }, headers: {} };
        }

        tiers.set(h, body.tier);
        const row = playOrderRowsMock().find((r) => r.peerKey === h);
        return ok({ tier: body.tier, rank: row ? { position: row.position, count: row.count, tier: row.tier, reason: row.reason, label: row.label } : null });
    });
    route('GET', 'Peers/([^/]+)/Quality', (m) => {
        const h = decodeURIComponent(m[1]);
        if (!opt.quality) {
            return fail(404);
        }

        const s = servers.get(h);
        if (!s) {
            return fail(404, 'notFound');
        }

        const l = limitOf(h);
        const eff = effectiveOf(l);
        const row = opt.playOrder ? playOrderRowsMock().find((r) => r.peerKey === h) : null;
        return ok({
            limit: { maxClass: l.maxClass, maxMbps: l.maxMbps, setUtc: l.setUtc, origin: l.origin },
            effective: eff,
            costText: eff.limited ? 'Titles above this quality will be converted by ' + s.displayName + ". That uses their computer's power." : null,
            recommendation: recFor(h),
            dismissed: [],
            rank: row ? { position: row.position, count: row.count, tier: row.tier, reason: row.reason, label: row.label } : null
        });
    });
    let qualityChangedOnce = !!opt.qualityChanged;
    route('PUT', 'Peers/([^/]+)/QualityLimit', (m, body) => {
        const h = decodeURIComponent(m[1]);
        if (!opt.quality || !servers.has(h)) {
            return fail(404, opt.quality ? 'notFound' : undefined);
        }

        if (qualityChangedOnce) {
            qualityChangedOnce = false;
            limits.set(h, { maxClass: '720', maxMbps: null, setUtc: iso(now()), origin: 'Manual' });
        }

        const okClass = body && ['Off', '480', '720', '1080', '2160'].includes(body.maxClass);
        const okMbps = body && (body.maxMbps === null || body.maxMbps === undefined || (Number.isInteger(body.maxMbps) && body.maxMbps >= 1 && body.maxMbps <= 1000));
        if (!okClass || !okMbps || !body.expectedCurrent) {
            return { status: 400, body: { error: 'invalid', detail: 'maxClass' }, headers: {} };
        }

        const cur = limitOf(h);
        if (body.expectedCurrent.maxClass !== cur.maxClass || (body.expectedCurrent.maxMbps ?? null) !== (cur.maxMbps ?? null)) {
            return { status: 409, body: { error: 'changed', current: cur }, headers: {} };
        }

        const next = { maxClass: body.maxClass, maxMbps: body.maxMbps ?? null, setUtc: iso(now()), origin: body.fromRecommendation ? 'Recommendation' : 'Manual' };
        limits.set(h, next);
        return ok({ limit: next, effective: effectiveOf(next) });
    });
    let recStaleOnce = !!opt.recStale;
    route('POST', 'Peers/([^/]+)/Recommendations/Dismiss', (m, body) => {
        const h = decodeURIComponent(m[1]);
        if (!opt.quality || !servers.has(h)) {
            return fail(404);
        }

        if (!body || !['480', '720', '1080', '2160'].includes(body.suggestedClass)) {
            return fail(400, 'invalid');
        }

        const card = recFor(h);
        if (recStaleOnce || !card || card.suggestedClass !== body.suggestedClass) {
            recStaleOnce = false;
            return fail(409, 'stale');
        }

        dismissedRec.add(h);
        return ok({ dismissed: true });
    });
    route('GET', 'Peers/([^/]+)/Speed', (m, body, query) => {
        const s = servers.get(decodeURIComponent(m[1]));
        return s ? ok(speedFor(s, query.range || '24h')) : fail(404);
    });
    route('POST', 'Peers/([^/]+)/SpeedTest', (m, body) => {
        const key = (body && body.deep ? 'deep:' : 'test:') + m[1];
        if (cooldownUntil.has(key) && cooldownUntil.get(key) > now()) {
            return fail(429, 'rateLimited', { 'Retry-After': '45' });
        }

        cooldownUntil.set(key, now() + (body && body.deep ? 10 * MIN : MIN));
        return ok({}, 202);
    });

    route('POST', 'Peers/Probe', (m, body) => {
        if (opt.probeError === 'rateLimited') {
            return fail(429, 'rateLimited', { 'Retry-After': '300' });
        }

        const inviteOnly = ['inviteWrongServer', 'inviteLanHttp', 'inviteInvalid'].includes(opt.probeError);
        if (inviteOnly && body.invite) {
            // These come from the invite check: the answer is a 200 with ok:false and the fixed word, like the real probe.
            if (opt.probeError === 'inviteInvalid') {
                return ok({ ok: false, addressProblem: 'None', failure: 'inviteInvalid', probeHandle: null, serverName: null, version: null, serverIdPrefix: null, latencyMs: 0, address: null });
            }

            return fail(400, opt.probeError);
        }

        // The real probe answers 200 with ok:false and a fixed word (ProbeResult, K5).
        if (opt.probeError && !inviteOnly) {
            const problem = { blocked: 'Blocked', wrongNetwork: 'WrongNetwork', proxyNotAllowed: 'ProxyNotAllowed' }[opt.probeError];
            return ok({ ok: false, addressProblem: problem || 'None', failure: problem ? null : opt.probeError, probeHandle: null, serverName: null, version: null, serverIdPrefix: null, latencyMs: 0, address: null });
        }

        // Plain http to a home network address needs the acknowledgement on every check, like the real address policy.
        if (body.network === 'LAN' && /^http:/i.test(body.url || '') && !body.lanAck) {
            return ok({ ok: false, addressProblem: 'CleartextNotAcknowledged', failure: null, probeHandle: null, serverName: null, version: null, serverIdPrefix: null, latencyMs: 0, address: null });
        }

        // An invite never goes to a home network address over plain http (DESIGN 4.2 step 1).
        if (body.invite && body.network === 'LAN' && /^http:/i.test(body.url || '')) {
            return fail(400, 'inviteLanHttp');
        }

        const handle = 'probe-' + (++probeSeq);
        const host = (() => { try { return new URL(body.url).hostname; } catch { return 'server'; } })();
        const p = { ok: true, addressProblem: 'None', failure: null, probeHandle: handle, serverName: hostile ? XSS : host.split('.')[0].replace(/^./, (c) => c.toUpperCase()) + ' Jellyfin', version: '12.1.0', versionTested: !/untested/.test(body.url || ''), latencyMs: 41, serverIdPrefix: '3fa91c7e', address: body.url, alsoWatchesYou: /sam|hops/i.test(host) };
        if (opt.pingThrottled) {
            p.shoalPingThrottled = true;
        }

        // hasShoal and inviteExpiresUtc are optional on the real probe (pairing v2 section 11): opt.hasShoal 'absent' leaves the field out.
        if (opt.hasShoal !== 'absent') {
            p.hasShoal = typeof opt.hasShoal === 'boolean' ? opt.hasShoal : true;
        }

        if (body.invite && opt.inviteLeftMin) {
            p.inviteExpiresUtc = iso(now() + opt.inviteLeftMin * MIN);
        }

        const known = Array.from(servers.values()).find((x) => x.url === body.url);
        probes.set(handle, Object.assign({ url: body.url, network: body.network, createdAt: now(), peerHandle: known ? known.handle : null, invite: !!body.invite }, p));
        return ok(p);
    });
    route('POST', 'Pairing', (m, body) => {
        const probe = probes.get(body.probeHandle);
        if (!probe || probe.used || now() - probe.createdAt > 10 * MIN) {
            return fail(409, 'probeExpired');
        }

        if (session && !session.error && session.state !== 'done') {
            return fail(409, 'sessionActive');
        }

        if (opt.startError === 'rateLimited') {
            return fail(429, 'rateLimited', { 'Retry-After': '300' });
        }

        probe.used = true;
        const rec = probe.peerHandle ? servers.get(probe.peerHandle) : null;
        session = {
            createdAt: now(), method: body.method, state: body.method === 'qc' ? 'gettingCode' : 'waiting', error: null, displayName: body.displayName, url: probe.url, network: probe.network,
            serverIdPrefix: probe.serverIdPrefix, lifetimeMs: (opt.pairing === 'expired' ? 12000 : body.method === 'invite' ? 15 : 10) * (opt.pairing === 'expired' ? 1 : MIN),
            code: null, safetyCode: body.method === 'invite' ? 'K7QM2XA4' : 'K7QM', peerHandle: rec ? rec.handle : null, rePair: !!rec && rec.paired !== false
        };
        if (rec) {
            rec.pairingError = null;
            rec.status = 'Pairing';
        } else {
            session.peerHandle = 'h-new-' + (++probeSeq);
            addServer({ handle: session.peerHandle, displayName: body.displayName, url: probe.url, network: probe.network || 'Tailnet', status: 'Pairing', path: 'unknown', rttMs: null, mbps: null, titleCount: 0, paired: false });
        }

        return ok(pairingView(), 201);
    });
    route('GET', 'Pairing', () => {
        if (session && opt.endOnConfirm && session.state === 'done') {
            // The server closes a confirmed pairing by itself (N13): the page then finds no session.
            session = null;
            return ok(null, 204);
        }

        return session ? ok(pairingView()) : ok(null, 204);
    });
    route('POST', 'Pairing/Confirm', (m, body) => {
        if (!session) {
            return fail(404);
        }

        pairingView();
        if (session.state !== 'awaitingConfirm') {
            return fail(409, 'notWaiting');
        }

        if (!body.match) {
            failSession(session, 'codesDidNotMatch');
            return ok(pairingView());
        }

        session.state = 'done';
        finishPairing();
        return ok(pairingView());
    });
    route('DELETE', 'Pairing', () => {
        if (session && session.peerHandle) {
            const rec = servers.get(session.peerHandle);
            if (rec && rec.paired === false && rec.status === 'Pairing') {
                servers.delete(session.peerHandle);
            } else if (rec && rec.status === 'Pairing') {
                rec.status = session.prevStatus || 'Online';
            }
        }

        session = null;
        return ok(undefined, 204);
    });
    route('POST', 'Peers/([^/]+)/DismissPairing', (m) => {
        const h = decodeURIComponent(m[1]);
        const rec = servers.get(h);
        if (!rec || !rec.pairingError || (session && session.peerHandle === h && !session.error)) {
            return fail(409, 'nothingToDismiss');
        }

        if (rec.paired === false) {
            servers.delete(h);
            return ok({ removed: true });
        }

        const prev = (detail.get(h) || {}).prevStatus || 'Online';
        Object.assign(rec, { pairingError: null, status: prev, pairingPhase: undefined, pairingStoppedUtc: undefined });
        return ok({ restored: true, status: prev });
    });

    route('GET', 'Libraries', () => ok({ mappings: clone(mappings), users: clone(users), currentUserId: self.currentUserId }));
    route('PUT', 'Libraries/([^/]+)/Access', (m, body) => {
        const map = mappings.find((x) => x.mappingKey === decodeURIComponent(m[1]));
        if (!map) {
            return fail(404);
        }

        if (opt.accessFail) {
            return fail(500, 'server');
        }

        map.access = { mode: body.mode, selectedUserIds: body.selectedUserIds || [], includeRestricted: !!body.includeRestricted };
        map.drift = (map.drift || []).filter((d) => d.kind === 'managed');
        return ok({ access: clone(map.access) });
    });
    route('POST', 'Libraries/([^/]+)/Reconcile', (m) => {
        const map = mappings.find((x) => x.mappingKey === decodeURIComponent(m[1]));
        if (map) {
            map.drift = [];
        }

        return ok({});
    });

    /* ---------------- library layouts (PLAN v2.9 K44/K45; shapes of AdminLayoutsController) ---------------- */
    // The mockup's layout: Hops alone in two own libraries, Fernwood in Shoal Movies, Granite added into the main Movies library, Fernwood and Granite in Shoal TV Shows.
    const layout = { preset: opt.layoutPreset || 'Custom', defaultTargets: { movies: 'lk-shoalmov', tvshows: 'lk-shoaltv' } };
    const conn = (h) => (servers.has(h) ? [{ peerKey: h, name: servers.get(h).displayName }] : []);
    const layoutLibs = [];
    const addLib = (def) => layoutLibs.push(Object.assign({ placement: 'Shoal', legacy: false, access: { mode: 'Everyone', selectedUserIds: [], includeRestricted: false } }, def));
    if (servers.size && opt.scenario !== 'problems') {
        // opt.mainTarget (Pilot 16): the real shape. The Main row has its own Shoal name and records its target main library's id; the target's name
        // comes only from mainLibraries (opt.mainLibraries). Without it the row is named after its target, as the older mock did.
        addLib(opt.mainTarget
            ? { libKey: 'lk-main', name: X('Friends in Movies'), type: 'movies', placement: 'Main', jellyfinLibraryId: 'main-movies', connections: conn('h-gran'), titleCount: 612 }
            : { libKey: 'lk-main', name: X('Movies'), type: 'movies', placement: 'Main', connections: conn('h-gran'), titleCount: opt.layoutCounts === false ? undefined : 612 });
        if (opt.secondMain) {
            addLib({ libKey: 'lk-main2', name: X('Friends in Shows'), type: 'tvshows', placement: 'Main', jellyfinLibraryId: 'main-shows', connections: conn('h-hops'), titleCount: 20 });
        }
        addLib({ libKey: 'lk-hopsmov', name: X('Hops Movies'), type: 'movies', connections: conn('h-hops'), titleCount: opt.layoutCounts === false ? undefined : 1204 });
        addLib({ libKey: 'lk-shoalmov', name: X('Shoal Movies'), type: 'movies', connections: conn('h-fern'), access: { mode: 'Selected', selectedUserIds: [gid('u-rich'), gid('u-sam')], includeRestricted: false }, titleCount: opt.layoutCounts === false ? undefined : 386 });
        addLib({ libKey: 'lk-hopstv', name: X('Hops TV Shows'), type: 'tvshows', connections: conn('h-hops'), titleCount: opt.layoutCounts === false ? undefined : 86 });
        addLib({ libKey: 'lk-shoaltv', name: X('Shoal TV Shows'), type: 'tvshows', connections: conn('h-fern').concat(conn('h-gran')), titleCount: opt.layoutCounts === false ? undefined : 140 });
        if (opt.layoutLegacy) {
            addLib({ libKey: 'lk-legacy', name: 'Old Movies', type: 'movies', legacy: true, connections: [] });
        }
    }

    const layoutOrder = layoutLibs.map((l) => l.libKey);
    const hiddenFriendTitles = [gid('u-kit')];
    const idleJob = () => ({ running: false, moves: 0, movesDone: 0, steps: 0, stepsDone: 0, lostAccess: [] });
    let layoutJob = opt.layoutJob === 'running'
        ? { running: true, phase: 'Running', moves: 1, movesDone: 0, steps: 612, stepsDone: 380, lostAccess: [] }
        : opt.layoutJob === 'stopped' ? { running: false, phase: 'Running', moves: 1, movesDone: 0, steps: 612, stepsDone: 380, lostAccess: [] } : idleJob();
    if (opt.layoutJobCurrent && layoutJob.running) {
        layoutJob.current = { peerName: 'Granite', type: 'movies', toName: 'Movies' };
    }

    const jobStep = opt.layoutJobStep || 200;
    const startLayoutJob = (steps, lost) => {
        layoutJob = { running: true, phase: 'Planned', moves: 1, movesDone: 0, steps, stepsDone: 0, lostAccess: lost || [] };
    };
    const layoutView = () => ({
        layout: clone(layout), libraries: clone(layoutLibs.map((l) => Object.fromEntries(Object.entries(l).filter(([, v]) => v !== undefined)))), order: layoutOrder.slice(),
        hiddenFriendTitles: hiddenFriendTitles.slice(), job: clone(layoutJob), mainLibrariesSavingNfo: Array.isArray(opt.nfoMains) ? opt.nfoMains.slice() : [], shoalLibrariesNotLocked: Array.isArray(opt.notLocked) ? opt.notLocked.slice() : [], mainLibrariesMissing: Array.isArray(opt.mainsMissing) ? opt.mainsMissing.slice() : [],
        ...(opt.mainLibraries ? { mainLibraries: [{ id: 'main-movies', name: 'Movies', type: 'movies' }, { id: 'main-shows', name: 'Shows', type: 'tvshows' }] } : {})
    });
    const libByKey = (k) => layoutLibs.find((l) => l.libKey === k);
    const jobBusy = () => layoutJob.running || ['Planned', 'Running', 'Cleanup'].includes(layoutJob.phase);

    // opt.layoutsRoute === false: an older server without layouts (bare 404); Libraries & access then lists the per-friend mappings.
    route('GET', 'Layouts/Libraries', () => (opt.layoutsRoute === false ? fail(404) : ok(layoutView())));
    route('PUT', 'Layouts/Libraries', (m, body) => {
        const order = body && Array.isArray(body.order) ? body.order : null;
        if (!order || order.length !== layoutLibs.length || new Set(order).size !== order.length || order.some((k) => !libByKey(k))) {
            return fail(400, 'invalid');
        }

        layoutOrder.splice(0, layoutOrder.length, ...order);
        return ok({ order: layoutOrder.slice() });
    });
    route('POST', 'Layouts/Libraries', (m, body) => {
        if (!body || !body.name || !['movies', 'tvshows'].includes(body.type) || !['Shoal', 'Main'].includes(body.placement)) {
            return fail(400, 'invalid');
        }

        if (layoutLibs.some((l) => l.name.toLowerCase() === String(body.name).toLowerCase())) {
            return fail(409, 'nameInUse');
        }

        const libKey = 'lk-new' + layoutLibs.length;
        addLib({ libKey, name: body.name, type: body.type, placement: body.placement, connections: [], access: { mode: 'Selected', selectedUserIds: [self.currentUserId], includeRestricted: false } });
        layoutOrder.push(libKey);
        return ok({ libKey }, 201);
    });
    route('PUT', 'Layouts/Libraries/([^/]+)', (m, body) => {
        const lib = libByKey(decodeURIComponent(m[1]));
        if (!lib) {
            return fail(404, 'notFound');
        }

        // opt.renameFailsAfterAccess (Warden 12 L4): the access part was saved, then the rename failed.
        if (opt.renameFailsAfterAccess && body && typeof body.name === 'string') {
            return { status: 500, body: { error: 'failed', accessChanged: true, gaining: [], losing: [] }, headers: {} };
        }

        let losing = [];
        if (body && body.access) {
            const a = body.access;
            const admitted = (acc) => users.filter((u) => !u.isShareUser && (acc.mode === 'Everyone' ? !u.restricted || acc.includeRestricted : acc.selectedUserIds.includes(u.id))).map((u) => u.id);
            const before = admitted(lib.access);
            lib.access = { mode: a.mode, selectedUserIds: a.selectedUserIds || [], includeRestricted: !!a.includeRestricted };
            const after = admitted(lib.access);
            losing = before.filter((id) => !after.includes(id));
        }

        if (body && typeof body.name === 'string') {
            if (layoutLibs.some((l) => l !== lib && l.name.toLowerCase() === body.name.toLowerCase())) {
                return fail(409, 'nameInUse');
            }

            lib.name = body.name;
        }

        return ok({ library: clone(lib), gaining: [], losing });
    });
    route('DELETE', 'Layouts/Libraries/([^/]+)', (m) => {
        const lib = libByKey(decodeURIComponent(m[1]));
        if (!lib) {
            return fail(404, 'notFound');
        }

        if (lib.connections.length) {
            return fail(409, 'notEmpty');
        }

        if (lib.legacy) {
            return fail(409, 'legacy');
        }

        layoutLibs.splice(layoutLibs.indexOf(lib), 1);
        layoutOrder.splice(layoutOrder.indexOf(lib.libKey), 1);
        return ok({ removed: true });
    });
    const presetPlan = (preset) => {
        const moves = [];
        // opt.layoutMatches: the libraries already are what every preset would make (no moves).
        if (opt.layoutMatches) {
            return { preset, created: [], removed: [], moves, titlesCollapsing: 0, usersLosingAccess: [] };
        }

        const has = (h) => servers.has(h);
        if (preset === 'PerFriend') {
            if (has('h-fern')) {
                moves.push({ peerKey: 'h-fern', peerName: servers.get('h-fern').displayName, type: 'movies', from: ['Shoal Movies'], to: 'Fernwood Movies' });
                moves.push({ peerKey: 'h-fern', peerName: servers.get('h-fern').displayName, type: 'tvshows', from: ['Shoal TV Shows'], to: 'Fernwood TV Shows' });
            }

            if (has('h-gran')) {
                moves.push({ peerKey: 'h-gran', peerName: servers.get('h-gran').displayName, type: 'movies', from: ['Added into Movies'], to: 'Granite Movies' });
                moves.push({ peerKey: 'h-gran', peerName: servers.get('h-gran').displayName, type: 'tvshows', from: ['Shoal TV Shows'], to: 'Granite TV Shows' });
            }

            return { preset, created: [{ name: 'Fernwood Movies', type: 'movies' }, { name: 'Granite Movies', type: 'movies' }], removed: [{ libKey: 'lk-shoalmov', name: 'Shoal Movies' }], moves, titlesCollapsing: 0, usersLosingAccess: [] };
        }

        if (preset === 'AllMixed') {
            if (has('h-hops')) {
                moves.push({ peerKey: 'h-hops', peerName: servers.get('h-hops').displayName, type: 'movies', from: ['Hops Movies'], to: 'Shoal Movies' });
                moves.push({ peerKey: 'h-hops', peerName: servers.get('h-hops').displayName, type: 'tvshows', from: ['Hops TV Shows'], to: 'Shoal TV Shows' });
            }

            if (has('h-gran')) {
                moves.push({ peerKey: 'h-gran', peerName: servers.get('h-gran').displayName, type: 'movies', from: ['Added into Movies'], to: 'Shoal Movies' });
            }

            return { preset, created: [], removed: [{ libKey: 'lk-hopsmov', name: 'Hops Movies' }, { libKey: 'lk-hopstv', name: 'Hops TV Shows' }], moves, titlesCollapsing: opt.layoutCollapse || 41, usersLosingAccess: [gid('u-alex')] };
        }

        return { preset, created: [], removed: [], moves: [], titlesCollapsing: 0, usersLosingAccess: [] };
    };
    // Preview answers a planHash that Apply must send back (Warden 10 L3): missing = 400 invalid, a plan that changed since = 409 planChanged.
    // opt.planChangedOnce: the first Apply finds the plan changed (a friend was added meanwhile).
    let planVersion = 1;
    let planChangedOnce = !!opt.planChangedOnce;
    const PRESETS = ['AllMixed', 'PerFriend', 'CombineIntoMine', 'Custom'];
    const hashOf = (preset, targets) => 'ph-' + preset + '-' + planVersion + (preset === 'CombineIntoMine' ? '-' + JSON.stringify(targets || {}) : '');
    /*
     * Combine into my libraries (owner decision; Kiro's PresetPreview.Targets shape): the admin's own libraries per type as candidates.
     * opt.combine: 'one' (default: one Movies and one TV Shows library, chosen for the admin), 'many' (two movie libraries: the admin picks; Movies saves NFO
     * files), 'noTv' (no TV library of their own). Apply answers targetRequired while a type with candidates has none chosen.
     */
    const COMBINE_IDS = { movies: ['0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b', '1a2b3c4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d'], tvshows: ['2b3c4d5e-6f7a-4b2c-8d3e-4f5a6b7c8d9e'] };
    function combineTargets(sent) {
        const mode = opt.combine || 'one';
        const movies = mode === 'many'
            ? [{ id: COMBINE_IDS.movies[0], name: X('Movies'), savesNfo: true }, { id: COMBINE_IDS.movies[1], name: 'Kids Movies', savesNfo: false }]
            : [{ id: COMBINE_IDS.movies[0], name: X('Movies'), savesNfo: false }];
        const tv = mode === 'noTv' ? [] : [{ id: COMBINE_IDS.tvshows[0], name: 'TV Shows', savesNfo: false }];
        return [['movies', movies], ['tvshows', tv]].map(([type, candidates]) => {
            const want = sent && sent[type];
            const ok = candidates.find((c) => c.id === want);
            const chosen = ok ? ok.id : candidates.length === 1 ? candidates[0].id : null;
            return { type, chosen, candidates, rejected: !!want && !ok };
        });
    }

    function combinePlan(sent) {
        const targets = combineTargets(sent);
        const moves = [];
        const nameOf = (c) => c.candidates.find((x) => x.id === c.chosen).name;
        for (const c of targets.filter((x) => x.chosen)) {
            for (const h of ['h-hops', 'h-fern', 'h-gran'].filter((x) => servers.has(x))) {
                moves.push({ peerKey: h, peerName: servers.get(h).displayName, type: c.type, from: [servers.get(h).displayName + (c.type === 'movies' ? ' Movies' : ' TV Shows')], to: 'Added into ' + nameOf(c) });
            }
        }

        return {
            preset: 'CombineIntoMine', created: [], removed: [], moves, titlesCollapsing: 12, usersLosingAccess: [], usersGainingAccess: [], restrictedGainingAccess: [],
            targets, needsTargets: targets.some((c) => c.candidates.length && !c.chosen),
            targetsSavingNfo: targets.filter((c) => c.chosen).flatMap((c) => c.candidates.filter((x) => x.id === c.chosen && x.savesNfo).map((x) => x.name)),
            targetsRewritingTitles: targets.filter((c) => c.chosen).flatMap((c) => c.candidates.filter((x) => x.id === c.chosen && (x.savesNfo || x.fetchesMetadata)).map((x) => x.name)),
            shoalLibrariesNotLocked: Array.isArray(opt.notLocked) ? opt.notLocked.slice() : []
        };
    }

    const planOf = (preset, targets) => (preset === 'CombineIntoMine' ? combinePlan(targets) : presetPlan(preset));
    route('POST', 'Layouts/Preview', (m, body) => (body && PRESETS.includes(body.preset) ? ok(Object.assign(planOf(body.preset, body.targets), { planHash: hashOf(body.preset, body.targets) })) : fail(400, 'invalid')));
    route('POST', 'Layouts/Apply', (m, body) => {
        if (!body || !PRESETS.includes(body.preset)) {
            return fail(400, 'invalid');
        }

        if (body.preset === 'CombineIntoMine' && combinePlan(body.targets).needsTargets) {
            return fail(409, 'targetRequired');
        }

        if (typeof body.planHash !== 'string' || !body.planHash) {
            return fail(400, 'invalid');
        }

        if (planChangedOnce) {
            planChangedOnce = false;
            planVersion += 1;
        }

        if (body.planHash !== hashOf(body.preset, body.targets)) {
            return fail(409, 'planChanged');
        }

        if (jobBusy()) {
            return fail(409, 'busy');
        }

        const plan = planOf(body.preset, body.targets);
        layout.preset = body.preset;
        if (!plan.moves.length) {
            // Nothing to move (Custom, or already that layout): no job starts, the preset is remembered.
            return ok({ job: { running: false, phase: 'Done', steps: 0, stepsDone: 0 } }, 202);
        }

        startLayoutJob(plan.moves.length * 100, plan.usersLosingAccess);
        return ok({ job: clone(layoutJob) }, 202);
    });
    route('PUT', 'Connections/([^/]+)/Library', (m, body) => {
        const h = decodeURIComponent(m[1]);
        if (!servers.has(h)) {
            return fail(404, 'notFound');
        }

        if (!body || !['movies', 'tvshows'].includes(body.type)) {
            return fail(400, 'invalid');
        }

        if (jobBusy()) {
            return fail(409, 'busy');
        }

        let target = body.libKey ? libByKey(body.libKey) : null;
        if (body.libKey && (!target || target.type !== body.type)) {
            return fail(404, 'notFound');
        }

        if (!body.libKey) {
            const n = body.newLibrary;
            if (!n || !n.name || !['Shoal', 'Main'].includes(n.placement)) {
                return fail(400, 'invalid');
            }

            if (layoutLibs.some((l) => l.name.toLowerCase() === String(n.name).toLowerCase())) {
                return fail(409, 'createFailed');
            }

            const libKey = 'lk-new' + layoutLibs.length;
            addLib({ libKey, name: n.name, type: body.type, placement: n.placement, connections: [] });
            layoutOrder.push(libKey);
            target = libByKey(libKey);
        }

        // The state switch happens at once (the job then moves the files): the connection now feeds the target.
        const source = layoutLibs.find((l) => l.type === body.type && l.connections.some((c) => c.peerKey === h));
        const entry = source ? source.connections.find((c) => c.peerKey === h) : { peerKey: h, name: servers.get(h).displayName };
        if (source) {
            source.connections = source.connections.filter((c) => c.peerKey !== h);
        }

        target.connections.push(entry);
        startLayoutJob(source && typeof source.titleCount === 'number' ? source.titleCount : 300, []);
        return ok({ job: clone(layoutJob) }, 202);
    });
    route('GET', 'Layouts/Job', () => {
        if (layoutJob.running) {
            layoutJob.phase = 'Running';
            layoutJob.stepsDone = Math.min(layoutJob.steps, layoutJob.stepsDone + jobStep);
            if (layoutJob.stepsDone >= layoutJob.steps) {
                Object.assign(layoutJob, { running: false, phase: 'Done', movesDone: layoutJob.moves });
            }
        }

        return ok(clone(layoutJob));
    });
    route('PUT', 'Users/([^/]+)/FriendTitles', (m, body) => {
        const id = decodeURIComponent(m[1]);
        if (!users.some((u) => u.id === id && !u.isShareUser)) {
            return fail(404, 'notFound');
        }

        if (!body || typeof body.hidden !== 'boolean') {
            return fail(400, 'invalid');
        }

        const i = hiddenFriendTitles.indexOf(id);
        if (body.hidden && i < 0) {
            hiddenFriendTitles.push(id);
        } else if (!body.hidden && i >= 0) {
            hiddenFriendTitles.splice(i, 1);
        }

        return ok({ hidden: body.hidden });
    });
    // PUT Layouts/Default (bb89634): { preset, defaultTargets } -> { preset, defaultTargets }. opt.layoutDefaultRoute === false = an older server (bare 404).
    route('PUT', 'Layouts/Default', (m, body) => {
        if (opt.layoutDefaultRoute === false) {
            return fail(404);
        }

        if (!body || !['AllMixed', 'PerFriend', 'Custom'].includes(body.preset)) {
            return fail(400, 'invalid');
        }

        layout.preset = body.preset;
        if (body.defaultTargets && typeof body.defaultTargets === 'object') {
            layout.defaultTargets = Object.assign({}, layout.defaultTargets, body.defaultTargets);
        }

        return ok({ preset: layout.preset, defaultTargets: clone(layout.defaultTargets || {}) });
    });
    // K49 (af33bc8): GET -> the card; POST -> { sent, explainer }. Problems come back inside the card (installed:false, error refused|failed).
    // opt.inbox: 'ok' (not posted), 'posted', 'missing' (posted, 2 newer accounts lack it), 'noMoonfin', 'refused'; absent = an older server (bare 404).
    const inbox = { posted: ['posted', 'missing'].includes(opt.inbox), postedUtc: ['posted', 'missing'].includes(opt.inbox) ? iso(t0 - 2 * HOUR) : null, missing: opt.inbox === 'missing' ? 2 : 0 };
    const INBOX_TITLE = 'Why might a title not work?';
    const INBOX_BODY = "**Why might a title not work?** Some titles come from a friend's server. If that server is turned off or its internet is slow, the title may buffer or not play. Quality may be lowered to keep it smooth. Subtitles may be different from what you expect. If something won't play, try again later.";
    const inboxCardOf = () => ({
        installed: opt.inbox !== 'noMoonfin',
        posted: opt.inbox !== 'noMoonfin' && opt.inbox !== 'refused' && inbox.posted,
        postedUtc: inbox.postedUtc,
        recipients: inbox.posted ? 3 : 0,
        missingRecipients: inbox.posted ? inbox.missing : 3,
        title: INBOX_TITLE,
        body: INBOX_BODY,
        buttonNote: "Moonfin shows the inbox button only when it is turned on in Moonfin's settings.",
        fallbackNote: opt.inbox === 'noMoonfin' ? "Moonbase isn't installed. Each friend's title still says where it comes from." : null,
        error: opt.inbox === 'refused' ? 'refused' : null
    });
    route('GET', 'Inbox/Explainer', () => (opt.inbox ? ok(inboxCardOf()) : fail(404)));
    route('POST', 'Inbox/Explainer', () => {
        if (!opt.inbox) {
            return fail(404);
        }

        if (opt.inbox === 'noMoonfin' || opt.inbox === 'refused' || (inbox.posted && inbox.missing === 0)) {
            return ok({ sent: false, explainer: inboxCardOf() });
        }

        inbox.posted = true;
        inbox.missing = 0;
        inbox.postedUtc = iso(now());
        return ok({ sent: true, explainer: inboxCardOf() });
    });

    route('GET', 'Settings', () => ok(clone(settings)));
    route('PUT', 'Settings', (m, body) => {
        // The folder check of the real endpoint: inside a library and unwritable are refused, a network share needs the acknowledgement.
        const root = String(body.stubRoot || '');
        if (root !== settings.stubRoot) {
            const check = /\/media\//.test(root) ? 'insideLibrary' : /^\/mnt/.test(root) ? 'network' : /readonly/.test(root) ? 'notWritable' : 'ok';
            if (check !== 'ok' && !(check === 'network' && body.stubRootNetworkAck)) {
                return { status: 400, body: { error: 'stubRoot', detail: check }, headers: {} };
            }

            settings.stubRootCheck = check;
        }

        if (body.trickplayCacheGb !== undefined && !(Number.isInteger(body.trickplayCacheGb) && body.trickplayCacheGb >= 1 && body.trickplayCacheGb <= 20)) {
            return fail(400, 'invalid');
        }

        const { stubRootNetworkAck, ...rest } = body;
        const turnedOn = rest.reencodeImages === true && settings.reencodeImages === false;
        Object.assign(settings, rest);
        if (settings.reencodeImages === false) {
            settings.imageSandbox = { state: 'off', limits: settings.imageSandbox.limits, reason: null, rejected: settings.imageSandbox.rejected };
        } else if (turnedOn) {
            settings.imageSandbox = { state: opt.sandbox === 'unavailable' ? 'unavailable' : 'active', limits: opt.sandbox === 'partial' || opt.sandbox === 'unavailable' ? 'partial' : 'full', reason: opt.sandbox === 'unavailable' ? (opt.sandboxReason || 'ffmpegMissing') : null, rejected: settings.imageSandbox.rejected };
        }

        return ok(clone(settings));
    });
    route('DELETE', 'TrickplayCache', () => {
        const freed = settings.trickplayCacheBytes;
        settings.trickplayCacheBytes = 0;
        return ok({ freedBytes: freed });
    });
    route('GET', 'Hardening', () => ok(clone(hardening)));
    route('PUT', 'Hardening', (m, body) => {
        hardening.mode = body.mode;
        hardening.sinceUtc = iso(now());
        return ok(clone(hardening));
    });
    route('GET', 'State', () => ok(clone(stateInfo)));
    route('POST', 'State/Restore', () => {
        banner.mode = 'Normal';
        banner.reason = null;
        stateInfo.mode = 'Normal';
        banner.restoredFrom = { file: 'state.json.bak', atUtc: iso(now() - HOUR), needsConfirm: true };
        return ok({});
    });
    route('POST', 'State/ConfirmRestore', () => {
        if (!banner.restoredFrom || !banner.restoredFrom.needsConfirm) {
            return fail(409, 'nothingToConfirm');
        }

        banner.restoredFrom = null;
        return ok(undefined, 204);
    });
    route('POST', 'State/StartOver', (m, body) => {
        if (banner.mode !== 'Recovery') {
            return fail(409, 'notInRecovery');
        }

        if (!body || body.confirmName !== 'start over') {
            return fail(400, 'confirmName');
        }

        banner.mode = 'Normal';
        banner.reason = null;
        stateInfo.mode = 'Normal';
        servers.clear();
        mappings.length = 0;
        shares.clear();
        return ok({});
    });
    route('DELETE', 'All', (m, body) => {
        if (!body || body.confirmName !== 'delete everything') {
            return fail(400, 'confirmName');
        }

        servers.clear();
        mappings.length = 0;
        shares.clear();
        recentlyShared.length = 0;
        return ok({});
    });

    // Shoal's guess for the address to send (pairing v2 section 11). opt.reach 'none' answers 404 like a server without the route.
    route('GET', 'Shares/Reach', () => (opt.reach === 'none' ? fail(404) : ok({ tailscale: { dnsName: 'rich-jellyfin.tail1234.ts.net', ip: '100.101.102.103' }, https: { url: 'https://movies.example.com' }, lan: [{ url: 'http://192.168.1.20:8096' }] })));
    route('GET', 'Shares', () => ok({
        shares: Array.from(shares.values()).map((f) => ({
            handle: f.handle, friendName: f.friendName, status: f.status, libraryIds: f.libraryIds,
            libraryNames: f.libraryIds.map((id) => (ownLibraries.find((l) => l.id === id) || { name: id }).name),
            maxBitrateMbps: f.maxBitrateMbps, maxConcurrentTranscodes: f.maxConcurrentTranscodes, unexpectedDeviceCount: f.unexpectedDeviceCount
        })),
        availableLibraries: clone(ownLibraries)
    }));
    route('POST', 'Shares', (m, body) => {
        const handle = 'f-new-' + (++probeSeq);
        addShare({ handle, friendName: body.friendName, libraryIds: body.libraryIds, maxBitrateMbps: body.maxBitrateMbps, maxConcurrentTranscodes: body.maxConcurrentTranscodes || 4, devices: [], lastSeenUtc: null, status: 'Active' });
        return ok({ handle, friendName: body.friendName, libraryIds: body.libraryIds }, 201);
    });
    route('GET', 'Shares/([^/]+)', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (!f) {
            return fail(404);
        }

        const approval = f.approval && f.approval.state === 'pending' ? f.approval : null;
        return ok({
            id: f.handle, handle: f.handle, friendName: f.friendName, status: f.status, shareUserName: f.shareUserName, sinceUtc: f.sinceUtc, libraryIds: f.libraryIds,
            maxBitrateMbps: f.maxBitrateMbps, maxConcurrentTranscodes: f.maxConcurrentTranscodes, invite: clone(f.invite), approval: clone(approval), deviceCount: f.devices.length,
            devices: clone(f.devices), availableLibraries: clone(ownLibraries), hardeningMode: hardening.mode, reach: f.reach || 'tailnet',
            lastRotatedUtc: f.lastRotatedUtc, rotationCount: f.rotationCount, rotationFailed: !!f.rotationFailed
        });
    });
    route('PUT', 'Shares/([^/]+)', (m, body) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (f) {
            Object.assign(f, { libraryIds: body.libraryIds, maxBitrateMbps: body.maxBitrateMbps, maxConcurrentTranscodes: body.maxConcurrentTranscodes });
        }

        return ok({});
    });
    route('DELETE', 'Shares/([^/]+)', (m, body) => {
        const target = shares.get(decodeURIComponent(m[1]));
        if (!target) {
            return fail(404);
        }

        if (!body || body.confirmName !== target.friendName) {
            return fail(400, 'confirmName');
        }

        shares.delete(decodeURIComponent(m[1]));
        const f = { disabled: true, signedOut: 2, remaining: opt.revokeLeftover ? 1 : 0, deleted: !opt.revokeLeftover };
        return ok(f);
    });
    route('POST', 'Shares/([^/]+)/(Pause|Resume)', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (f) {
            f.status = m[2] === 'Pause' ? 'Paused' : 'Active';
        }

        return ok({});
    });
    route('POST', 'Shares/([^/]+)/Invites', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (!f) {
            return fail(404);
        }

        f.invite = { state: 'active', expiresUtc: iso(now() + 15 * MIN), createdAt: now() };
        f.approval = { state: 'none' };
        return ok({ invite: 'shoal1-3fa91c7e-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P', expiresUtc: f.invite.expiresUtc }, 201);
    });
    route('DELETE', 'Shares/([^/]+)/Invites/current', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (f) {
            f.invite = { state: 'none' };
        }

        return ok({});
    });
    route('GET', 'Shares/([^/]+)/Approval', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (!f) {
            return fail(404);
        }

        // A friend's server redeems the invite 7 s after it was created (harness only).
        if (f.invite && f.invite.state === 'active' && f.invite.createdAt && now() - f.invite.createdAt > 7000 && f.approval.state === 'none' && opt.shareFlow !== 'wait') {
            f.approval = { state: 'pending', homeName: hostile ? XSS : 'Sam’s Jellyfin', homeServerIdPrefix: '3fa91c7e', addressClass: 'tailnet', triesLeft: 3, restarted: opt.restarted === true };
        }

        return ok(clone(f.approval));
    });
    route('POST', 'Shares/([^/]+)/Approval', (m, body) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (!f) {
            return fail(404);
        }

        if (f.approval.state !== 'pending') {
            return fail(409, 'nothingPending');
        }

        if (!body.approve) {
            f.approval = { state: 'denied' };
            return ok({ state: 'denied' });
        }

        // The friend's server judges the code; the page never has it (DESIGN 4.1b step 3).
        const typed = String(body.sas || '').toUpperCase().replace(/[\s-]/g, '').replace(/[IL]/g, '1').replace(/O/g, '0');
        if (typed !== 'K7QM2XA4') {
            const triesLeft = f.approval.triesLeft - 1;
            if (triesLeft <= 0) {
                f.approval = { state: 'cancelledWrongCode' };
                f.invite = { state: 'none' };
                return ok({ match: false, triesLeft: 0, state: 'cancelledWrongCode' });
            }

            f.approval = Object.assign({}, f.approval, { triesLeft });
            return ok({ match: false, triesLeft });
        }

        f.approval = Object.assign({}, f.approval, { state: 'approved' });
        const home = f.approval.homeName;
        setTimeout(() => {
            if (opt.shareFlow === 'uncollected') {
                f.approval = { state: 'uncollected', homeName: home };
                return;
            }

            f.approval = Object.assign({}, f.approval, { state: 'collected' });
            f.invite = { state: 'none' };
            f.devices = [{ deviceHandle: 'd-n', name: 'Shoal: ' + home + ' (K7QM)', app: 'Shoal', version: '0.1.0', firstSeenUtc: iso(now()), lastSeenUtc: iso(now()), expected: true, signedOut: false }];
        }, opt.collectMs === undefined ? 2500 : opt.collectMs);
        return ok({ match: true, state: 'approved' });
    });
    route('DELETE', 'Shares/([^/]+)/Devices/([^/]+)', (m) => {
        const f = shares.get(decodeURIComponent(m[1]));
        if (f) {
            f.devices = f.devices.filter((d) => d.deviceHandle !== decodeURIComponent(m[2]));
            f.unexpectedDeviceCount = f.devices.filter((d) => !d.expected).length;
        }

        return ok({});
    });

    /* ---------------- entry point ---------------- */
    async function handle(method, rawPath, body) {
        calls.push({ method, path: rawPath, body: body === undefined ? undefined : clone(body) });
        if (opt.delayMs || opt.scenario === 'slow') {
            await new Promise((r) => setTimeout(r, opt.scenario === 'slow' ? 1500 : opt.delayMs));
        }

        const [path, qs] = rawPath.split('?');
        const query = Object.fromEntries(new URLSearchParams(qs || ''));
        if (opt.scenario === 'error' && !/^Pairing/.test(path)) {
            return fail(500, 'server');
        }

        if (opt.failPaths && opt.failPaths.some((re) => re.test(method + ' ' + path))) {
            return fail(500, 'server');
        }

        // A refusal word for one route ({ re, status, error }): the layout routes answer their errors this way.
        const refusal = (opt.refuse || []).find((r) => r.re.test(method + ' ' + path));
        if (refusal) {
            return fail(refusal.status || 409, refusal.error);
        }

        for (const r of routes) {
            if (r.method !== method) {
                continue;
            }

            const m = r.re.exec(path);
            if (m) {
                return r.fn(m, body, query);
            }
        }

        return fail(404);
    }

    return {
        handle,
        calls,
        options: opt,
        set(patch) {
            Object.assign(opt, patch);
        },
        getSession: () => session,
        stores: { servers, mappings, shares, settings, hardening, users, layoutLibs, layoutOrder, hiddenFriendTitles, layout, getJob: () => layoutJob }
    };
}
