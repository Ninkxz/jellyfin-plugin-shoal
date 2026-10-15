# WARDEN review 04: DESIGN v2.2 (client compatibility, Scout G1-G8)

Reviewer: WARDEN (security). Date: 2026-10-15. Mode: read-only. Nothing was built, run or deployed. The only file I wrote is this one.

Inputs read in full: `git diff 210298d b424da0 -- docs/DESIGN.md` (715 lines), the v2.2 sections it touches (3.5, 6.1-6.12, 8.5-8.7, 4.1, 11.2-11.3), `docs/research/scout-02-clients.md`, `docs/tests/pilot-02-tsec22-tcli-baseline.md`, `docs/DECISIONS.md` (D12), `docs/ui-spec.md` 8.3.12 (and its ship gate), my own `warden-02.md`. I also read the current `PeerClient.cs`, `PeerCallProfile.cs` and `SettingsValidator.cs`, because three findings depend on how they already behave.

Source checks use the local clone of Jellyfin at tag `v12.1` (paths relative to `jellyfin/`). Evidence labels as in earlier reviews: **Verified (source)** = I read the line; **Derived** = follows from source plus behavior I did not execute; **Pilot** = taken from Pilot's lab report. Every exploit below is a reading of the code, not a run.

## Summary

| | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 4 (W4-1 to W4-4) |
| Low | 11 (W4-5 to W4-15) |

Nothing here reopens the v2.1 design. The new surface is smaller than it looks: the client query is closed (nothing from it reaches the Peer), the grant model cannot be used across users, the JSON rebuild is sound, and the 401 rule for stock apps holds with no gap I could find. The four Mediums are all in the places where v2.2 added *waiting* and *bookkeeping*: the subtitle handler lets one ordinary user take a Peer offline for everyone on Home (W4-1), the grant key rests on a false claim about `DeviceId` (W4-2), one sentence in 6.6a could make an implementer authorize one item and serve another (W4-3), and the `ActiveEncodings` row lets a user shed a slot while a direct-play stream keeps running (W4-4). All four fixes are a few lines of spec.

## Verdicts

| Task | Verdict | Conditions (all small; none needs a redesign) |
|---|---|---|
| **K13** intercept filter | **GO** | W4-12 (query/GUID parsing rules) and W4-14 (logging ban list) in the same PR; extend T-CLI-6 as listed in the test section. |
| **K14** PlaybackInfo and grants | **GO** | W4-2 (DeviceId is client-chosen), W4-6 (clamp and capacity order), W4-10 (profile predicate) land with the code. |
| **K15** static stream, slots | **GO** | W4-4 and W4-6 (slot release and bookkeeping rules). |
| **K16** PlaylistGrammar | **GO** | v2.2 does not touch the grammar. Warden-02 section 4.6 still stands. |
| **K17** HLS controller, grant lifecycle | **GO** | W4-11 (carve credentials out of the "never a 4xx" sentence), W4-4, W4-12. |
| **K18** subtitles | **NO-GO until W4-1, W4-3 and W4-7 are written into DESIGN 6.6a/6.9/6.10** (spec edits, about a page). Then GO, with W4-8, W4-9, W4-10 applied in the code. | The handler is where the only new cross-user effect lives, and its structure (a per-peer gate and a breaker rule) should be decided before it is coded. |

`ui-spec.md` 8.3.12 help text: **may ship after the owner confirms G1 and the three edits in W4-15.** It does not invite anyone to weaken authentication.

## Answers to the eight questions

