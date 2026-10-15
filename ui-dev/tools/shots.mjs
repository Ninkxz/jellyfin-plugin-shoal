// Screenshots of harness pages with the system Chrome.
// Usage: node tools/shots.mjs "<query>" [width] [height|full] [out.png] [--click "<selector>"]...
//   node tools/shots.mjs "scenario=default&theme=dark&view=overview" 1440 full
// Output goes to ui-dev/shots/ (git-ignored).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(here, '../shots');
fs.mkdirSync(outDir, { recursive: true });

const [query = 'scenario=default', width = '1440', height = 'full', out = ''] = process.argv.slice(2);
const actions = [];
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--click' || argv[i] === '--wait' || argv[i] === '--eval' || argv[i] === '--key') {
        actions.push([argv[i], argv[i + 1]]);
        i += 1;
    }
}

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const w = Number(width);
const ctx = await browser.newContext({ viewport: { width: w, height: height === 'full' ? 900 : Number(height) }, deviceScaleFactor: 1, reducedMotion: query.includes('reduced=1') ? 'reduce' : 'no-preference' });
const page = await ctx.newPage();
const errors = [];
page.on('console', (m) => {
    if (m.type() === 'error' || m.type() === 'warning') {
        errors.push(m.type() + ': ' + m.text());
    }
});
page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
await page.goto('http://127.0.0.1:5174/?' + query, { waitUntil: 'load' });
await page.waitForTimeout(1200);
for (const [kind, arg] of actions) {
    if (kind === '--click') {
        await page.click(arg);
        await page.waitForTimeout(400);
    } else if (kind === '--wait') {
        await page.waitForTimeout(Number(arg));
    } else if (kind === '--key') {
        await page.keyboard.press(arg);
        await page.waitForTimeout(200);
    } else if (kind === '--eval') {
        const r = await page.evaluate(arg);
        if (r !== undefined) {
            console.log(JSON.stringify(r));
        }
    }
}

const name = out || (query.replace(/[^a-z0-9]+/gi, '_').slice(0, 80) + '_' + w + '.png');
const file = path.join(outDir, name);
await page.screenshot({ path: file, fullPage: height === 'full' });
console.log(file);
if (errors.length) {
    console.log(errors.join('\n'));
}

await browser.close();
