# Warden review 05: Kiro 2's admin UI branch (U1 to U8)

Date: 2026-10-15. Reviewer: Warden (read-only; this file is the only thing written).
Subject: branch `worktree-agent-a639ef01943bf9cd9`, commits `1487efc..8becc55` (11 commits), worktree
`<project>` (paths below are relative to it).
Merge base with main: `af7fbe0`. Main is at `4a40308` (K9) plus an uncommitted `Visibility/` folder.
Spec: DESIGN v2.3 (4.1b, 8.5, 8.6, 8.7, 9.1, 9.3, 9.5, 9.6), ui-spec F5, T-SEC-11.

## Verdict

**0 Critical, 0 High, 3 Medium, 5 Low, 4 Info.**

**GO to merge into main, on the condition that W5-1, W5-2 and W5-3 are done in the merge commit.** None of them is a design change.
W5-1 and W5-2 are not optional: the merged tree **will not build** (W5-1) and one admin route becomes **ambiguous at runtime** (W5-2).
The branch alone (against its own merge base) is clean: no anonymous action, no credential or peer GUID in any shape it returns, no
markup built from data in the page. The page is the best-built part of the project so far on the injection side.

What I checked and found sound is listed at the end ("Nothing found in"), so the absence of a finding is stated, not assumed.

## Findings

### W5-1 (Medium, merge-blocking) Both controllers write responses the way the banned-API list forbids; the merged tree does not compile

