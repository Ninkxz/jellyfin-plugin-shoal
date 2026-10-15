# Warden review 06: K3 to K13 (and K14), committed code only

Reviewer: Warden (read-only). Date: 2026-10-15. Status: complete (K3 to K13 at 172f590, plus K14 at 88edeec).
Method: `git archive main` (172f590) into a scratch folder, read there; Kiro's working tree untouched. In the scratch copy only: full test run (1957 passed, 6 skipped, Release, analyzers on), and two throwaway probes (hostile JSON through MetadataSanitizer; a probe file calling banned and unbanned APIs to see what RS0030 rejects). Nothing was written to the repo except this file.

## K2.1 re-check (M-1..M-3 from warden-03): FIXED
- M-1: `ResolveKey` (SecretStore.cs) now takes the raw environment value; empty or blank counts as set, parses as invalid, `BadKey`, `master.key` neither read nor written. Tests cover `""`, `"   "`, empty `_KEY_FILE`, empty file, both-empty = Ambiguous.
- M-2: `Redact.Scrub` cuts to 4096 chars, all patterns on `RegexOptions.NonBacktracking` with a 100 ms timeout, returns `[text omitted]` (never the raw text) on timeout.
- M-3: prefixed key names, `%3D`/escaped-JSON separators, Bearer/Basic, userinfo, QC code, invite with/without prefix, dashed GUID, any 32+ hex run (any case). Idempotence tests exist.
No new finding on K2.1. (Info: `PeerCredential` now enforces token and device id shapes.)

## Findings so far (severity order; file:line are in main@172f590)

### M-1 [Medium] A hostile peer can crash catalog sync with a `null` array element, and one crash stops the sync of every peer behind it
- Where: Sync/MetadataSanitizer.cs:241 (`dto.Studios?.Select(s => s.Name)`), :294 (`person.Type`), :368 (`MediaStreams.FirstOrDefault(s => s.Type...)`), and `source.Id` for a null `MediaSources` element; caught nowhere: Sync/SyncRun.cs:416-429 catches only `PeerCallException`; Sync/CatalogSyncService.cs:131 (`RunAsync`) catches only `PeerCallException`; `SyncAllAsync` is one `foreach` with no per-peer catch; Sync/SyncScheduler.cs catches generically and sleeps 6 h.
- Why: `System.Text.Json` accepts `"Studios":[null]`, `"People":[null]`, `"MediaSources":[null]`, `"MediaSources":[{"Id":"..","MediaStreams":[null]}]` into the Jellyfin DTO arrays. I reproduced a `NullReferenceException` for all four shapes by running the exact deserializer options (`JsonDefaults.Options`, depth 32) into `MetadataSanitizer.Sanitize`. T-SYNC-6 fuzz only fuzzes strings, never array shape.
- Exploit: a paired (hostile or compromised) friend serves one item with `"Studios":[null]`. Every sync that reaches that item throws out of `SyncRun`, out of `CatalogSyncService`, aborts `SyncAllAsync` before the peers listed after it, and `catalog.json` is never saved for the hostile peer (silent: `LastSync` stays stale). Result: durable sync denial for every other friend whose record sits after the hostile one in `state.json`, repeated every cycle. A manual sync of the hostile peer returns an unhandled exception to the admin API.
- Fix: (1) null-guard every element in `MetadataSanitizer` (`Where(x => x is not null)`), or reject the item and count it skipped; (2) catch `Exception` (not OOM) per peer in `CatalogSyncService.RunAsync` and per item in Phase C, record `SyncReport.Failed("internal")`, and make `SyncAllAsync` continue to the next peer; (3) add a fuzz test with `null` elements in every array and dictionary value of `BaseItemDto`.

