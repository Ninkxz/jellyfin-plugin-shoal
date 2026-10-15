# Lumen review 01: Shoal admin UI (Kiro 2, U1 to U8)

Date: 2026-10-15. Reviewer: Lumen (UI/UX). Against: `docs/ui-spec.md` v2.2 (now v2.2.1, see the end).
Code reviewed: worktree `.claude/worktrees/agent-a639ef01943bf9cd9`, branch `worktree-agent-a639ef01943bf9cd9`, commits `1487efc..8becc55`. Read-only: nothing in the worktree was changed.
Paths below are relative to `src/Jellyfin.Plugin.Shoal/Configuration/` unless they start with `ui-dev/` or `docs/`.

## Verdict: SHIP AFTER MUST-FIXES

The page is good work. Overview matches the approved Native Plus composition almost exactly, the approval card with the typed safety code is right, the access switch and picker behave as specified, and the pairing outcomes, banners and danger zones read well. But Server detail, the screen people land on when something is wrong, is visibly broken (hero, empty diagnostics), and two screens print the word "null". Fix M1 to M7, then merge. The "Which apps work" block stays behind its flag until its ship gate (8.3.12) is met.

## How I checked

- `ui-dev`: `npm test` ran **165 passed, 0 failed** (jsdom plus real Chrome).
- Harness served with `node server.mjs` and driven with playwright-core and the system Chrome from my scratchpad (no files written into the worktree). Screenshots at 1440 and 375 px, Dark and Light, of: Overview (default, problems, empty, keys locked, recovery, restored), Libraries & access, Settings (flag on and off), Server detail (Hops, Maple paused, Cedar certificate), Friend detail (approval card: empty, format error, wrong code), Pair a server (Quick Connect phases A and B at 1440, invite path step 3 at 375, stored safety stop at 375). A scripted check at 320 and 375 px on 10 views found no horizontal page scroll.
- `STRINGS` diffed against every quoted string in the spec with a script (1,033 entries; 169 not found verbatim, almost all plural forms, units or extra lines the spec leaves to Kiro). Each difference that matters is listed below.
- Tokens, type and contrast: `shoal.css:15-98` is the 4.1 block word for word; `font-weight` appears only as 400 (3 uses) and 700 (45 uses); no `@import`, `url()` or font request; the primary button is `#0a7cab` with white text in every blue theme and follows the theme in Purple Haze; `:focus-visible` ring on everything (`shoal.css:114`); Jellyfin's `.raised:focus` and `.button-submit:focus` colors are neutralised (`shoal.css:202-209`). No `innerHTML`, no inline style or script in `shoal.html`.
- Not checked: a real Jellyfin 12.1 dashboard and the five non-Dark themes on it (see Kiro's question 2), a screen reader pass (E3), and axe (E7). Those belong to the lab release checklist, not to this merge.

## Must fix

**M1. Server detail hero renders as the page banner (red box, small image, pill at the bottom of the page).**
`shoal.js:6903-6926` builds the hero with `class: 'shoal-banner'`, `shoal-banner__img` and `shoal-banner__ph`. `.shoal-banner` is the 6.16 page banner (grid, `--shoal-hint-bad-bg`, red border, `shoal.css:263-273`), and the hero styles live under `.shoal-hero` (`shoal.css:731-733`), which nothing uses. Result in Dark at 1440: a red-tinted box with the backdrop as a small centred tile; at 375 an empty red box; the "Latest from Hops: Sintel" pill escapes to the bottom of the page; the 1240 px image overflows its box at 320 and 375 px.
Fix: in `serverBanner` use `class: 'shoal-hero' + (model.dim ? ' is-dim' : '')` on the container, `shoal-hero__img` on the `<img>` and `shoal-hero__ph` on the placeholder. Keep the pill as is (it positions against `.shoal-hero`, which is `position: relative`). Add a browser test that the hero's computed height is 160 px (120 on S) and that it has no `.shoal-banner` class.

**M2. Server detail never loads its link speed: empty chart, no KPIs, ladder, route panel, hints, recent tests or plays.**
`createSpeedPanel` starts with `selected = o.selected` (`shoal.js:3568`). `serverDetailScreen.render` passes `selected: s.handle` and then calls `setServers([s], s.handle)` (`shoal.js:7545-7555`); `setServers` only loads when the selection changed (`shoal.js:3729-3740`), so the panel never calls `ctx.api.speed`. Confirmed in the harness after a 4 s wait. This is the diagnostics screen; it must work.
Fix: in `setServers`, load when `changed || !data` (`if (changed || !data) { data = null; load(); }`). Also stop rebuilding the speed panel on every render: create it once per Server detail instance and only call `setServers` on redraws, so a poll doesn't reset the range the admin picked or re-run the request. Add a test that opening `view=server&id=h-hops` issues `GET …/Peers/h-hops/Speed` and renders the "Speed now" KPI.

**M3. The word "null" appears on screen.**
Native `Element.append` turns `null` into the text "null"; the page's own `el()` skips it, but two places call the native one with conditional children:
- `shoal.js:5154` `settingsPanel` (`p.body.append(...content)`): Settings > Playback prints "null" whenever the apps flag is off, i.e. **in production by default** (`shoal.js:5191`).
- `shoal.js:6210-6221` Quick Connect phase A (`wrap.append(…, silent ? … : null, s.paired ? … : null, …)`): prints "nullnull" under "Waiting for {Server} to approve…".
Fix: replace both with `appendKids(node, [...])` (`shoal.js:1348`), and grep for any other `.append(` that can receive `null`. Add a browser test that walks every view and scenario in the harness and fails when the root's `innerText` matches `/\bnull\b|\bundefined\b|NaN|\{[a-z]+\}/`.

**M4. Relay bottleneck note reads "Relayed through TailscaleFrankfurt:".**
`STRINGS.bottleneck_relay` (`shoal.js:238`) is "Relayed through Tailscale{region}: …" and `bottleneckText` passes the bare region (`shoal.js:1931`).
Fix: two strings, `bottleneck_relay: "Relayed through Tailscale: expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side."` and `bottleneck_relayRegion: "Relayed through Tailscale ({region}): expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side."`, chosen by whether `bottleneck.region` is set (same pattern as `route_*` / `routeRelayed`).

**M5. A poll on Server detail wipes what the admin is doing.**
`patch()` (`shoal.js:7565-7582`) rebuilds the whole screen whenever the payload changes and focus isn't inside it. On a real server `rttMs`, `mbps`, `streams[]` and `lastSync` change often, so every 10 s the admin can lose: open `details` ("Recent tests", "Settings for this server", "Notes about this sharing account", "Which apps…"), values typed into "Settings for this server" but not saved (focus left the field), and their scroll anchor. Each rebuild also re-inserts the bad alerts, which carry `role="alert"` (`shoal.js:7114`), so a screen reader re-announces them on every change; 12.4 says only state changes are announced. Spec 2.3: "Updates patch the DOM in place".
Fix (smallest that is correct): (a) before re-rendering, record which `details` are open by a stable key (`data-key` on each collapse) and reopen them after; (b) skip re-rendering the settings section while its form is dirty (compare inputs to `st.data.settings`); (c) give rendered alert boxes `role="note"` and announce a *new* bad alert kind once through `ctx.alertMessage` by diffing `alerts[].kind` between polls. The same `details` preservation applies to Friend detail (`shoal.js:8131`).

**M6. Owner D13 is not applied: "Streams at once per person" is 1-10, default 2.**
`shoal.js:5189` and `:5275` (`min: 1, max: 10`), `SettingsValidator.cs:38` (`Fix(…, 1, 10, …)`), `PluginConfiguration.cs:39` (`= 2`).
Fix: 1-4, default 4 in all three, and add the help line from the updated 8.5 item 3: `fMaxStreamsHelp: "How many friends' titles one person here can watch at the same time, across all friends' servers. Up to 4."` passed as `help` to that `numberField`. (Per-server "Streams at once" in 8.3.7 stays 1-10.)

**M7. Server detail library row: the access summary is laid out as the access switch.**
`shoal.js:7291-7294` wraps the summary in `class: 'shoal-access'`, the 6.17 switch container (`flex-direction: column`, `shoal.css:506`), so the eye icon sits on its own line above "1 person: Rich". It also never says "Only you", and with unknown users it prints "2 people: " with nothing after the colon (`:7293`).
Fix: replace the whole `summary` expression with the shared helper: `accessSummary(m.access, st.data.users || [], currentUserId(ctx, st.data), 'accessEveryoneSummary')` (`shoal.js:4558`), which already uses `shoal-accessline` and handles "Only you" and "Hidden from everyone".

## Should fix

**S1. Keys can't be unlocked: sync and test actions stay live.** 8.8 "Blocking": "pairing, sync and test actions for existing servers are replaced by Pair again". Overview's "Sync now" (`shoal.html:38`, wired at `shoal.js:3783`) and Server detail's "Sync now", "Test link" and "Retry now" (`shoal.js:7073-7087`) still show. Fix: when `ctx.status.secretsUnreadable`, hide "Sync now" in the Overview header; on Server detail render primary "Pair again" first and hide Sync, Test, Retry and Pause (8.3.9: "Keys can't be unlocked: banner, primary Pair again").

**S2. Server detail: the fix isn't first.** 8.3.1: "Problem states put their fix first as primary". `actionsNode` (`shoal.js:7096-7101`) puts Resume and Pair again third and fourth (Maple at 375: Resume is the third tile). Fix: build the list as `[fix, ...rest]` where `fix` is the one primary for the state (Retry now, Check certificate, Resume, or Pair again) and `rest` keeps the quiet order Sync now, Test link, Pause, Pair again, Remove server… without duplicating the fix.

**S3. Server detail header height on phones (Kiro's question 3) and the pause help.** The help line now sits in its own full-width row under the whole header (`shoal.js:7107`, `shoal.css:707,771`), detached from the button it explains. Decision:
- Put it where Friend detail already puts it, which reads well: wrap the Pause button in `el('span', { class: 'shoal-action' }, pauseBtn, pauseNote)` with `aria-describedby` from the button to the note, and delete `.shoal-detail__pausenote` (JS and both CSS rules). Show it for Pause only; when paused the chip and the hint already explain, so hide the note.
- On S the action grid has five tiles, so "Remove server…" sits alone and wraps to two lines. Add inside the `max-width: 639px` block: `.shoalPage .shoal-detail__actions > :last-child:nth-child(odd) { grid-column: 1 / -1; }`.
- Meta line 2 wraps as "Server ID 0071072a" / "· Paired Oct 6 by Quick Connect" with the dot leading the second line (`shoal.js:7048-7049`). Render "Paired {date} by …" as its own `shoal-detail__metaitem` (one line each, no separator), which is also easier to read.
- Keep the 120 px hero on S. With M1 fixed and the three changes above, the header at 375 is about 120 px shorter and every line is attached to what it explains. No further cut needed.

**S4. Copy differs from the spec.**
- `shoal.js:96` `emptyStep3`: "Compare the safety code, then…" → "Check the safety code together, then try it yourself before you open it to everyone here." (8.1.5; "compare" is wrong for the invite path, where the friend types it).
- `shoal.js:696` `errWrongNetwork` is used for every network class but names Tailscale only (8.2.3 says "adapt per class"). Pick by the chosen `network`: Tailnet keeps the current text; LAN "That address isn't a home network address. Change How you reach it, or check the address."; Public "That address is on a private network, not the internet. Change How you reach it, or check the address."
- `shoal.js:114` `playingNowTranscoding`: "Playing now: {n} · {t} transcoding" → "Playing now: {n} ({t} transcoding)" (8.1.1 item 7, 8.1.7).

**S5. Delete dead strings, one of them wrong.** Unused keys: `hintPairingStopped_codesDidNotMatch`, `hintPairingStopped_requestReplaced`, `hintPairingStopped` (`shoal.js:177-179`), `accessNoOne` (`:336`), `backToOverview` (`:753`), `factHomeName` (`:824`), `keepWaiting` (`:891`). `hintPairingStopped_requestReplaced` ("Someone else's request took over the code") is the alarming wording v2.1 removed; a translator or a later edit could revive it. Add the inverse of the existing test: every `STRINGS` key is referenced (literally or through a known dynamic prefix such as `out_`, `hintSafety_`, `ladderClass_`).

**S6. Quick Connect phase A: "Copy code" sits between the two codes.** `shoal.js:6213` puts the copy button after the 6-digit box in a wrapping row, so at 1440 it lands between "171 482" and the safety code "K7QM" and reads as if it copies the safety code (which must never be copied or typed, 6.10). Fix: put the button inside the 6-digit code's column, under the box: `el('div', { class: 'shoal-codewrap' }, labelledCode('qc', …), copyButton(…))`, and keep the safety code as the second item of `.shoal-codes`.

**S7. Friend detail, invite section while an approval is pending (Kiro's note).** The card says "Sam's Jellyfin wants to connect" and the section below still says "An invite is active · expires in 8:59", which suggests the invite is still waiting to be used. Decision: while `approval.state === 'pending'`, the Invite section shows only the `meta` line "{homeName} used this invite. Approve or deny the request above." plus link "Cancel invite" (which ends the request too); hide "Create a new invite" and the countdown. I'll fold this into 8.7 item 3 in the next spec pass; build it from this note.

**S8. Apps block to the new 8.3.12 (before the flag is turned on, not before merge).** `shoal.js:1025-1040` and `appsWorkBlock` (`:6934-6949`):
- `appsLead`: "Titles from {server} play in most Jellyfin apps, but not all of them."
- `appsSummary`: "Which apps can play titles from {server}" (no possessive: today it renders "Hops's titles").
- `appsNotYet` → `appsDont: "Doesn't work"`; `appsWhatBody`: "ask which app they're using. If it's on the Doesn't work list, try Moonfin or Jellyfin's web app instead."
- Replace `appsWorks1..3` with the five row keys of the 8.3.12 table (`appsRowWeb`, `appsRowMoonfinIos`, `appsRowMoonfinOther`, `appsRowJmp`, `appsRowAndroid`) and a constant `APPS_VERIFIED = { web: false, moonfinIos: false, moonfinOther: false, jmp: false, android: false }` next to `STRINGS`. Render `verified` rows under "Works" (`check`, ok; skip the heading when none) and the rest under "Should work" (`info` icon in `--shoal-muted`, then `appsShouldNote: "These are built the same way, but nobody has tried them with Shoal on a real device."`). Flip a flag only with a test record named in the commit.
- Update `strings.test.mjs` and `settings.test.mjs` expectations accordingly.

## Nice to have

- **N1. Everyone mode says the same thing twice** (Libraries & access): the description "Everyone with an account here can see and play it, including people you add later." and the summary "Everyone on this server, including people you add later" sit 40 px apart. Use `accessEveryoneShort` for the summary in the library block (`shoal.js:4919`, drop the `'accessEveryoneSummary'` argument).
- **N2. "See saved selection"** is a quiet button; 8.4.1 calls for a link (`linkButton`). It's secondary to "the switch", so the lighter weight helps.
- **N3. Number fields are full width** (Transcodes at once, Storage limit, Streams at once per person, Priority). Add `shoal-input--num` (96 px, `shoal.css:655`) via `numberField`; a 1,200 px wide box for "2" looks unfinished.
- **N4. Step checklist numbers centre on tall rows** (Steps for your friend, step 3): `shoal.css:716` `align-items: center` → `flex-start`, and give `.shoal-checklist__icon` `margin-top: 2px`.
- **N5. "Hours offline before hiding"** stays editable while its checkbox is off (`settingsScreen`); disable it until the box is ticked.
- **N6. `speedHint_perConnection`** ("…holds single streams back; one stream gets the lower number.") is jargon. Suggest: "One stream is slower than the link allows, usually because of latency or a relay. Several streams at once still get the full speed."
- **N7. Approval card "Deny" busy text** is "Saving…" (`shoal.js:2330`); add `denying: "Denying…"`.
- **N8. CSS tidy**: `shoal.css:550-551` define `.shoal-titlerow` twice; `shoal.css:819` `margin: 24px -0px 0`.

## Accepted deviations (no change)

- **Settings Save placement.** Save sits after the form sections (before "Protect your shared videos"), not at the very bottom. Right call: it makes clear which fields Save covers, since Protect, Key storage and Backups save on their own. 8.5 focus order will follow the build.
- **No `@supports not (container-type)` fallback** (spec 3.2). Note for the record: the browser is the admin's, not something Jellyfin ships, so "12.1 ships a browser that has them" isn't the reason. The real reason is that container queries are in every browser from 2023 on (Safari 16, Chrome 105, Firefox 110); an admin on iOS 15 gets the desktop grid, which still works. Accepted.
- **One "Open Settings" string** for both places. Fine; Settings is a screen name.
- **Extra copy Kiro added** (`restoreBody`, `restoreNone`, `transportTailnetBody`, `transportPublicBody`, the three extra `finding_*` kinds, `deleteAllBody`, `typedHelp`): read and approved as written.

## Answers to Kiro 2's design questions

1. **Normalising the typed safety code on the page.** Yes, and keep sending the normalised value as `sas`. Conditions: the server applies the identical normalisation itself (upper case, strip spaces and hyphens, I and L to 1, O to 0), validates 8 Crockford characters (U is invalid) and compares in constant time; it never relies on the page having done any of it. A malformed `sas` gets a 400 and never a 500; whether it costs a try is Warden's call (from the page it can't happen, because Approve stays disabled). Spec 13's "as typed" means "the server doesn't depend on the page's formatting"; your behaviour satisfies it.
2. **Theme palettes are approximations.** Accepted for the harness: `ui-dev/themes.css` isn't shipped and the page reads the real `--jf-palette-*` at runtime. Release gate, not merge gate: Pilot takes the 15 C1 screenshots (six built-in themes, Overview, Server detail, Settings) on the lab's Jellyfin 12.1 and I check them, including that Purple Haze takes the themed primary and Apple TV the light signal set.
3. **Tall Server detail header on phones.** See M1 and S3: move the pause help under the Pause button, span the odd last tile, split the meta line. Keep the hero.
4. **Approval card above "An invite is active".** See S7.
5. **Open question 1 (`off` for the Tailscale socket).** Yes from the UI side: 8.5 item 6 offers "Off". Atlas adds `off` to DESIGN 3.2.

## Spec change made in this review (for Kiro 2)

`docs/ui-spec.md` is now **v2.2.1** (copy only; tokens, layout and components unchanged):
- **8.3.12 rewritten** for Warden W4-15 and owner D12/D14: three lists, **Works** (only apps with a recorded real-device pass on the shipping build; at first release at most Jellyfin's web app and Moonfin on iPhone), **Should work** (the other supported apps, with one honest line), **Doesn't work** (was "Not yet"). No "yet" anywhere. Summary is "Which apps can play titles from {Server}". The rows are a table with a `verified` flag so moving an app is a one-line change. Ship gate now also needs T-CLI-13 on record for every verified row. Build per S8.
- **8.5 item 3 and 8.5.1**: "Streams at once per person" 1-4, default 4, with a help line (owner D13). Build per M6.
- 8.3.6 and 8.3.10 use the new summary text; a short "v2.2.1 changes" section sits at the top of the spec.

Warden's README sentence (the token-adding proxy warning) is README-only by design and isn't in the UI.

## Re-review

After M1 to M7 land, send me the commit range. I'll re-run the harness screenshots for Server detail (1440 and 375, Dark and Light), Settings with the flag off, and Quick Connect phase A. S-items can land in the same pass or the next; S8 must land before `features.appsWork` is turned on.