`src/Jellyfin.Plugin.Shoal/Api/Admin/AdminController.cs:250-257, 266` and `Api/Admin/AdminSettingsController.cs:564-571` do
`Response.Headers.CacheControl = "no-store"`, `Response.Headers.RetryAfter = ...` and `new ContentResult { ContentType = "application/json; charset=utf-8", ... }`.
Main's `BannedSymbols.txt` (K7, DESIGN 8.6/8.8) bans `P:HttpResponse.Headers` and `M:ContentResult.set_ContentType`; RS0030 is an error
(`TreatWarningsAsErrors`). The branch was last merged from main at `f4ca23a`, before K6/K7, so it never saw the ban.
Beyond the build: these responses bypass `ResponseWriter`, so they carry none of the headers 8.6 gives every Shoal JSON answer
(`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, CSP/CORS stripped). Admin JSON that can hold peer-supplied names
should have them.
**Fix:** return `ShoalResult` (`Http/ResponseWriter.cs:286`, `new ShoalResult(status, body, retryAfterSeconds)`) from every action, as main's
`AdminPairingController` does; delete both private `Respond/Error/TooMany` helpers. `ShoalResult` serialises with its own options
(camelCase, string enums, **no null omission**, `ResponseWriter.cs:94-97`) while `AdminJson` omits nulls. Either add
`DefaultIgnoreCondition = WhenWritingNull` to the writer options or confirm the page treats `null` like absent (it mostly tests truthiness;
run the 165 ui-dev tests against real output once). Use `Policies.RequiresElevation` instead of the string literal (`AdminController.cs:106`,
`AdminSettingsController.cs:162`) so a rename cannot silently drop the policy.

### W5-2 (Medium, merge-blocking) `POST Shoal/Admin/State/ConfirmRestore` is defined twice, and the two versions mean different things

Branch: `Api/Admin/AdminSettingsController.cs:461-472`, backed by an in-memory `RestoreConfirmation` (`AdminServices.cs:301-305`) read in
`AdminStatusBuilder.cs:96-113`. Main: `Api/AdminPairingController.cs:183` calling the new `IStateStore.ConfirmRestore()`, which clears
`StateLoadInfo.AccessReviewRequired` (durable, set by `RestoreFromAsync`, `StateStore.cs:169`).
Two actions on the same verb and template make ASP.NET Core throw `AmbiguousMatchException` on every call: the restore banner can never be
confirmed, and no unit test sees it (tests call the actions directly). Even with one deleted, the branch's banner reads
`needsConfirm = !restore.Confirmed` (memory only), so confirming through main's endpoint would not clear it, and a restart re-shows it.
The access-review flag is the security control of Warden N-9 (a restored older state is hardened, and the admin must review who can see what);
a banner that does not track it is worse than none.
**Fix:** delete the branch's action, `RestoreConfirmation` and its registration (`PluginServiceRegistrator.cs`, `TryAddSingleton<RestoreConfirmation>`);
build `RestoredFromDto.NeedsConfirm` from `state.LoadInfo.AccessReviewRequired`. Note main's action answers **204 with no body**; verify
`ApiClient.ajax` with `dataType:'json'` does not reject an empty 204 (it calls `response.json()`; if it does, `shoal.js:1563` `call()` must skip
JSON for 204, and the same applies to main's `DELETE Pairing` 204). Add a test that builds the MVC endpoint table and asserts no duplicate
(verb, template) pair across controllers.

### W5-3 (Medium) `POST State/StartOver` works in Normal mode, not only in Recovery mode

`Api/Admin/AdminSettingsController.cs:418-438`. The typed phrase is checked server-side (good), but nothing checks `_state.Status`. DESIGN 3.7
and ui-spec 8.8 define Start over as the Recovery-mode exit; the page only shows it there. Any caller holding an admin token (or an admin's
stale tab) can post `{"confirmName":"start over"}` on a healthy server: the state and `.bak` are moved aside and an empty state is written,
which drops `PeerRole.HardeningMode` (back to the default, i.e. out of `Enforce`), forgets every share and every peer, and leaves the friends'
sharing accounts and their tokens alive on the server with nothing sweeping or repairing them. Also, `StartOverAsync` raises no AccessReview
or reconcile finding for that case.
**Fix:** `if (_state.Status != StateStatus.Recovery) return Error(409, "notInRecovery");` before the phrase check. The healthy-server
reset is `DELETE Shoal/Admin/All`, which stays the single destructive door. Add the negative test.

### W5-4 (Low) A request without a JSON content type is a 500, not a 400

`AdminSettingsController.cs:220, 314, 385, 479`. `ReadFromJsonAsync` throws `InvalidOperationException` (not `JsonException`) when
the content type is not JSON, e.g. `curl -X POST .../State/StartOver` with no body. Only `JsonException` is caught, so the answer is a 500
and a logged stack trace. Admin-only, no leak to the client, but it breaks the "an error is a status and one fixed word" rule in the
controller's own comment. **Fix:** catch `InvalidOperationException` as well (or check `Request.HasJsonContentType()` first) and answer 400 `invalid`; put the four reads in one helper.

### W5-5 (Low) The F5 scans are too narrow to be the gate they are described as

Asked: does the automated scan run over every endpoint, or would it miss one? It would miss some.
1. `ui-dev/tests/security.test.mjs:92-103` scans the **mock** (`mock.handle`), not the C# controllers, over a hand-listed set of 11 GET paths,
   in one scenario (`problems`). Every POST/PUT/DELETE answer is unscanned (Probe, Pairing, Pairing/Confirm, `Shares/{id}/Invites`,
   `Shares/{id}/Approval`, `PUT Settings`, `PUT Hardening`, `POST Libraries`, `State/*`). `Peers/h-hops/Telemetry` and the `hostile` scenario are not included.
2. The GUID regex (`security.test.mjs:9`, `AdminControllerTests.cs:27`, `AdminSettingsControllerTests.cs`) matches **dashed** GUIDs only.
   Jellyfin item, user and device ids travel in N format (32 hex, no dashes); the peer handle itself is 32 hex, so the regex was kept loose and
   the real thing to detect (a peer-side id) cannot be caught. A leaked `shareUserId` or full `pinnedServerId` in N form passes.
3. The key check (`security.test.mjs:101`) matches the exact keys `token|accessToken|apiKey|secret|password`. It would not flag `sas`, `nonce`,
   `homeNonce`, `commit`, `code`, `invite`, `deviceId`, `accessKey`.
4. The C# F5 tests (`AdminControllerTests.cs:168-195`) run Status, GetPeer and SyncAll with one fixture, and only Settings/Hardening/State in
   `AdminSettingsControllerTests.cs`. They do check the admin user id and the expected device id by value, which is the right idea.
5. The DOM scan (`assertSafe`, `security.test.mjs:12-44`) strips every `id="..."` before the GUID check (needed for generated ids) and so
   would not see a GUID placed in an id attribute. The "invite in the DOM" check (`shoal1-`) is sound, but the invite is legitimately shown once on Friend detail, so it only guards the other screens.
**Fix:** (a) replace the regexes with a **value-based** scan: the fixture holds known sentinel values (share user id in N and dashed form, full
pinned server id, every `expectedDeviceIds`, the TLS pin, a fake token and a fake invite planted in the state/secret store) and every response
of every action, GET and write, must not contain any of them in any case or format; (b) walk the MVC endpoint table once and drive **every**
admin route against the real controllers, so a new route is covered by default; (c) widen the key blacklist to the list in point 3 (with an
allow-list for the two intentional one-time fields: `invite` in the create-invite answer, `safetyCode`/`code` in the Home pairing session).
Required before K21/K22 land their routes, because that is where the real risk sits (invite create and approval).

### W5-6 (Low) `StubRootChecker` implements less than DESIGN 3.1 requires

`Api/Admin/AdminServices.cs:137-251`. 3.1 says StubRoot "must not be inside, **or contain**, any existing library path", "must not be a symlink or
reparse point", and refuses `fuse.*`. The checker (a) only tests "inside" (`IsInside(full, location)`), not "contains"; (b) never resolves
links (`Path.GetFullPath` does not), so a symlink named `/srv/shoal` pointing into a library passes the inside check; (c) lists only
`fuse.sshfs` among FUSE types. ShoalIndex classifies by `<StubRoot>/<peerKey>/<subfolder>`, so a root that contains a library is not
catastrophic, but it breaks the "no chains, no overlap with real media" rule and the cleanup assumptions of 5.1-5.2.
Also: `GET Settings` calls `Check` -> `CanWrite` -> `Directory.CreateDirectory` + a probe file, so a read creates directories at the configured path (Info).
**Fix:** add the "contains" test, resolve `LinkTarget` for the folder and each ancestor up to the root, treat any `fuse.*` as network, and
make the read path non-creating (check writability only on PUT).

### W5-7 (Low) `MaxStreamsPerUser`: the branch still says 1..10; the decision is 1..4 (D13, DESIGN 3.5, 6.9, K14)

`Configuration/SettingsValidator.cs:35` (unchanged, `1, 10`) and the page: `Configuration/shoal.js:5189` (`max: 10`) and `:5275` (`whole(..., 1, 10)`).
`PutSettings` (`AdminSettingsController.cs:256`) rejects any value `Apply` would adjust, so after K14 changes the clamp, a page that offers 5..10
gets a generic 400 and the form shows no reason. Until K14 lands, a PUT of 6..10 is accepted and stored, which is exactly the unsafe range
(grant cap 8 cannot guarantee a slot-free grant to evict; T-CLI-8 writes `10` into the XML and expects `4` back).
**Fix:** K14's PR changes `SettingsValidator.cs:35` to `1, 4`; the page range (`5189`, `5275`, the `errWholeNumber` text) must follow in the same merge
window; add one controller test (`PUT 5` -> 400). `SettingsValidator` changes from both branches touch different lines and merge cleanly (see Merge risk).

### W5-8 (Low) The server cannot yet judge the typed safety code; the page's normalisation is fine only if the server also normalises

The approval endpoints do not exist in C# yet (`Shares/{id}/Approval` is mock-only), so this is the contract K22 must meet. The page sends
`{approve:true, sas}` where `sas` is already normalised (`shoal.js:2552-2558`: upper-case, whitespace and hyphens removed, `I`/`L`->`1`,
`O`->`0`; validity `^[0-9A-HJKMNP-TV-Z]{8}$`; the input has `autocomplete=off`, is never stored, logged or put in a URL, and polling never touches it).
That matches DESIGN 4.1b step 3, and normalising on the page is acceptable as a convenience. Requirements for the server side, none of which can be
trusted to the page:
1. The server normalises the string itself with the same rules and treats the page's normalisation as absent: a direct caller can send
   `k7qm-2xa4`, `K7QM 2XA4`, `K7QM2XA4\n`. Bound the raw length (<= 32 chars) **before** any work; reject non-string.
2. Constant-time compare of the 8 characters (`CryptographicOperations.FixedTimeEquals` on bytes); a miss and a malformed value count the same way.
3. The tries counter is per approval request, in the store, never per page or per tab. The third miss ends the request and its invite together
   (4.1b); restarting the page, a second tab, or a retry after a 429 must not reset it. Rate limit the endpoint in addition (the 3-miss rule
   protects the code, not the CPU).
4. The code is never written to a log line, an exception message, a response (`GET Approval` carries no `sas`), the state file or the audit
   trail; `{match:false, triesLeft}` is the whole answer. Add the code to the F5 sentinel list (W5-5).
5. Spec 13 says "as typed"; the page sends normalised. Record "normalised by the page and again by the server" in ui-spec 13 so the two stay in step.

### Info items

- **I-1 `DELETE Shoal/Admin/All` returns 501** after the typed-phrase check (`AdminSettingsController.cs:446-455`). Correct for now (the phrase gate is
  server-side: it reads `confirmName` from the body and compares ordinally to `"delete everything"`; the test pins 400 without it). **Release
  blocker:** add to the 11.5 checklist that the danger zone's "Delete all Shoal data" must be implemented (libraries Shoal created, sharing
  accounts, secrets, stubs, state) or the button removed before the first public push; a visible destructive control that answers "not
  implemented" after the user typed a confirmation is a trust problem.
- **I-2 Per-object destructive calls are still mock-only** (`DELETE Peers/{h}`, `DELETE Shares/{id}`, `PUT Peers/{h}` with a new host). When K21/K22
  and the peer endpoints are written: the server must compare `confirmName` with the **stored** name of that peer/friend (not with anything the
  client sends for comparison), refuse when it is missing, and apply it before any side effect. `StartOver` and `DeleteAll` do this correctly today
  and can be the model. Also `ShareState.ShareId` must be a random local id, not the share user's id (the fixture uses `share-bob`).
- **I-3 `ServerDetailDto.ServerIdPrefix`** (`AdminController.cs:~193`) returns the first 8 hex of the pinned server id. That is the spec (9.3, "Server ID"),
  and the full id never appears (test `PeerDetail_ShowsTheServerIdPrefixNeverTheFullId`). Recorded so the F5 scan's allow-list is explicit about it.
- **I-4 Hostname in the copy-able command** `'tailscale ping ' + hostOf(s.url)` (`Configuration/shoal.js:7220`) is built from the stored URL. Safe today because
  `AddressPolicy.HostLabel` (`Peers/AddressPolicy.cs:646`) limits hosts to letters, digits, `_` and `-`; a hand-edited `state.json` is the only way around
  it (WHATWG host parsing allows `$ ( ) ; & '`). One-line defence in depth: only build the command when the host matches `^[A-Za-z0-9._-]+$`.

## Requested checks, answered

**Admin-only authorization.** Both classes carry `[Authorize(Policy = "RequiresElevation")]` at class level (`AdminController.cs:106`,
`AdminSettingsController.cs:162`); no method overrides it; there is no `[AllowAnonymous]` anywhere in `src` of the branch (grep: only `ShoalProxyController`'s
plain `[Authorize]` and a comment). Routes: `GET Status`, `POST Sync`, `GET Peers/{handle}`, `POST Peers/{handle}/Pause|Resume`, `GET|PUT Settings`,
`GET|PUT Hardening`, `GET State`, `POST State/Restore|StartOver|ConfirmRestore`, `DELETE All`. The share user and any non-admin fail core's
`RequiresElevation` handler (401/403 before the action). API keys pass `RequiresElevation` in core, as for every admin endpoint; that is core's
model, not a Shoal decision, and the Hard Rule about tokens in URLs is not touched (the page uses headers via `ApiClient`).
The reflection test that pins exactly one anonymous action is in **main** (`CallerContextTests.cs:~337-368`, K6) and scans **every** controller type in the
assembly; it does not exist on the branch (the branch predates K6). After the merge it will include these two controllers; both have class-level
`Authorize`, so it passes and the anonymous set stays `{PairController.Pair}` (empty until K22). Kiro 2's own per-class tests
(`AdminControllerTests.cs:313-327`, `AdminSettingsControllerTests.cs:~365-379`) are fine as extra pins. CSRF is not a concern: Jellyfin's admin API authenticates
by header, not by cookie.

**Admin JSON content.** I read every DTO (`AdminDtos.cs`, `AdminSettingsController.cs` records) and the builders. Nothing returned is a token,
invite, nonce, SAS, password, API key, TLS pin value (only `tlsPinned: bool`), secret-store content (`keySource` is an enum word, `secretsReason` a word),
user id, device id, or full peer server id. Handles are local random values (`PeerKey` = 16 random bytes hex, `PairingService.cs:347`). Strings that are
shown: peer `DisplayName`, `BaseUrl`, `ProxyUrl`, friend names, library names, finding details, state file names (an allowlist from `GetCandidates`).
`BaseUrl` and `ProxyUrl` cannot carry credentials when written through `AddressPolicy` (userinfo is rejected, `AddressPolicy.cs:373`; proxy must be a loopback
`socks5` literal, `:643`); `StateValidator` itself only checks length and control characters (`State/StateValidator.cs:117-118`), so a hand-edited `state.json`
could put `user:pw@` in `url`, and Status would echo it to the admin. Admin-only and self-inflicted, but a one-line `Redact` pass on `Url`/`ProxyUrl` in
`AdminStatusBuilder.Server` would close it. Error bodies are `{error:"<fixed word>"}` only (`Error()`), never exception text. Logging: only fixed
messages, the allow-listed restore file name, and the hardening mode.

**Destructive calls.** `StartOver` and `DeleteAll` require the typed phrase **server-side** (`ConfirmedAsync`, `AdminSettingsController.cs:474-487`, ordinal compare),
checked before any side effect; `Restore` requires a name from the server's own candidate list (`:393-397`), and the store re-checks and hardens
the restored state (`HardenAfterRestore`, access review). `PUT Hardening` has no server-side confirmation (the page asks first); acceptable for an
admin-only toggle, and `Off` is only reachable through the Troubleshooting disclosure. Gaps: W5-3 (StartOver in Normal mode), I-1, I-2.

**SettingsValidator.** The `off` addition (`Configuration/SettingsValidator.cs:19-22, 70`) is exact (`StringComparison.Ordinal`; `OFF` is rejected), keeps the
260-character cap, control-character ban and fully-qualified-path rule for everything else; validation stays strict: `PutSettings` runs `Apply` on a
copy and answers 400 if it would adjust anything (`AdminSettingsController.cs:254-259`), so nothing is silently clamped on the way in. `DiskQuotaGbPerPeer > 50`
is rejected (`:255`), matching main's new clamp `1..50`. Answer to open question 1: **confirm `off`** (DESIGN 3.2 should list it), but the reader of the
socket (no consumer exists in `src` yet) must treat `off` as "do not read" and never try to open a relative path called `off`. A custom socket path is read by
Shoal as the admin's choice; an admin can already point Jellyfin anywhere, so I record it, not rate it. `MaxStreamsPerUser`: W5-7.

**Typed safety code.** W5-8.

**Frontend.** `shoal.js` (8,636 lines), `shoal.html`, `shoal.css`:
- No `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval`, `new Function`, `DOMParser`, string timers or `.on*=` handlers; the
  C# test `Js_NeverBuildsMarkupFromStrings` pins that and the grep agrees. `el()` writes text through `textContent` or `createTextNode`; `setAttr`
  throws on any attribute starting `on`, `style`, `srcdoc` or `formaction` (`shoal.js:1319, 1327`); `style` is touched in exactly one place and only for
  `--shoal-*` custom properties (`:1340`, pinned by a test). Copy with substituted values is split first and filled after (`tRich`), so a value cannot add markup; `fill`
  is a single pass and does not re-expand.
- Every hostile string (server, library, friend, title, user, device, Home names, file names, findings) reaches the DOM through those helpers.
  The `hostile` scenario runs on all six screens, five banner scenarios and overlap with the payload visible as text, and the DOM walk checks for
  event attributes, scripts, frames, off-origin images and non-hash links (`security.test.mjs`).
- URL construction: API paths use `encodeURIComponent` for every handle (`:1580-1627`); the only query is `?deleteLibrary=true|false` and `?range=`
  (encoded). Images come from `ApiClient.getImageUrl/getUserImageUrl` with ids and tags from the local state (`:3398-3412`); `localItemId` is a local library
  id, never a peer's. In-page links are `#/configurationpage?...` built by `routePath` with encoded values. No `fetch`, XHR, WebSocket or beacon; the only URL literal in the file is
  the SVG namespace (pinned).
- CSP: no `<script>`, no inline style or handler, no `${` in the HTML (pinned: Jellyfin's `translateHtml` would rewrite it), one same-origin stylesheet link, no `url()`
  or `@import` in the CSS. The harness CSP (`ui-dev/server.mjs:12`) allows `style-src 'unsafe-inline'`, so the tests cannot prove "no inline style" from CSP
  violations; the DOM-attribute test (`assertSafe`, style must match `--shoal-*` only) and `style.setProperty` (allowed under strict `style-src`) cover it. Fine.
- Storage and console: exactly two `localStorage` keys, `shoal.selected` (a local peer handle) and `shoal.qcStepsSeen` (`'1'`), both through a try/catch wrapper
  (`:1502-1520`); no `sessionStorage`, no IndexedDB, no `console.*`, no `debugger` (all pinned by `Js_NeverLogsAndNeverStoresSecrets`). The one-time invite and the
  QC code live in JS memory and DOM text only (the invite is held in the wizard object and in `inviteBox`; I would also clear `wiz.form.invite` after
  `startPairing` succeeds, Info). The page never calls `getPluginConfiguration`/`updatePluginConfiguration` (pinned).
- Error handling: `toApiError` keeps only the status, `Retry-After` and an `error`/`detail` word matching `^[A-Za-z]{1,40}$` (`:1535-1558`); no server message is shown.

**The mock admin API never ships.** The plugin `.csproj` embeds exactly three files: `Configuration\shoal.html`, `shoal.js`, `shoal.css` (`Jellyfin.Plugin.Shoal.csproj`, last
`ItemGroup`). `ui-dev/` is a sibling directory of `src/`, not under the project folder, so no SDK glob reaches it; the plugin project has no reference to it, `build.yaml`
ships `Jellyfin.Plugin.Shoal.dll` only, `tools/stage-plugin.sh` copies the dll and `meta.json`, and `ui-dev/package.json` is `private` and says "Not shipped".
`shoal.js` contains no import of the mock (the harness imports `shoal.js`, not the other way round; the word "mock" appears only in comments). The test
`AdminUiResourceTests` asserts the embedded names. I did not rebuild the dll (read-only role); recommended release check: `unzip`/`ildasm` the
resource list of the built dll and assert it equals `{shoal.html, shoal.js, shoal.css}` plus nothing under `ui-dev`, as a CI step.

**Rate limits.** `POST Sync` has a 5-minute global cooldown with `Retry-After` (`AdminCooldowns`, tested). Other admin writes are not rate-limited; they are
admin-only and bounded by the state-store lock. The approval endpoint is the one that needs a limit (W5-8.3).

## Merge risk: files both branches touch

Computed from `git diff --name-only af7fbe0 <tip>` on both sides: only **two** files overlap.

| File | Branch (Kiro 2) | Main (Kiro) | Textual | Semantic |
|---|---|---|---|---|
| `Configuration/SettingsValidator.cs` | `OffSocket`, note text, `IsValidSocket` (lines 19, 54, 67) | `DiskQuotaGbPerPeer` clamp 1..50 (line 42); K14 will change line 35 | auto-merges | K14 must bring the page range with it (W5-7) |
| `PluginServiceRegistrator.cs` | admin services after `StartupReport` | K5-K9 services after `PeerClient`, new usings | different hunks, auto-merges; the using block may conflict | drop `RestoreConfirmation` (W5-2); `IPeerLiveStatus` must be bound to main's `PeerHealthStore`/pairing state instead of `UnknownPeerLiveStatus`, otherwise every server reads "Offline" and `Pairing`/`PairingAborted` never show |

Files that do **not** overlap but collide in meaning:
- `State/IStateStore.cs` (main adds `ConfirmRestore()`): the branch's tests use the real `StateStore`, so no fake breaks; W5-2.
- `Api/AdminPairingController.cs` (main) vs `Api/Admin/*` (branch): same route prefix `Shoal/Admin`, different classes. Overlap is only `State/ConfirmRestore` (W5-2).
  `Peers/Probe`, `Pairing`, `Pairing/Confirm`, `Peers/{handle}/DismissPairing` exist only in main and `GET Peers/{handle}` only in the branch: no other ambiguity. Main's
  `AdminPairingController` already implements what the page's pairing wizard needs; the mock shapes were written to ui-spec 13 before it existed. After merging, run the page once against the
  real `Pairing` JSON (field names, `null` handling, 204 on cancel) before any sign-off.
- `Http/ResponseWriter.cs`, `BannedSymbols.txt` (main): W5-1.
- Main's uncommitted `Visibility/` folder (`AccessService`, `LibraryOptionsFactory`): no overlap; the Libraries screen's `GET Libraries` and `PUT Libraries/{m}/Access` will be
  the next controller to be written against `ShoalResult`.
- `docs/DECISIONS.md` has an uncommitted edit in main; the branch touches no shared docs except its own report.

Recommended order: merge main into the branch first (fix W5-1 to W5-3 there, rerun both test suites), then fast-forward main. That keeps Kiro's concurrent work out of the conflict.

## Nothing found in

- Anonymous reachability of any new route; policy string on both controllers.
- Markup injection: the page cannot build markup from data (pinned by two independent test layers).
- Secrets in `localStorage`/`sessionStorage`/console/URLs from the page.
- Credentials or peer GUIDs in the shapes returned by Status, Peer detail, Settings, Hardening and State.
- Path traversal through `Restore`: the file name must equal a name in the server's own candidate list; the store re-checks it.
- The mock reaching the plugin build.
- `PutSettings` mutating live config before validation: `ISettingsAccessor.Current` returns a **copy** (`PluginSettingsAccessor.Copy`), and `Save` happens only after validation.

## Residual notes for the manager

1. Open question 2 to 7 of Kiro 2's report are shapes, not security; I have no objection to any of them. Question 8 (W4-15 softening) is Lumen's.
2. Question 10 (restored banner global state, "Got it" key removed) is right and is the reason W5-2 must be fixed with the durable flag.
3. After merge, re-run T-SEC-15 (the harness enumerates controllers) and the F5 scan with the real routes (W5-5); I would ask Warden-06 to re-review once Shares and Approval exist.