### M-2 [Medium] The banned-API analyzer does not reject `HttpResponse.ContentType` writes, and has large gaps around the response-hardening rule (K7 acceptance not met)
- Where: Src BannedSymbols.txt `M:Microsoft.AspNetCore.Http.HttpResponse.set_ContentType(System.String)`. Probe result in a scratch copy: with a file calling `Response.ContentType = "text/html"` the build **succeeds**; `Response.Headers[...]`, `Headers.Append`, `Ok()`, `StatusCode(int)`, `GetAuthorizationInfo`, `GetItemById(Guid)`, `QueryString`, `Path`, `GetDisplayUrl`, `GetEncodedUrl`, `HttpClient.GetStringAsync`, `Random` are rejected as designed.
- Not rejected either: `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (DESIGN 8.8 row; the spike `PlaybackInfoRewriter.cs` still uses it), `Response.WriteAsJsonAsync` (sets ContentType internally), `Response.GetTypedHeaders()`, `Response.Cookies`, `Response.Redirect`, `Response.WriteAsync`, `ControllerBase.File/PhysicalFile/Redirect/Problem/Created*/Accepted*`, `new FileStreamResult/JsonResult/ObjectResult/OkObjectResult`.
- Why it matters: DESIGN 8.6 says handlers cannot set Content-Type or copy peer headers; K7 acceptance says "the analyzer rejects direct ContentType writes elsewhere". K15 (static stream), K17 and K18 are exactly the code that will be tempted to use `FileStreamResult`/`WriteAsJsonAsync`/`Response.ContentType`. The unit test (`TheBannedSymbols_*`, `PeerClientCoreTests.TheBannedSymbolsFile_*`) only does `Contains` on the text of the file, so it passes with a ban that does nothing. The `#pragma warning disable RS0030` in ResponseWriter.cs:156 protects a ban that never fires.
- Fix: use `P:Microsoft.AspNetCore.Http.HttpResponse.ContentType`; add the rows above plus `P:...HttpResponse.Cookies`; replace the text-contains tests with a compile test (Roslyn `CSharpCompilation` + the analyzer, or a `Fact` that builds a small probe project and expects RS0030) for at least GetAuthorizationInfo, ContentType, Headers, GetItemById.

### L-1 [Low] Pairing: `likelySessionLimit` rule (b) and the Start-time account reading (K5 v2.3) are not implemented
- Where: Pairing/PairingService.cs AwaitApprovalAsync (:972-1004) always throws `Expired` at the deadline; `MaxActiveSessions` is only read in Pairing/PairingWire.cs:226 for the post-confirm warning. No refresh of `/Users/Me` at Start on a re-pair.
- Why: K5 row and T-PAIR-13 (i1) require `likelySessionLimit` for a re-pair that reaches the deadline unapproved when the account reads `MaxActiveSessions >= 1`. Rule (a) only is done. Not a security hole (the admin gets "expired" instead of the precise hint, which on a stock Peer with limit 1 is the wrong advice), but the acceptance row is open and no test covers (i1)/(i2).
- Fix: implement (b) as written (single `GET /Users/Me` with the old token at Begin, only when the peer has a credential; trust window `None`; never send it to a host that failed the pin), add (i1)/(i2) tests.

### L-2 [Low] Re-pair connections ignore the stored `TlsSpkiPin`
- Where: Pairing/PairingService.cs `Unauthenticated()`/`Authenticated()` (:745-749) call `Configured(network, proxy, ack, serverId)`, which copies `TlsSpkiPin` from the new (null) endpoint; `ProbeEntry` carries no pin.
- Why: for a pinned peer (self-signed or CA-compromise defence) the Quick Connect mint and the new token travel on a connection that is validated by the CA chain only; a self-signed pinned peer simply cannot re-pair. The pin is the only authentication on a Public peer against a hostile CA.
- Fix: put `TlsSpkiPin` of the existing peer into `ProbeEntry` and `Configured`; if a pin exists use it.