| # | Question | Answer | Finding |
|---|---|---|---|
| 1 | SubtitleHandler | Design is sound on parsing and rebuild. **No cross-user leakage or poisoning** from coalescing, provided CallerContext runs before the join (it does) and the key includes the peer and media source (it does not say so). The 120 s wait is the problem: it shares a 16-connection pool and the peer-wide breaker with everything else. 6.6a step 1 is ambiguous about which id is authorized. | W4-1, W4-3, W4-7, W4-8, W4-9, W4-12 |
| 2 | G2 client query | **No path from a client query to the Peer** other than the validated integers and the mapped subtitle values. "Grant wins" is right. One sentence is wrong for credentials (duplicate `ApiKey` is a 401 from core). | W4-11 |
| 3 | G8 grants | **No cross-user hijack, eviction or starvation by spoofing `DeviceId`**: every key and lookup includes the authenticated user id. But `DeviceId` is not "filled from the token's device row": it is whatever the request header says. Nobody can exhaust the global 200 alone (the user cap is 8). | W4-2, W4-6 |
| 4 | G3 bitmap profile | The predicate is enough *given core's matching rule* (verified), but it is hand-written and should use core's own function plus an allowlist of formats Shoal can serve. `application/json` row is fine. | W4-10 |
| 5 | G6/G7 | Sound. Content-Type is from the catalog, the Container cap is right, the Http Path fallback is gone. | none |
| 6 | G1 and 8.3.12 | 401 holds. The help text is honest and does not push admins toward weaker auth; it claims "Works" for apps no test covers yet. | W4-15 |
| 7 | Credentials | Intended and safe. Header or `ApiKey` or both, header wins, `api_key` only with legacy auth, duplicate `ApiKey` is a 401 from core. Do not "fix" it. | W4-11, W4-14 |
| 8 | Pilot findings | Restart gap: real, closed by the lock or the anchor, "60 s" is overstated. Phantom sessions: no new weakness, one runbook line. Stub leak: **closed by two independent layers**, with the exceptions listed in section 8. | W4-5, W4-13 |

---

# Findings

## Medium

### W4-1. Subtitle-file calls are unbounded against the Peer and can open the peer-wide breaker for every user of Home (Medium)

**DESIGN:** 6.6a step 7, 6.9, 6.10 ("Subtitle file" row, 120 s headers / 150 s total), 6.10 "Circuit breaker"; code: `PeerClient.cs:383` (`MaxConnectionsPerServer = 16`, one handler per peer), `PeerClient.cs:162-189` (header timeout and any 5xx call `Breaker.RecordFailure()`).

