/*
 * A stand-in for Jellyfin's ApiClient, backed by the mock server. Only the members shoal.js uses exist:
 * getUrl, ajax, getImageUrl, getUserImageUrl, serverId, getCurrentUserId.
 */

const API_BASE = '/mockapi/Shoal/Admin/';

function qs(params) {
    const parts = [];
    for (const [k, v] of Object.entries(params || {})) {
        if (v !== undefined && v !== null && v !== '') {
            parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
        }
    }

    return parts.length ? '?' + parts.join('&') : '';
}

/**
 * The real server writes absent optional values as null (ResponseWriter does not omit them). nullify() makes the mock do the same,
 * so a test can prove the page treats null like absent.
 */
const OPTIONAL_KEYS = [
    'latest', 'lastSync', 'offlineSinceUtc', 'nextCheckUtc', 'rttMs', 'mbps', 'jitterMs', 'lossPct', 'derpRegion', 'pairingError', 'pairingPhase',
    'pairingStoppedUtc', 'pairingMethod', 'pairingSession', 'pairedUtc', 'deviceLock', 'sessionLimit', 'accountNotes', 'serverIdPrefix', 'reason',
    'restoredFrom', 'guardLastGood', 'secretsReason', 'keySource', 'homeName', 'imageCredit', 'updatedUtc', 'proxyUrl', 'maxBitrateMbps', 'lastSeenUtc',
    'backdropTag', 'primaryTag', 'year', 'runtimeMin', 'expiresUtc', 'note', 'error', 'detail', 'phase', 'code', 'safetyCode', 'peerHandle', 'sinceUtc'
];

export function nullify(value) {
    if (Array.isArray(value)) {
        return value.map(nullify);
    }

    if (value && typeof value === 'object') {
        const out = {};
        for (const [k, v] of Object.entries(value)) {
            out[k] = nullify(v);
        }

        for (const key of OPTIONAL_KEYS) {
            if (!(key in out) && !Object.keys(out).some((k) => k === 'servers' || k === 'shares')) {
                out[key] = null;
            }
        }

        return out;
    }

    return value;
}

export function createMockClient(mock, opts) {
    const o = Object.assign({ origin: '' }, opts || {});
    return {
        getUrl(path, params) {
            if (String(path).startsWith('Shoal/Admin/')) {
                return '/mockapi/' + path;
            }

            return o.origin + '/' + path + qs(params);
        },
        ajax(request) {
            const url = String(request.url);
            const at = url.indexOf(API_BASE);
            if (at < 0) {
                return Promise.reject({ status: 404, headers: { get: () => null }, json: async () => ({}) });
            }

            const path = url.slice(at + API_BASE.length);
            const body = request.data ? JSON.parse(request.data) : undefined;
            return mock.handle(request.type || 'GET', path, body).then((res) => {
                if (res.status >= 200 && res.status < 300) {
                    // Like the real client: a 204 has no body, and dataType 'text' hands back an empty string.
                    return request.dataType === 'text' || res.status === 204 ? '' : o.nulls ? nullify(res.body) : res.body;
                }

                throw {
                    status: res.status,
                    headers: { get: (name) => (res.headers && res.headers[name]) || null },
                    json: async () => res.body
                };
            });
        },
        getImageUrl(itemId, options) {
            const p = options || {};
            return o.origin + '/Items/' + encodeURIComponent(itemId) + '/Images/' + encodeURIComponent(p.type || 'Primary') + qs({ tag: p.tag, maxWidth: p.maxWidth, quality: p.quality });
        },
        getUserImageUrl(userId, options) {
            const p = options || {};
            return o.origin + '/Users/' + encodeURIComponent(userId) + '/Images/Primary' + qs({ tag: p.tag, maxWidth: p.maxWidth });
        },
        serverId: () => 'srv-home',
        getCurrentUserId: () => 'u-rich'
    };
}