### L-3 [Low] Probe handle for a re-pair can outlive an address change
- Where: PairingService.cs:258 (`ProbeEntry` stores the address), :347-348; PeerAddressService.ChangeBaseUrlAsync refuses only while a session is live (Peers/PeerAddressService.cs, `_gate.IsLive`).
- Why: between Probe and Start (up to 10 min) an admin can change the host; Start then pairs against the old host with the new record, stores the old host's token under the new host entry. The pin check (`PinnedServerId` set from the probe) stops the token being sent anywhere wrong later, so this is integrity not disclosure, and it needs two admin actions.
- Fix: in `BeginAsync` for an existing peer compare the stored `BaseUrl` host with `probe.Address` and refuse with `ProbeExpired`.

### L-4 [Low] CallerContext does not check that the peer is enabled; the test harness mutation hooks live in production code
- Where: Authorization/CallerContext.cs:246-256 (`IsAdmitted` finds the peer by key regardless of `Enabled`); `_skippedStep` branches at :186-198, :207-212.
- Why: for a paused/disabled peer the caller is allowed through all 7 steps and only the later `PeerDirectory.Find` (Enabled filter) stops the request, so "deny = zero Peer calls" depends on every handler (K14-K18) remembering to call `Find`. The mutation switches (`_skippedStep`) are only reachable through the internal constructor, which DI does not use (verified: public ctor registered), so no production risk, but a skipped-step code path inside the authorization class is a standing hazard.
- Fix: add `peer.Enabled` to step 6 (404, same as unknown) plus a matrix row for a paused peer; move the mutation seam behind a test-only subclass or keep it but add a test that the DI-resolved instance has `_skippedStep == 0`.

### L-5 [Low] Uninstall restores "all libraries" for managed accounts even when that exposes Shoal libraries they were excluded from
- Where: Visibility/VisibilityService.cs:120-155 (`RestoreManagedAccountsAsync`), called from `Plugin.OnUninstalling`.
- Why: a managed account was converted precisely because it is not admitted to some Shoal library. After uninstall the Shoal libraries (stub folders, posters, overviews) remain in Jellyfin, there is no Shoal step 6 any more, and the account gets `EnableAllFolders=true`, so it can browse the friend's catalog it was explicitly excluded from (D3 "nobody else can see"). It also overwrites an admin's later dashboard edits if the hash no longer matches.
- Fix: on uninstall give back "all libraries" only to accounts admitted to every Shoal library; for the rest write the explicit list (non-Shoal libraries plus admitted ones) and say so in the README, or offer to delete the Shoal libraries; skip accounts whose hash mismatches.

### L-6 [Low] Visibility: a share user with `EnableAllFolders=true` is only logged, not narrowed
- Where: VisibilityService.cs `if (account.EnableAllFolders) { LogWarning; continue; }` inside the share-user branch.
- Why: DESIGN 5.6 N-2 rule 2 says Reconcile removes Shoal libraries from a share user's list. With "all libraries" the share user can list the federated titles (not play them, R15 holds). K21 creates share users with `EnableAllFolders=false`, so this needs a dashboard edit.
- Fix: convert like other accounts for this case or let K21's drift repair own it and add a test.

### L-7 [Low] Leftover spike controller and resolver are still routable and do not apply the access mode
- Where: Api/ShoalProxyController.cs (class `[Authorize]`, route `Shoal/Proxy/{itemId:guid}/{**path}`, uses `GetAuthorizationInfo`, `GetItemById<BaseItem>(id, user)` and `Request.QueryString` under a file-level `#pragma warning disable RS0030`), Playback/ShoalItemResolver.cs, both registered (PluginServiceRegistrator.cs:58). K13's commit message says the spike PlaybackInfo filter is removed (true: ShoalPlaybackFilter.cs deleted), but this second spike path remains until K17.
- Why: it authorizes with core visibility only (no step 6, no share-user refusal, no API-key refusal beyond `Forbid`). Today it answers 404 for every real stub because it parses `shoal://<peer>/<id>` and K12 stubs hold `http://shoal.invalid/v1/<key>`. That is luck, not design: any change to the sentinel parse, or an old spike stub under the StubRoot, re-opens a proxy that bypasses CallerContext.
- Fix: delete the controller, `ShoalItemResolver`, `PeerRequestAllowlist`, `PlaylistRewriter`, `PlaybackInfoRewriter` (the last uses `UnsafeRelaxedJsonEscaping`) now, or unregister the controller; at minimum add a reflection test that no controller outside Api/Admin* and the planned ones exists.