**Why it matters.** v2.2 lets Home trigger *embedded* subtitle extraction on the Peer without a stream slot and without a grant that proves anything beyond "this user called PlaybackInfo". The Peer side (Verified, `SubtitleEncoder.cs:515-560, 647-700`) answers the first request for any text track of a title by running **one ffmpeg over the whole file that extracts every extractable track at once, PGS and VobSub included**, then serializes later requests for the same media source on a per-path lock (`_semaphoreLocks`). A cold request therefore holds a Peer connection for minutes on a large remux (Moonfin's own comment, SC§2.2). Four things follow:

1. **Pool starvation.** Home allows 30 subtitle requests per user per minute, each up to 150 s, and the coalescing key (same item, index, fmt *and mapped values*) is trivially varied with `{ticks}` or `endPositionTicks`. Fifteen requests with different tick values for one cold title all pass Home's coalescing, all reach the Peer, and all block on the Peer's per-path lock, each holding one of the 16 pooled connections. Then PlaybackInfo (12 s), HLS segments and the health probe queue behind them and time out.
2. **The breaker turns that into an outage for everyone.** Header-phase timeouts and every 5xx call `RecordFailure()`; three in a row open the circuit (6.10), after which "nothing else is sent to the peer" and probes back off to 300 s. One user with a normal login can therefore mark a friend's server `Offline` for all of Home's users, repeatedly, with about 7 requests a minute. The honest case is nearly as bad: a title with one track the Peer cannot extract returns a Peer 500, and a client that retries three times opens the breaker the same way.
3. **Client aborts waste the work.** The Peer passes the request's cancellation token into extraction, so an aborted request kills ffmpeg and the next request starts from zero. A TV app with a 30 s timeout against a 3 minute extraction never succeeds and keeps the Peer busy. (Derived from `SubtitleEncoder.cs:515` taking `cancellationToken`.)
4. **6.6a step 10's rationale is wrong for the cost argument.** "Extraction also costs the Peer a full read of the file" is not a reason to refuse `sup`: any text-track request already triggers the full read and the PGS extraction. The 404 for bitmap files stays right for the parser-attack reason only.

**Fix (spec, before K18):**
- Add a **per-peer concurrency gate for subtitle-file calls** (suggest 2, queue wait counted inside the call budget, 429 `Retry-After` beyond it) and a **per-user cap of 1 cold subtitle request** at a time. These calls get their own `PeerCallProfile` (the current `ExternalSubtitle` profile is 30 s total and must not be reused) and must not share the 16-connection budget of control and media calls: give the gate its own `SemaphoreSlim` and keep at least 8 of the 16 connections unreachable by it.
- **Subtitle-file outcomes never count toward the breaker** (`PeerCallProfile.CountsTowardBreaker = false`): neither timeouts nor 5xx. A hung extraction is not evidence that the Peer is down; connect failures still count.
- **Single-flight per peer item, not per request key.** The cold extraction is per media source, so the gate key is (peerKey, peerItemId, peerMediaSourceId): the second request for any index, format or tick value on that item waits for the first one to finish and then makes its own (now cheap) call, instead of occupying a connection beside it.
- **Detach the shared Peer call from the first client's abort** (reference-counted: cancel only when the last waiter has gone *and* the call has been running less than the gate wait), so an impatient client does not restart the extraction forever.
- Update the 6.6a step 10 text.

### W4-2. `DeviceId` is whatever the request header says, not "the token's device row" (Medium)

**DESIGN:** 3.5 (grant key, "core fills it from the token's device row"), 6.2 step 2 (profile fallback via `GetCapabilities(caller.DeviceId)`), 8.5 step 8 (`Caller.DeviceId`), 6.9.

**Verified (source).** `AuthorizationContext.cs:117` sets `authInfo.DeviceId` from the `Authorization` header's `DeviceId` field; the device row's id is used only when the header omits it (`:146-148`). `CustomAuthenticationHandler.cs:65` copies that value into the claim. Nothing compares it with the token's own device. So any logged-in user can present any `DeviceId` on any request.

**What this does and does not break.**
- *Cross-user hijack or eviction: not possible.* Every grant key, lookup, eviction and replacement in 3.5 includes `localUserId` from the principal, and the HLS, reporter and subtitle paths check it (R13). Spoofing `DeviceId` only lets a user create more keys *for themselves*, which the cap of 8 bounds. The replacement rule "a new PlaybackInfo replaces that device's slot-free grant" becomes a courtesy for honest clients, not a control: a user who rotates `DeviceId` per call simply accumulates up to 8 grants and then evicts their own oldest slot-free one. That is acceptable, but 3.5 must not describe the id as trustworthy.
- *Stored-capabilities fallback.* `DeviceManager.SaveCapabilities` is keyed by `deviceId` alone (`SessionManager.cs:1901`), and `SessionController` lets any user write it for any `DeviceId` string. So the profile Shoal mirrors in 6.2 step 2 can be one that another user's device (or an attacker) registered. It still goes through the rebuild, so the impact is "who decides codec support", not disclosure, and core has the same property. Cheap to avoid anyway.
- *Unbounded length.* The claim is stored in every grant (up to 8 per user).

**Fix (K14, K12/K6 for the Caller type):** reword 3.5 and 8.5: "`DeviceId` is client-chosen; it is a convenience key within one user, never an identity". Cap it at 128 characters before it is stored or used as a key (hash longer values). Take the capabilities fallback only when the device row for that id belongs to the caller (`IDeviceManager.GetDevices(new DeviceQuery { DeviceId, UserId })`), else treat as "no profile". Add a test: user B presents user A's `DeviceId` and cannot read, replace or evict A's grant (T-CLI-8 variant).

### W4-3. 6.6a step 1 can be read as "authorize the query item, serve the route item" (Medium)

**DESIGN:** 6.6a step 1 ("CallerContext ... for the route item (or the `itemId` query, 6.1)") versus step 2 ("Route values only") and 6.1 "Subtitle route, obsolete query overrides".

**Verified (source).** Core's action lets the obsolete `itemId` query override the route (`SubtitleController.cs:226-229`) and its route is anonymous, so CallerContext is the only gate.

**Exploit if an implementer follows step 1 literally.** The attacker is authorized for federated item B and has a grant for it, but not for item A (hidden by the access mode or a parental limit). They request `GET /Videos/{A}/{A}/Subtitles/2/Stream.vtt?itemId={B}`. If the authorization is run on the query id (as core would resolve it) while the data comes from the route id, or the reverse in some other branch, Shoal serves one item under the other's permission. The v2.2 text only specifies the case "non-federated route id with a federated query id → 404". It says nothing for "federated route id with a *different* federated query id".

**Fix (K13/K18):** one rule in 6.1 and 6.6a: *the id that is authorized is the id that is served, and it is the route id.* If an `itemId` query value is present and is not equal to the route id (after parsing both with `Guid.TryParse`), answer 404 before CallerContext and before any Peer call, whichever of the two is federated. Delete "(or the `itemId` query)" from step 1. Add the cell to T-SEC-15 and K18: federated A in the route, federated B in `itemId`, caller admitted to B only → 404, zero fakepeer requests.

### W4-4. `ActiveEncodings` delete releases the slot of a stream that is still running, and is not rate limited (Medium)

**DESIGN:** 6.1 last row (v2.2 "release that grant's slot"), 6.9, 6.3 step 3, 6.11 "Scoped kill".

**Why it matters.** The slot is what bounds a user's share of the Peer's bandwidth (W§A-5, T-SEC-17). v2.2 made the delete release it "so a track switch's new grant can take it". For an HLS grant that is right: the forwarded scoped delete kills the Peer's job, and a later segment request has to win a slot again. For a **direct-play** grant it is a bypass: the slot is keyed by (user, item) and "taken at the first request, shared by that stream's Range requests" (6.3), so the running connection is never re-checked. A user starts item X (slot 1), calls `DELETE /Videos/ActiveEncodings?deviceId=a&playSessionId=<grant's PlaySessionId>` (the id is in their own PlaybackInfo answer), starts item Y, repeats. `MaxStreamsPerUser`, the half-of-the-peer rule and the per-peer limit of 4 stop mattering; the only remaining bound is "6 concurrent connections per user", which is enough to fill a friend's uplink. The route also has no limiter, and every call with a matching grant makes Home send a Peer request, so it is a free amplifier.

**Fix (K13 row, K15):** release the slot on this call **only for grants whose `playMethod` is Transcode and only after the scoped delete was sent**. For direct play it releases nothing (the slot is released by connection close plus the 2 minute grace, or `PlaybackStopped`). Forward to the Peer only when the grant has actually started a Peer job (some segment was served); otherwise answer 204 without a Peer call. Add a per-user bucket (10 per minute). Test: direct-play stream running, `ActiveEncodings` delete, then the user's third stream must still get 429.

## Low

### W4-5. "The window after a restart is at most 60 s" overstates what Home can promise (Low)

**DESIGN:** 4.1 (the anchor bullet, `DESIGN.md:727`), 4.4, 6.11 "Session anchor", 8.3 T42.

**Pilot** confirms the gap (D2, D3: with limit 1, the session list empty and the lock off, both self-minting routes succeed), confirms the anchor closes it instantly (D4), and shows that with the lock on the gap cannot be used (D7a-d, an inference Pilot flags as not exhaustive). The 60 s bound itself is untested. Reading the design against that:

- The anchor rides on the 60 s health call. It is not sent while the circuit is open (probes back off to 300 s, 6.10), while Home is down or restarting, or while the peer is paused or disabled in Shoal. In those cases the window is minutes to unbounded. Only someone who already holds a token can use it, and the token then dies with the friend's own response (E1: an admin password change signs out every device; deleting the account does too), so I rate it Low. It is still a security claim the checklist makes, so it should be true.
- Home cannot see whether step 8 (the device lock) was applied, so the lock cannot be counted as Home's control. It is the friend's.

**Fix:** (1) send the anchor **on the first successful probe after any outage and at Home startup**, not on the next tick; (2) rewrite the sentence as "protection resumes within 60 s of Home next reaching the Peer"; (3) say in the checklist that steps 8 and 9 are a pair and neither is optional (Pilot section 7 point 1); (4) T-SEC-22(d) gets a timed variant when K20 lands.

### W4-6. Slot and grant bookkeeping needs three more sentences (Low)

**DESIGN:** 3.5, 6.2 step 1, 6.3 step 3, 6.9; code: `SettingsValidator.cs:35`.

- **`hasSlot` is undefined when two devices of one user stream the same item.** The static slot is keyed (user, item) (6.3), grants are keyed (user, item, DeviceId). State which grants "hold" the slot (suggest: all live grants of that (user, item) while the slot is live). Otherwise one device's grant can be evicted or replaced mid-stream, and the subtitle lookup (6.6a step 4) breaks for it.
- **The invariant "a slot-free grant always exists" is enforced only by a clamp that is not there.** 3.5 relies on `MaxStreamsPerUser` being clamped to 1..4, but `SettingsValidator.cs:35` clamps to 1..10. With the cap of 8, a setting of 8-10 lets a user's grants all hold slots and the "evict the oldest slot-free grant" rule has nothing to evict. Fix the validator (1..4) **and** make the eviction code safe on its own: if no slot-free grant exists, refuse the new grant (`RateLimitExceeded`), never evict a slotted one.
- **Capacity check order.** 6.2 step 1 checks "grant capacity" before replacement and eviction. When the global 200 is reached, a user who would only have replaced their own slot-free grant (net zero) is refused. Do replacement, then own-eviction, then the global check. (A single user cannot reach 200 alone because of the cap of 8; it takes 25 accounts. With a handful of users this is not a real lever.)

### W4-7. Coalescing is safe against leakage if five rules are written down (Low; answers question 1)

**DESIGN:** 6.6a step 7, 6.9 ("a coalesced request counts once per caller").

No cross-user leakage or poisoning in the design as written: CallerContext (all 7 steps) and the per-caller grant lookup run before a request may join, the content depends only on (peer, item, source, index, fmt, mapped values), and Home attaches no per-user data to a subtitle body. For K18 to keep it that way:
1. The key **includes `peerKey` and `peerMediaSourceId`** ("same peer item" could be read as the id alone; a hostile peer chooses its own item ids).
2. The shared result is an **immutable, fully validated buffer** (after the 8.6 checks and the JSON rebuild); each joiner is served from a copy or a read-only view, never from a shared stream being read at different offsets.
3. **No cache after completion** (or, if one is added, key it with the peer's ETag and a short TTL). Coalescing is for requests in flight.
4. A failed shared call is **not retained**: the next request tries again (subject to W4-1's gate).
5. The key excludes anything caller-specific, and each joiner's rate-limit token is charged separately (already in 6.9).

### W4-8. JSON subtitle parse caps the event count after building the whole tree (Low)

**DESIGN:** 6.6a step 9.

10 MB of `{"Id":"1","Text":"","StartPositionTicks":0,"EndPositionTicks":0},` is roughly 170,000 events; `DeserializeAsync` into a typed list allocates all of them (tens of MB) before the "at most 20,000 events" rule applies. A hostile Peer can repeat that per request, and the gate in W4-1 only limits how many at once. Parse with `Utf8JsonReader` over the bounded stream and stop with 502 at event 20,001. Also pin the reader options (no comments, no trailing commas, default number handling) so "strict" means the same everywhere, set `UnmappedMemberHandling = Disallow` or say explicitly that extra members are ignored (the K18 fixture list says "dropped, stripped or 502"), and serialize with the default encoder (never `UnsafeRelaxedJsonEscaping`). Test property for the fuzz: the `Text` output matches `^(?:[^<>]|</?[ibu]>)*$` for every input. (Verified: core's own writer, `JsonWriter.cs`, emits exactly the four members the design lists.)

### W4-9. VTT and SRT cue text goes through unsanitized while JSON cue text is rebuilt (Low)

**DESIGN:** 6.6a steps 8-9, 8.6 "Subtitle body checks".

Step 9's argument ("the same JSON is a public core format other clients may render") applies equally to `vtt` and `srt` from the same handler: Moonfin's TV apps run one sanitizer over cue text whatever the file format, and other clients render these formats differently. The body checks for VTT, SRT and ASS look at the first line or the first 4 KB only. Embedded tracks now reach all clients through these formats. This falls under the accepted "a friend's server can send malformed media to your players" residual (W2 N-14), so Low; but either apply the same cue-text allowlist (`<i> <b> <u>` only) to VTT and SRT cue bodies (cheap, line-based; leave ASS override tags alone) or write the specific reason JSON differs into 6.6a.

### W4-10. Use core's own predicate and an allowlist for subtitle formats (Low; answers question 4)

**DESIGN:** 6.2.2 ("No external bitmap subtitles"), 6.6a step 5.

**Verified (source).** The Peer picks `External` for a stream only when `stream.IsTextSubtitleStream == MediaStream.IsTextFormat(profile.Format)` and, without conversion, `stream.Codec == profile.Format` (`StreamBuilder.cs:1600-1618`), plus the VobSub `.mks` case. So removing entries for which `IsPgsFormat`, `IsVobSubFormat` or `dvbsub` hold *is* enough to stop the Peer choosing `External` for the bitmap codecs core knows. Two small gaps: the hand-written list omits the exact string `sub`, which `IsTextFormat` treats as bitmap (`MediaStream.cs:741-752`), and `IsTextFormat` is a **blocklist** (anything unknown, such as `xsub` or `hdmv_text_subtitle`, counts as text). Step 5 relies on it to decide what Home may fetch.

**Fix:** drop every `External` entry whose format is not in the set Shoal can actually serve (`srt`, `subrip`, `ass`, `ssa`, `vtt`, `webvtt`, `js`, `json`), which subsumes the bitmap rule and also stops `smi`, `ttml` and friends from producing a `DeliveryUrl` nobody serves. In 6.6a step 5 use the same allowlist for codecs rather than `IsTextFormat`. The body checks of 8.6 remain the backstop. `application/json` in the 8.6 table is fine (the body is rebuilt, `nosniff`, CSP sandbox and `attachment` still apply).

### W4-11. The "never a 4xx" sentence in 6.4 is false for credentials (Low; answers questions 2 and 7)

**DESIGN:** 6.4 "Client query" (first sentence and the `ApiKey` / `api_key` bullet), 8.5 step 1, T-CLI-3, T-CLI-14.

Pilot found lowercase `api_key` and a duplicated `ApiKey` give 401 on the spike proxy. **That is the intended and correct behavior**, and Shoal must not change it. Verified: core reads the token from the header, then `queryString["ApiKey"]` (case-insensitive key; a duplicate becomes the joined string `a,b`, which matches no device), and reads `api_key`, `X-Emby-Token` and `X-MediaBrowser-Token` only when `EnableLegacyAuthorization` is on (`AuthorizationContext.cs:93-110`). Shoal never parses credentials (8.8), so it inherits those rules, and the `[Authorize]` on `/Shoal/Hls/...` and on `Shoal/Subtitles` answers before Shoal's code. The supported clients do not trigger it (Moonfin skips URLs that already carry a key, Smart-TV builds its own).

Edit 6.4 to say: "an unknown, extra, repeated or mutated key never causes a 4xx *from Shoal*; the credential keys are judged by core first (a repeated `ApiKey` or an `api_key` without legacy authorization is a 401)". Replace the `ApiKey` / `api_key` bullet accordingly, and make T-CLI-3 expect 401 for the duplicate-key and `api_key` variants (Pilot's table is the right expectation). Make the `clientIndexMismatch` counter admin-only wherever it is exposed (it is a debug aid, not a metric), with no value labels. For the repeated `runtimeTicks`-style keys the rule needs one more clause: take the first value or answer 400, but say which (the rule currently says both "never a 4xx" and "validated, else 400").

### W4-12. Parse the query and the GUID exactly as MVC does (Low)

**DESIGN:** 6.1 "Filter mechanics".

Three implementation traps for K13, all about the filter deciding "not federated" when core will still resolve the item:
- **Use `Request.Query`, never `QueryString.Value`.** Core's `QueryStringDecodingMiddleware` / `UrlDecodeQueryFeature` re-parses a fully percent-encoded query (`?static%3Dtrue%26...`) into real keys (`Jellyfin.Api/Middleware/UrlDecodeQueryFeature.cs`). The filter and core must read the same collection.
- **Check every value of a repeated key.** The MVC binder takes the first value of `itemId`, while `Query["itemId"].ToString()` of two values is `a,b`, which does not parse. A route id that is local plus `itemId=<federated>&itemId=x` could reach core. For the subtitle template: federated if *any* parsed value is federated; any unparseable extra value on a request whose route id is federated stays Shoal's.
- **Use `Guid.TryParse` (all formats, trims whitespace), the same grammar as the model binder.** `ParseExact("N")` or `"D"` would let `{guid}` or `(guid)` through to core. Add fixtures for B, P and X formats, upper case and `%20` padding on every route in 6.1.

### W4-13. Phantom sessions on a stock Peer: one runbook line and one watch rule (Low; Pilot finding)

**DESIGN:** 4.5, 4.1 step 9, K20.

Pilot: `Sessions/Capabilities/Full` creates token-less sessions that are invisible in Dashboard > Devices, survive device deletion and an admin password change, and clear only on a restart or user deletion. They carry no credential, so they cannot be used to authenticate and they only make the session limit stricter. Three consequences: (1) a token holder can grow the Peer's in-memory session list without bound (a stock-Peer residual of the accepted "hostile Home" kind; a Shoal Peer bans the route); (2) the friend's step-6/8 check ("exactly one device") cannot see them; (3) after a suspected compromise the stock-Peer runbook already says delete the account, which clears them, so add one sentence: "restarting Jellyfin also clears hidden sessions". For K20, have the foreign-device watch also alert when `GET /Sessions` shows more sessions for the share user than the one device, since that is the only place these are visible.

### W4-14. Logging: say what is banned, and note that core logs full URLs at Debug (Low)

**DESIGN:** 6.1 "Logging", 11.5 README.

"Shoal never logs a client query string" needs teeth: add to `BannedSymbols.txt` (K6/K13) `HttpRequest.QueryString`, `GetDisplayUrl`, `GetEncodedUrl`, `GetEncodedPathAndQuery` and `HttpContext.Request.Path`+query formatting in any `ILogger` call outside one helper that logs the route template. Verified: core's own `ResponseTimeMiddleware.cs:54-56` logs `GetDisplayUrl()` (the full URL with `ApiKey`) for every response slower than 500 ms **at Debug**; every cold subtitle fetch and the start of every long stream trips it. That is core's line, not Shoal's, so the README sentence "redact Debug logs, they contain user tokens" (11.5) must stay, and T-CLI-14 should state that its grep covers Shoal's lines only.

### W4-15. Help text 8.3.12 and the README: three edits before it ships (Low; answers question 6)

I read the block in `ui-spec.md` 8.3.12 and the matching text in DESIGN 6.12. It is calm, truthful about the mechanism ("Shoal never sends a friend's video to a request that isn't signed in"), tells the admin nothing is broken on their side, and offers no setting or workaround. It does **not** invite anyone to weaken authentication. Edits:

1. **"Works" must match what has been tested.** The list asserts Jellyfin Media Player (Scout marks it "assumed, unread", and no test names it), the official Android app's own player (T-PLAY-5 covers it), and Moonfin on Xbox, Vega, Fire TV, tvOS and desktop (T-CLI-13 names iOS, Android, Android TV, one Smart-TV and Roku). Either add the missing clients to T-CLI-13 or soften those rows ("should work") until Pilot has run them. The ship gate already waits for Warden and the owner; add "and T-CLI-13 for every row in Works".
2. **Add one sentence to the README (not the in-app text):** "Do not work around the apps that don't work by putting a proxy in front of Jellyfin that adds a token or API key to requests that don't have one. That would turn Shoal's protection off for everyone who can reach the proxy." Admins who hit "won't play in Swiftfin" are exactly the people who will try this, and it is the one workaround that actually exposes friends' titles.
3. **"not all of them yet"** reads as a promise of a fix, while Option A is parked behind a security review and may never ship. Prefer "Titles from {Server} play in most Jellyfin apps, but not all of them." The same word is in the README text of 6.12.

---

# Pilot's findings (question 8), item by item

| Pilot finding | Reading |
|---|---|
| Restart gap real when the lock is off; anchor fixes it; 60 s untested | Agreed and consistent with W4-5. Treat steps 8 and 9 as a required pair. The anchor needs to fire on first contact after an outage, not on the next tick. |
| Refusal text "maximum number of sessions" is only in the Peer's log | Design impact is copy, not security: 4.1 step 5 and 4.2a must classify "Authorize 403 / AuthenticateWithQuickConnect 404 after a code was entered" as "session limit?" and never parse a message. Lumen and K5. |
| Phantom sessions | W4-13. No new weakness. |
| A self password reset leaves an empty password (limit 0) and keeps other tokens | Already covered by v2.1 (the limit refuses the login while a session exists; the admin password change signs everything out, E1). |
| Core's anonymous `/Videos/{id}/stream`, `Audio/{id}/stream`, `Items/{id}/Download|File` leak the stub text including the **Peer item id** on the spike build | **Closed by two independent layers in the v2.2 design.** (1) *Content:* the v2 sentinel is `http://shoal.invalid/v1/<stubKey>` (3.4); the Peer item id, host and token are not in the file at all, so a leak of the text discloses a random key that is meaningless outside Home's catalog. This holds even if Shoal is disabled, in recovery mode or fails to load, which is why T-SEC-6 must keep checking that the stub file contains nothing else. (2) *Routes:* K13's rows return 401 (no credentials, static and subtitle), 403 (`Download`, `File`, API keys), 404 (every `Audio/...` and HLS route) for federated ids. Exceptions to note, none exploitable: a stub item created after the index snapshot and before it refreshes falls through to core and discloses only the stubKey (3.5 says the filter applies the path rule before the index is ready; apply it for any miss on a freshly scanned item as well); `Items/{id}/Download` and `File` have core's own `[Authorize]` ahead of the filter, so anonymous callers get core's 401 and only download-permitted users reach Shoal's 403. |
| Core's `Videos/{id}/master.m3u8` and `Audio/{id}/master.m3u8` answer 200 with a dead playlist | The 404 rows are needed, not cosmetic. Covered. |

---

# Things I checked and found sound

- **G2.** The Peer request is built from the grant plus validated integers only (6.11 table). No header, query key or body field from the client is copied. `AudioStreamIndex` / `SubtitleStreamIndex` mismatch changes nothing. I looked for a way for `StartTimeTicks`, `PlaySessionId`, `DeviceId`, `MediaSourceId` or an unknown key to reach the Peer and found none; segment `n`, `ext`, `r`, `l` and subtitle `s`, `e` are validated integers and enum values.
- **G3 JSON rebuild.** Strict typed parse, depth 8, re-numbered ids, tick range checks, text allowlist with the remaining `<`/`>` removed, then serialized from typed values, then the canary. I tried `<<script>`, `<scr<i>ipt>`, `<<i>b>`, entity forms and nested allowed tags: the rule "no `<` survives except in the six exact tags" holds provided the final pass runs after the allowed tags are protected (state the invariant as a test, W4-8).
- **G6/G7.** Container validation (40-character cap, comma lists whole), Content-Type from the catalog container via the closed table, `Protocol=File` / `IsRemote=false` / `Path=null`, `SupportsDirectStream=false`. Nothing in the answer lets a client play a peer-chosen URL.
- **G1.** Credential-less requests get 401 from CallerContext step 1 on every federated route; API keys get 403; there is no anonymous branch anywhere in 6.1-6.6a. The route coverage in 6.1 matches the controllers I listed in `Jellyfin.Api` (stream, Audio, HLS, Trickplay, Attachments, Download, File, subtitle). `Audio/{id}/Lyrics` resolves `Audio` items only, so a movie stub returns 404 in core.
- **G8 cross-user.** All keys and lookups include the user id from the authenticated principal, and the per-user cap of 8 and the global 200 mean one account cannot starve another through grants. Slot starvation by *holding* slots (2 per user, 4 per peer) is the accepted v2 trade-off, unchanged.
- **PlaybackInfo merge rules.** Query wins, body fills, `UserId` ignored, bad values 400. Matches `MediaInfoController.cs`.

# Tests to add or change (for Pilot, Kiro)

| Test | Change | Gates |
|---|---|---|
| T-SEC-15 / K18 | Federated route id A + `itemId=B` (admitted to B only) → 404, zero fakepeer requests; same with `itemId` repeated (`itemId=local&itemId=federated`), B/P/X GUID formats and `%20` padding on every 6.1 route | K13, K18 |
| New T-CLI-16 | Cold-extraction storm: 20 subtitle requests with different `ticks`/`endPositionTicks` for one title against a fakepeer that holds 120 s; PlaybackInfo, an HLS segment and the health probe for another user must still complete, the breaker must stay closed, concurrent Peer subtitle calls never exceed the gate | K18 |
| T-CLI-4 | Add: a Peer 500 on the subtitle route three times in a row does not open the breaker; client aborts mid-extraction do not restart it forever | K18 |
| T-CLI-3 / T-CLI-14 | Duplicate `ApiKey` and `api_key` expect **401** (core), not 200; the log grep says "Shoal's lines" | K17, K13 |
| T-CLI-6 | Add anonymous and download-permitted-user variants of `Audio/{id}/stream?Static=true`, `Items/{id}/Download`, `Items/{id}/File`; assert the body is empty and contains no stub text | K13 |
| T-CLI-8 | Add: user B presents user A's `DeviceId` on PlaybackInfo, subtitle and `ActiveEncodings` calls and cannot read, replace, evict or kill A's grant or stream; `MaxStreamsPerUser` set to 10 in the XML is clamped | K14 |
| New T-SEC-25 | Direct play running, `DELETE ActiveEncodings` with its PlaySessionId, then the user's third stream still gets 429; the delete is rate limited | K13, K15 |
| T-SEC-22(d) | Timed anchor variant once K20 lands: Peer restart with the circuit open, anchor sent on first contact | K20 |
| T-CLI-13 | Add Jellyfin Media Player and the official Android app, or soften the Works list | release |

# Residuals recorded, not findings

- A friend's server can still send malformed subtitle or media bytes to your players (N-14); W4-9 only narrows the cue-text part.
- A hostile Home that holds a stock Peer's token can read whole files and trigger extraction for the whole library (accepted in 6.9 and 4.5). W4-1 is about the *ordinary-user* cross-user effect, not that.
- `LiveStreams/Open` with a body-only `ItemId` still falls to core (accepted, W§A-20.3).
- Slot holding by a pair of cooperating users still starves a third (v2 trade-off).
