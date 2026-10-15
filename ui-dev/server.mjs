// Static server for the development harness. Serves the real plugin files under the URLs Jellyfin uses
// (configurationpage?name=Shoal, Shoal.js, Shoal.css), the harness under /ui-dev/, and generated art.
// Local only: binds to 127.0.0.1.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const cfgDir = path.resolve(here, '../src/Jellyfin.Plugin.Shoal/Configuration');
const CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; connect-src 'self'; font-src 'self'; base-uri 'self'; form-action 'self'";

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.svg': 'image/svg+xml' };
const PAGE_FILES = { Shoal: 'shoal.html', 'Shoal.js': 'shoal.js', 'Shoal.css': 'shoal.css' };

function hash(s) {
    let h = 2166136261;
    for (const c of String(s)) {
        h = Math.imul(h ^ c.charCodeAt(0), 16777619) >>> 0;
    }

    return h;
}

/** Stand-in for a movie backdrop or poster: layered hills under a sky, colored by the item id. */
function artSvg(id, type) {
    const h = hash(id);
    const hue = h % 360;
    const w = type === 'Primary' ? 300 : 800;
    const hgt = type === 'Primary' ? 450 : 450;
    const sky1 = `hsl(${hue} 55% 38%)`;
    const sky2 = `hsl(${(hue + 40) % 360} 60% 62%)`;
    const hill = (k, y, amp) => {
        let d = `M0 ${hgt}`;
        for (let x = 0; x <= w; x += w / 8) {
            const yy = y + Math.sin((x / w) * 6 + (h >> k) % 7) * amp;
            d += ` L${x.toFixed(0)} ${yy.toFixed(0)}`;
        }

        return d + ` L${w} ${hgt} Z`;
    };

    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${hgt}" width="${w}" height="${hgt}">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${sky1}"/><stop offset="1" stop-color="${sky2}"/></linearGradient></defs>
  <rect width="${w}" height="${hgt}" fill="url(#g)"/>
  <circle cx="${w * 0.72}" cy="${hgt * 0.3}" r="${hgt * 0.1}" fill="hsl(${(hue + 60) % 360} 90% 85%)" opacity=".9"/>
  <path d="${hill(2, hgt * 0.62, 18)}" fill="hsl(${hue} 35% 26%)"/>
  <path d="${hill(4, hgt * 0.74, 14)}" fill="hsl(${hue} 30% 18%)"/>
  <path d="${hill(6, hgt * 0.86, 10)}" fill="hsl(${hue} 25% 11%)"/>
</svg>`;
}

function avatarSvg(id) {
    const hue = hash(id) % 360;
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" fill="hsl(${hue} 45% 40%)"/><circle cx="32" cy="25" r="11" fill="hsl(${hue} 60% 85%)"/><path d="M10 64c2-16 10-22 22-22s20 6 22 22z" fill="hsl(${hue} 60% 85%)"/></svg>`;
}

function send(res, status, type, body, extra) {
    res.writeHead(status, Object.assign({ 'Content-Type': type, 'Cache-Control': 'no-store' }, extra || {}));
    res.end(body);
}

export function createHarnessServer() {
    return http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const p = decodeURIComponent(url.pathname);

    if (p === '/' || p === '/index.html') {
        // ?csp=1 serves the page under script-src 'self'; style-src 'self' 'unsafe-inline' (ui-spec 15 F2).
        return send(res, 200, TYPES['.html'], fs.readFileSync(path.join(here, 'harness.html')), url.searchParams.get('csp') ? { 'Content-Security-Policy': CSP } : undefined);
    }

    if (p === '/configurationpage') {
        const file = PAGE_FILES[url.searchParams.get('name')];
        if (!file) {
            return send(res, 404, 'text/plain', 'not found');
        }

        const full = path.join(cfgDir, file);
        if (!fs.existsSync(full)) {
            return send(res, 404, 'text/plain', 'not found: ' + file);
        }

        return send(res, 200, TYPES[path.extname(file)], fs.readFileSync(full));
    }

    let m = /^\/Items\/([^/]+)\/Images\/([^/]+)$/.exec(p);
    if (m) {
        return send(res, 200, TYPES['.svg'], artSvg(m[1], m[2]));
    }

    m = /^\/Users\/([^/]+)\/Images\/Primary$/.exec(p);
    if (m) {
        // User "u-rich" has a picture, others fall back to the initial (404).
        return m[1] === 'u-rich' ? send(res, 200, TYPES['.svg'], avatarSvg(m[1])) : send(res, 404, 'text/plain', 'none');
    }

    if (p.startsWith('/ui-dev/')) {
        const full = path.join(here, p.slice('/ui-dev/'.length));
        if (full.startsWith(here) && fs.existsSync(full) && fs.statSync(full).isFile()) {
            return send(res, 200, TYPES[path.extname(full)] || 'application/octet-stream', fs.readFileSync(full));
        }
    }

    return send(res, 404, 'text/plain', 'not found');
});

}

export function startHarness(port) {
    const server = createHarnessServer();
    return new Promise((resolve) => {
        server.listen(port || 0, '127.0.0.1', () => resolve({ server, port: server.address().port }));
    });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const { port } = await startHarness(Number(process.env.PORT || 5174));
    console.log(`Shoal harness: http://127.0.0.1:${port}/?scenario=default&theme=dark`);
}