### L-8 [Low] Text written to disk is not bounded by the quota
- Where: Sync/StubWriter.cs AddAsync (NFO written before any quota check; quota only gates images), Sync/DiskQuota.cs.
- Why: T-SYNC-10 only asks images to stop. A hostile peer with the entry cap (50,000 titles) and 10,000-character overviews of 3-byte characters writes about 1.5 GB of NFO plus about 800 MB of file-system block overhead (4 files per folder) regardless of the 1..50 GB quota setting; the first sync is exempt from the new-items guard. Bounded and accepted-looking, but the quota number the admin sets is not the number that is enforced.
- Fix: count NFO and sentinel bytes against the quota and stop adding titles (alert `QuotaReached`) when exhausted, or lower `OverviewMax` for stored NFO; document that block overhead is not counted.

### I (Info)
- Image validation is header-only (JPEG marker walk, PNG IHDR); decoder bugs in Skia/libjpeg/libpng remain, as DESIGN 5.4 states.
- `Peers/PeerTrust.cs` `VerifyAuthAsync` is public and sends the token with `TrustRequirement.None` without checking the pin: correct per the "health call exemption", but K20's HealthMonitor must call `VerifyPinAsync` first; nothing enforces it.
- `.strm` holds `http://shoal.invalid/v1/<key>`: an ISP that rewrites NXDOMAIN would answer if any core route ever fell through to ffprobe; K13's table keeps every route from doing so. No action.
- Unauthenticated callers can tell a federated id from a local one (403/404 vs core 401) on the Download/CoreHls/Audio/non-static rows. GUIDs are unguessable; no action.
- Reflection test pins `anonymous.Count <= 1`; change to `== 1` when K22 lands.

## K14 (88edeec, PlaybackInfo handler and grants), reviewed from a separate export; its suite passes (2054 passed, 6 skipped)

Verified correct: CallerContext runs before the handler (filter) and the body (<= 256 KB, 10 s, chunk-counted, depth 32) is read only after it; query wins over body, `UserId`/`LiveStreamId` never read, foreign `MediaSourceId` 400, unparseable query value 400; the forwarded body is built field by field from validated values (`BuildPeerBody` matches 6.2.1: policy ANDs, clamps, `AutoOpenLiveStream=false`, no query string); profile rebuilt as a new object named `Shoal` with closed alphabet, list caps, HLS kill switch and the eight External formats; capabilities fallback only when the device row belongs to the caller; exactly one source; the answer is built from scratch (Protocol File, IsRemote false, Path null, DirectStream false, attachments empty, streams sanitized or dropped, absolute URLs dropped, ETag from the stub key, TranscodingUrl only from a Peer URL that is exactly `/videos/<peerItemId>/master.m3u8?...` with a strict query charset and the Peer's ApiKey dropped); canary on N/D ids, token, play-session id and host on label boundaries, 502 plus admin alert; grants keyed (user, item, capped DeviceId), replace then evict-own then global, a slotted grant never replaced or evicted, 12 h absolute / 2 h / 10 min idle, 128-bit ids; 30/min burst 15 limiter; `MaxStreamsPerUser` 1..4 default 4. What PlaybackInfo returned between K13 and K14: after CallerContext allowed it, `NotYetImplementedHandlers` answered 501 from Shoal itself (never core), and unauthenticated or refused callers got core's 401 or Shoal's 403/404; no gap.

### K14-L1 [Low] The canary alert has no throttle: a hostile peer can flood every admin with pop-ups
- Where: Playback/PlaybackInfoHandler.cs (`AlertAsync` on every canary hit). A peer that puts its own item id (or token-looking value) in a source `Name` or stream text makes every play attempt of that title a 502 plus a `DisplayMessage` to every signed-in admin; the only bound is the 30/min per-user limiter.
- Fix: at most one alert per peer per 5 minutes (same rule as the offline notice), count the rest.

### K14-L2 [Low] Hostile null elements in the Peer's answer or the client's profile give 500 instead of 502/400
- Where: PlaybackInfoBuilder.Rebuild (`answer.MediaSources.FirstOrDefault(s => ... s.Id ...)`), DeviceProfileRebuilder.Conditions (`condition.Value` on a null condition). StreamSanitizer and the profile list mappers do guard nulls; these two spots do not. The filter boundary turns it into a 500 with no body and only the exception type logged, so nothing leaks; it is the same class of bug as M-1.
- Fix: `Where(x => x is not null)` in both places, plus a null-element test for the answer and the profile.

### K14-I (Info)
- Local policy is enforced on the request, not on the answer: a peer that returns `SupportsDirectPlay=true` or a `TranscodingUrl` although Home sent `EnableDirectPlay=false` / `EnableTranscoding=false` (local `ForceRemoteSourceTranscoding`, no transcode permission) is believed. Only Peer CPU or Home bandwidth is at stake; consider `directPlay &= !ForceRemoteSourceTranscoding` and `transcoding &= CanTranscodeVideo` when building.
- `ParseMasterQuery` checks only the character set and `%` escapes; the value-level rules of 6.11 (grant-bound ids, DeviceId etc.) must be applied by K17 when it forwards `PeerMasterQuery`. Flagged so K17 does not assume K14 validated values.
- `ShoalRawJsonResult` (in ResponseWriter.cs) correctly goes through `ResponseWriter.Apply`.

## Verified correct, per priority (no finding)

K5 (Home pairing):
- AwaitingConfirm gate: the token lives in `Run._token` only; `SecretStore.PutAsync`, `Enabled=true`, `Pairing` info are written only in `FinalizeAsync` after `ConfirmAsync(match:true)`; Confirm is legal only in AwaitingConfirm (`TryDecide` checks step and `_finished`); `{match:false}`, the 15 min window, Cancel and every abort call `LogoutTokenAsync` then null the token; shutdown leaves the pending record so the next start reports `interrupted` with its phase.
- Preflight identity check against the probed server id before Initiate; Initiate sent only from `Run.RunCoreAsync`, started only by `BeginAsync` (the admin's call); no other caller of `/QuickConnect/Initiate`; second Begin 409 (`SessionActive`); 5 per 10 min per host:port on the server; probe handles single-use, 10 min, 16 max; peer address immutable while live (`PeerAddressService` + `IPairingGate`).
- Poll classification matches 4.2a: baseline poll before the code is shown, 404/non-JSON before baseline = `quickConnectUnreachable` never `requestReplaced`, 404 after baseline = exactly one redeem, 401 = off, timeouts/5xx/429 counted (3 = silent, 30 s = network), sequential polls measured from the start of the previous one, deadline 570 s.
- Validation: Code `^\d{6}$`, Secret `^[0-9A-Fa-f]{64}$` compared ordinally, token `^[0-9a-f]{32}$`, user id equals `/Users/Me` id, server id equals pin, `IsAdministrator` missing is treated as a failure (fail closed), admin account = logout then `adminAccount`.
- Secrecy: log lines carry `Redact.Id(peerKey)` and enum names only; `PairingSessionInfo` carries code and check but no secret/token/GUID; `PeerCallException` messages are fixed; header values percent-encoded by `PeerAuthorization.Encode` (every non-unreserved byte), server name sanitized; DeviceId derived server-side from the Home id and peer key; abort records are durable in `health.json`; the five safety kinds (and only those) notify admins.
- `AdminPairingController`: class-level `[Authorize(Policy = RequiresElevation)]`, no anonymous action, `ShoalResult` for every answer.

K3/K4/K7:
- `BoundedReadStream` never reads more than cap+1 bytes, counts chunked bodies, checks declared length both ways, idle timeout per read, total deadline from `CallBudget`; `PeerResponse` exposes no peer header other than a fixed whitelist, strict UTF-8, JSON media type required, depth 32; redirects are errors with no second request; cookies, proxy, decompression off; `HttpVersion` 1.1; breaker opens after 3, half-opens at 30/60/120/240 then 300 s; exceptions carry no URL, host, token (tests assert it).
- `AddressPolicy`: text-level validation before `Uri`, canonical dotted-decimal only, userinfo/query/fragment/backslash/non-ASCII refused, IPv4-mapped, NAT64, 6to4, Teredo, compatible forms unwrapped, metadata addresses (169.254/16, 100.100.100.200, 168.63.129.16, 192.0.0.192, fd00:ec2::254) blocked, `CheckResolved` refuses the whole set if any address is unsuitable and runs in the connect callback (rebinding), Public needs https, LAN http needs the acknowledgement, proxy only loopback socks5 and only for Tailnet names/literals. I found no bypass.
- Trust window (120 s pin, auth for static) enforced inside `PeerClient.SendAsync`, so a caller cannot forget it; `TlsKeyChanged` and `IdentityMismatch` block everything; SPKI pin compare is constant time; connection signature includes the pin.
- `ResponseWriter`: closed type table, upstream accept lists, base headers on all, CSP and `attachment` where specified, existing Content-Disposition/CSP replaced; subtitle body check rejects NUL, bad UTF-8, wrong header lines.

K6/K13:
- CallerContext steps 1-8 in order, identity only from `HttpContext.User`, API-key claim 403, core default policy with `ResourceNotFound` = 401, step 6 reads state per request, share users refused first, unknown or non-federated item 404, recovery mode 403. Mutation tests (steps 4 and 6) turn the right rows red; the harness hosts the real filter on real routing with a fake core controller and a fake peer that counts requests. `[AllowAnonymous]` reflection test passes (currently zero anonymous actions).
- Route table checked against the Jellyfin 12.1 source in the scratch copy (`Jellyfin.Api/Controllers`): every `Videos/{id}/...` HLS, trickplay, attachment, subtitle playlist and legacy `hls` route, every `Audio/{id}/...` stream, universal, master, main, hls1, legacy segment route, `Items/{id}/Download` and `File`, `LiveStreams/Open`, `Videos/ActiveEncodings`, both PlaybackInfo verbs and both subtitle `Stream.{fmt}` templates are covered. Not covered, by design: `POST Videos/{id}/Subtitles`, `DELETE Videos/{id}/Subtitles/{i}`, `Items/{id}/RemoteSearch/Subtitles/*` (all need subtitle-management permission, act on the local stub folder only).
- Filter: query read only through `Request.Query`, `Guid.TryParse` in all forms, every `itemId` value judged, subtitle route id mismatch = 404 before CallerContext, 414 over 8 KB (federated only), handler exceptions become 500 with the exception type and the template in the log and never reach `ExceptionMiddleware`, no log line carries a path or query (test T-CLI-14 scans the captured log), `Static` read as MVC's binder reads it.
- K10: `Admitted` identical in CallerContext, Reconcile and AccessService; share users never selectable, never given a library, cleaned if found with an explicit list; `Everyone` is evaluated at request time so later accounts are covered; library created with access already set to the pairing admin only; `BlockedMediaFolders` is never touched (the DTO round trip leaves it as read; analyzer bans the property); the policy write goes through `UpdatePolicyAsync`; managed record is written before the policy; an admin edit (hash mismatch) ends management.
- K8/K9/K11/K12: titles/paths/ids sanitized as 5.5 (NFKC first, then `<>` removal, control/bidi/noncharacter drops, rune-boundary cut), canonical provider ids only in folder names, NFO and dedup keys; `XmlWriter` with `CheckCharacters`; folder names confined, link-checked, collision-keyed after NFKC and case folding; stub removal deletes only `.strm/.nfo/poster.*/backdrop.*`; images JPEG/PNG by magic, JPEG marker walk to first SOF, PNG first-chunk IHDR, dimension caps, serial, quota stops images; the `.strm` is `http://shoal.invalid/v1/<random key>`, the NFO holds only sanitized text plus the admin-typed peer display name, nothing with a peer id, token or host; empty StubRoot = nothing federated (`ShoalIndex.Classify`, sync, resolver); mass-deletion and new-items guards, partial-run rule, one global sync (`SemaphoreSlim.Wait(0)`), peer-reported `TotalRecordCount` cannot cause removals without the confirm-present call.

## Test quality
- Strong: harnesses use real routing, real `PeerClient` against a fake peer that counts requests; matrix rows assert status, empty body and zero peer requests; step-4/6 mutations; hostile-string fixtures F-MD-02..06; PlaybackInfo body-exactness; log-grep tests.
- Weak or missing:
  1. Analyzer tests only check that the text file contains an ID string (M-2): a ban that matches nothing passes. Add a compile-based test.
  2. No test feeds `null` array elements or null dictionary values through catalog sync or PlaybackInfo (M-1, K14-L2). T-SYNC-6 fuzzes strings only.
  3. No test for T-PAIR-13 (i1)/(i2) because rule (b) is missing (L-1).
  4. The reflection test accepts zero anonymous actions (`<= 1`); tighten at K22.
  5. No test that a paused/disabled peer is refused by CallerContext itself (L-4), and no test that the DI-built `CallerContext` has no skipped step.
  6. No test that the spike controller is gone or refuses a non-sentinel stub (L-7).
  7. T-SEC-8 rebinding is tested with a fake resolver; the real `Dns` path and IPv6 literals through the connect callback are not.

## Verdict per task

| Task | Verdict | Condition |
|---|---|---|
| K3 PeerClient core | GO | none |
| K4 AddressPolicy, trust window, TLS | GO | none |
| K5 Quick Connect pairing (Home) | GO | L-1 (rule b, T-PAIR-13 i) before the Pilot lab run; L-2 and L-3 small follow-ups |
| K6 CallerContext, bans, harness | GO | L-4 and the analyzer part of M-2 |
| K7 ResponseWriter | FIX-FIRST (small) | M-2: the ContentType ban does not fire and the ban list has the gaps listed; fix before K15/K17/K18 start writing responses |
| K8 sanitizers, NFO | GO | none |
| K9 ImageFetcher, quota | GO | L-8 documented |
| K10 library, VisibilityService | GO | L-5 and L-6 follow-ups (L-5 before release) |
| K11 catalog sync | FIX-FIRST | M-1: null-element crash and the abort of `SyncAllAsync`; small fix |
| K12 index, enricher, sentinel | GO | none |
| K13 intercept filter and route table | GO | L-7: delete the spike controller and resolver now rather than at K17 |
| K14 PlaybackInfo and grants | GO | K14-L1, K14-L2 (small) |

## Counts

Critical 0, High 0, Medium 2 (M-1, M-2), Low 10 (L-1 to L-8, K14-L1, K14-L2), Info 9.

## Top findings
1. M-1 null array elements crash `MetadataSanitizer` and one hostile peer stops sync of the peers after it (reproduced).
2. M-2 the `HttpResponse.ContentType` ban is a no-op (reproduced), `UnsafeRelaxedJsonEscaping`, `WriteAsJsonAsync`, `FileStreamResult`, `Redirect`, cookies and typed headers are not banned, and the tests only grep the ban file.
3. L-7 the spike proxy controller authorizes without the access mode and is dead only because the sentinel format changed.
4. L-1 `likelySessionLimit` rule (b) and the Start-time account reading are missing (K5 acceptance row open).
5. L-5 uninstall hands "all libraries" back to accounts that were excluded from Shoal libraries.

## What I did not do
No build or test was run in the repo; all runs were in exported copies of 172f590 and 88edeec under the scratchpad. I did not exercise a real Jellyfin 12.1 (core policy handler behaviour, `TypeOptions` empty-array meaning, `ReportFileSystemChanged` with realtime monitoring off): those stay Pilot lab items. K15 and K14's uncommitted follow-ups in the working tree were not reviewed.
