# Warden review 03: K2 SecretStore, Redact (commit 88d4d7f)

Reviewer: Warden (read-only). Date: 2026-10-15.
Scope: `git show 88d4d7f` in full (SecretStore.cs, SecretKeyParser.cs, KeyProtector.cs, Redact.cs, VolumeCheck.cs, SecretTypes.cs, ISecretStore.cs, the registrator change, SecretStoreTests.cs, RedactTests.cs, CapturingLogger.cs), plus AtomicFile.cs, SecureDirectory.cs, ShoalPaths.cs, StartupReport.cs, PeerDirectory.cs and the K1 commits 26e8575 / 2049647 for how secrets reach disk. Spec: DESIGN v2.1 section 8.2, 3.1, 3.7, the K2 task row, "K2 prereq (N-7.1)", N-7 and N-8 rows, and warden-02 N-7.
The Security/ and Storage/ files in the working tree are byte-identical to the commit (checked with `git diff HEAD`). PeerDirectory.cs and the registrator carry uncommitted changes by another agent; I read the working-tree PeerDirectory only for the consumer contract.
Experiments: the Redact regexes were replayed in PowerShell/.NET with the exact patterns and options (same engine semantics as `[GeneratedRegex]`). A DPAPI blob was produced with `ProtectedData` to confirm the flags offset used in finding L-5. I did not run the test suite (the working tree has other agents' uncommitted edits); findings about tests are from reading them.

## Verdict: GO for K5 on top of this, conditional on M-1 (small) and on K5 not calling `Redact.Scrub` on unbounded or peer-controlled text until M-2/M-3 are fixed

Count by severity: **Critical 0, High 0, Medium 3, Low 13, Info 5.**

The cryptographic core is sound. I found no way for a token to reach logs, the plugin XML, admin JSON (none exists yet) or a URL from this commit. The three Mediums are an N-7.1 hole in key selection (an empty variable silently falls back to a generated `master.key`) and two defects in `Redact.Scrub`, which nothing calls yet but which K5 is about to depend on.

## What I verified and found correct (no finding)

- **AES-GCM construction** (SecretStore.cs:638-678). Key 32 bytes (HKDF-SHA256, empty salt, info `shoal-secrets-v1`, :305), nonce 12 bytes from `RandomNumberGenerator` per `SealWith` call, tag 16 bytes, `new AesGcm(key, 16)`. Every save re-seals every openable entry and the check message with a new nonce (`SnapshotForWrite` :729, `WriteAll` :752); the test asserts all nonces differ. Birthday bound: 96-bit random nonces are safe to about 2^32 messages per key; at most 1001 messages per save, so it takes millions of saves with a maximal store. Not reachable (I-1).
- **Tag before use.** `AesGcm.Decrypt` authenticates before releasing plaintext; the code never touches the output on failure (`TryOpen` :646-657). No decrypt-then-check anywhere. Plaintext buffers are zeroed after use (`SealWith` :672, `ResolveLocked` :600).
- **AAD** (:283) is exactly the spec string: `shoal-secrets|schema=1|peer=<peerKey>|host=<hex SHA-256 of lower-case IdnHost>`. Peer keys are validated 16-64 lower-case hex (ShoalPaths.cs:317), so the field separators are unambiguous. `Uri.IdnHost` gives punycode, normalized IPv4 forms and the same host the HTTP client connects to. The tests move an entry to another peer on the same host and re-point a peer to another host; both refuse. A broken (AAD-less) implementation would fail those two tests, so they do prove the binding.
- **Tamper behavior (T-REG-4).** One flipped byte in an entry refuses that peer, keeps the others, leaves `master.key` byte-identical, and a later save carries the broken entry over byte for byte (:737-740). A flipped byte in the check message, a different key, a missing `master.key` next to `secrets.enc`, or a corrupt `master.key` makes the whole store unreadable, and `PutAsync`/`RemoveAsync` throw before any write (`RequireReady` :680). `EnsureKey` (:690-725) can only run when `_aesKey is null`, which in the unreadable cases never happens because `RequireReady` throws first. The key is never regenerated.
- **Env key handling.** Both variables set: refused (:400-404). Exactly 32 bytes as 64 hex (either case) or 43 base64url characters (one trailing `=`), canonical-form check on the spare bits, all-identical bytes refused, no standard-base64 characters, no whitespace (SecretKeyParser.cs:27-114). A bad value or unreadable file gives `BadKey` and returns before `master.key` is looked at (:406-413). With a valid env key `master.key` is neither read nor written, and `EnsureKey` returns at once (:692). Logs carry the reason enum only; the test asserts the rejected value is absent from every log line.
- **DPAPI.** `CryptProtectData`/`CryptUnprotectData` with flags `CRYPTPROTECT_UI_FORBIDDEN` only, so scope is `CurrentUser` (LOCAL_MACHINE 0x4 is never passed); entropy is a public constant and named as such in code and docs; `DefaultDllImportSearchPaths(System32)`; DPAPI output buffers are cleared before `LocalFree` (KeyProtector.cs:146-147). A raw 32-byte `master.key` on Windows is refused (`KeyProtectionFailed`), not accepted.
- **Atomic writes and permissions.** Temp file in the same folder created `CreateNew` with `UnixCreateMode` 0600 (so there is no chmod-after-create window and umask can only narrow it), flushed, renamed over the target, directory fsync on Linux (AtomicFile.cs:90-133). Folder created 0700 (SecureDirectory.cs). Raw `master.key` and `secrets.enc` are 0600; the Linux test asserts it and CI runs on ubuntu-latest.
- **Key lifetime.** Raw key bytes, the parsed env key, the DPAPI output and the HKDF input are all zeroed after use (`Zeroing` struct, :779). The derived AES key is zeroed on reload and `Dispose`.
- **No token leakage path in this commit.** Grep over src/ for token, ApiKey, Authorization, Secret, ISecretStore, every `_logger.*` call and every `Redact.*` call: `PeerCredential.ToString()` is fixed text, `[DebuggerDisplay]` is fixed, `Token` is `[DebuggerBrowsable(Never)]`; `PeerEndpoint.Token` is internal and `ToString()` prints only the display name; the only place the token is written out is `PeerAuthorization.Build` (header, percent-encoded). `SecretStoreInfo` has no secret field. `PluginConfiguration` has toggles only. `state.json` has no token field. No admin controller exists yet.
- **Consumer is fail-closed.** `PeerDirectory.Find` returns null when the secret store is not `Ready` or `TryGet` returns null (PeerDirectory.cs:99-112), which is what a refused peer produces.

## Findings

### M-1 [Medium] An empty `JELLYFIN_SHOAL_KEY` / `_KEY_FILE` is treated as "not set" and falls back to a generated `master.key`

- **Where:** SecretStore.cs:398-399 and :451 (`Blank`), used by `ResolveKey`.
- **Why:** DESIGN 8.2 says "When either variable is set, Shoal never falls back to `master.key`, never generates a key". `docker compose` with `JELLYFIN_SHOAL_KEY=${SHOAL_KEY}` and an unset or typo'd shell variable, or a systemd `Environment=JELLYFIN_SHOAL_KEY=` line, produces an empty value. The admin believes the key comes from their secret manager. Shoal silently creates `master.key` next to `secrets.enc` (the exact weaker mode they opted out of), stores real tokens under it, and the first start with the variable corrected reports `Tampered` for the whole store. The security cost is a silent downgrade from "key outside the volume" to "key beside the secrets", which is the property that protects full-volume backups. This is also the misconfiguration N-7.1 was written to catch.
- **Exploit scenario:** none needed; it is a misconfiguration the design says must be refused. The `BadKeys_*` test theory has no empty-string row, so nothing pins it.
- **Fix:** distinguish absent from empty. `Environment.GetEnvironmentVariable` returns `null` only when absent (on Windows an empty value deletes the variable; on Unix it can be `""`). Treat `""` (and whitespace-only) for either variable as set-and-invalid: `BadKey`, no fallback. Add `[InlineData("")]`, `[InlineData("   ")]` and an empty `_KEY_FILE` to the bad-key theory, with a decoy `master.key` that must stay untouched and unread.

### M-2 [Medium] `Redact.Scrub` is quadratic on peer-controlled text (CPU denial of service); no length cap, no regex timeout

- **Where:** Redact.cs:313 (`QueryString`), and the absence of any input cap or `matchTimeoutMilliseconds` on all five generated regexes (:313-328). `Scrub` is documented for "a peer's error text, an exception message from a library" (Redact.cs:219-221), i.e. attacker-influenced input.
- **Why:** `\b[a-zA-Z][a-zA-Z0-9+.\-]*://` restarts a scan to the end of the run at every word boundary. Input like `a.a.a.a.` has a boundary every two characters and no `://`, so every start scans to the end. Measured with the exact pattern: 10,000 chars 0.39 s, 20,000 chars 1.6 s, 40,000 chars 6.3 s, 80,000 chars 24.8 s (interpreted engine; compiled is faster by a constant factor but the growth is still n squared). A hostile peer (or a hostile body echoed into an exception message) can pin a request thread for minutes per log line once K5/K11 call `Scrub` on peer error text. `Scrub` has no callers today, so this is latent; K5 is the first caller.
- **Fix:** (a) cap before scanning: `if (text.Length > 4096) text = text[..4096] + "...";`. (b) Make the scheme match linear by forbidding mid-run starts: `(?<![A-Za-z0-9+.\-])[a-zA-Z][a-zA-Z0-9+.\-]*://...`, or find `"://"` with `IndexOf` first. (c) Put `matchTimeoutMilliseconds: 50` on every `[GeneratedRegex]` and, on `RegexMatchTimeoutException`, return a fixed `"[text omitted]"` (never the unscrubbed text). (d) Add a test: 200 KB of `a.` must return in under a second.

### M-3 [Medium] `Redact.Scrub` misses real secret shapes (it will be the last line of defense for K5's peer error text)

- **Where:** Redact.cs:297-329. Each row below was replayed with the exact patterns; "leaks" means the secret survives `Scrub`.
- **Why / evidence:**

| Input shape | Result | Cause |
|---|---|---|
| `GET /x?ApiKey%3D<32 hex>` and `Token%3D%22<32 hex>%22` (URL-encoded separator, typical when a URL is a query value of another URL) | leaks | separator must be literal `=`/`:`; `LongHex` needs `\b`, and `D`+hex has no word boundary |
| `{\"Token\":\"abc\"}` (JSON escaped inside a log string or exception message) | leaks | `"\s*:\s*"` does not allow the backslashes |
| `Authorization: Bearer <non-hex>` and `Authorization: Basic ...` | leaks | no Bearer/Basic rule (hex tokens are caught only by the accidental `LongHex`) |
| `authToken=`, `refresh_token=`, `id_token:`, `device_<32 hex>`, `t<32 hex>` | leaks | key names are anchored with `\b`, and `_`/letters are word characters, so `refresh_token` and `authToken` have no boundary before `token`; same for `LongHex` |
| Uppercase 32-hex token | leaks | the 32-hex branch of `LongHex` is lower-case only |
| Quick Connect `"Code":"123456"` | leaks | DESIGN 4.2a says the code must not be logged; no rule |
| Bare invite `K7QM-2XA4-...` or 26 base32 without the `shoal1-<8hex>-` prefix (the POST body field is just `"invite":"<26 base32>"`, which the `invite` key rule does catch, but a bare copy in an error text does not match) | leaks | `Invite()` requires the prefix |
| `homeNonce` / SAS code (4.1b "Never the invite, nonce, commitment or token") | leaks | no rule |
| `password: hunter two` | `[redacted] two` | value class stops at whitespace |
| `https://user:pw@host/x` | leaks userinfo | the query rule only touches what follows `?` |
| Relative URL with unknown query key `?sig=...` | leaks | whole-query redaction applies only to absolute URLs with `://` |

  `Redact.Error` (type name only) and `Redact.Host` are fine; the weakness is `Scrub`.
- **Fix:** replace the `\b` anchors by "not preceded by a letter or digit" lookbehinds and allow any prefix on key names (`[\w-]*(?:token|secret|api[_-]?key|passw(?:or)?d|invite|nonce|code)`), allow `(?:%3D|%3A|=|:)` and `(?:%22|\\?")` in the separators, add `\b(?:Bearer|Basic)\s+\S+`, make the 32-hex rule case-insensitive and boundary-free (`(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])`), match 6-digit values after a `code` key, match the invite body (`(?:[0-9A-HJKMNP-TV-Z]{4}-){6}[0-9A-HJKMNP-TV-Z]{2}`, case-insensitive) with or without the prefix, redact userinfo (`//[^/@\s]+@`), and treat everything after `?` as secret for any string containing `?` followed by `key=`. Also run the scrub on a percent-decoded copy and mask by position, or decode first and then scrub. The stronger fix is the rule already in the file's comment: never pass the text in. K5 should log fixed sentences plus `Redact.Error` and a status code, and call `Scrub` only on a capped string. Add negative tests (each row above) and a property test that `Scrub(Scrub(x)) == Scrub(x)` over a generated corpus.

### L-1 [Low] The "how to make a good key" message is never shown; every bad-key cause reads the same

- **Where:** SecretKeyParser.cs:19 (`HowToMakeAKey`, no reference anywhere in src/ or tests); StartupReport.cs:62-66 logs only the enum; SecretStore.cs:411 collapses an unreadable file, wrong length, non-hex, placeholder, BOM and UTF-16 into `BadKey`.
- **Why:** DESIGN 8.2 requires "`SecretsUnreadable(badKey)` and a message that shows how to make a good key (`openssl rand -hex 32`)". It is dead code, so the admin of a Docker deployment who wrote the key with PowerShell `>` (UTF-16 with BOM) or with a trailing space sees only `BadKey`.
- **Fix:** log `HowToMakeAKey` at Error level when the reason is `BadKey` (StartupReport), add it to `SecretStoreInfo` for the U6 banner, and split `BadKey` into a short detail (`KeyFileUnreadable`, `KeyWrongShape`, `KeyPlaceholder`) that never contains the value.

### L-2 [Low] The file mode of `master.key` and of the `_KEY_FILE` file is never checked after the first write

- **Where:** SecretStore.cs:377 (only `secrets.enc` is checked on load), :724 (`master.key` only right after creation); VolumeCheck.cs:380; `ReadKeyFile` :453 checks nothing.
- **Why:** A `master.key` restored by `cp`, `rsync` without `-p`, a Docker volume copy or `chown`/`chmod -R` mishap becomes 0644 and Shoal says nothing. On the default Linux path that file is the entire key. For `_KEY_FILE` the world-readable-secret case is common (`/run/secrets/*` defaults vary, hand-made files are 0644). The existing warning text ("This volume cannot protect secret files") would also be the wrong message.
- **Fix:** at load, if `master.key` (raw) or the `_KEY_FILE` file has any group/other bit, set a separate warning "key file is readable by other users; run chmod 600". Do not refuse (read-only mounts and secrets managers are legitimate). Add a Linux test.

### L-3 [Low] `ResetAsync` cannot clear a tampered or wrong-key store when the key comes from the environment; the guard is wider than its comment and the move is not atomic

- **Where:** SecretStore.cs:220 (`_keySource is Environment or EnvironmentFile || reason is BadKey or AmbiguousKey`), :226-232.
- **Why:** The interface comment says reset is refused when the environment key "is not usable". The first clause also fires for a valid env key. In the two recommended deployments (env or `_FILE` key), a tampered `secrets.enc` or a store written under a previous key throws "fix JELLYFIN_SHOAL_KEY first" although the key is fine, so the banner's "re-pair" path (8.2) needs a manual `mv` on the server. Separately, if `secrets.enc.corrupt-<stamp>` already exists (two resets in one second), `File.Move` throws after possibly moving the first file, leaving a half reset. A reset of a healthy (`Ready`) store is also allowed and destroys every pairing (it only moves them aside); the caller must require the typed confirmation.
- **Fix:** allow reset for env keys when the reason is `Tampered`/`Corrupt` (move only `secrets.enc`; never touch `master.key` in env mode), keep refusing for `BadKey`/`AmbiguousKey`; use a unique suffix (`stamp` plus random) and move atomically-or-not-at-all; document for K5/U6 that the endpoint needs the typed-name body (9.6).

### L-4 [Low] The constructor "never throws" claim is false for platform failures; an escaped exception stops server startup

- **Where:** SecretStore.cs:36 (doc), :370 (`catch ... IOException or UnauthorizedAccessException`), :648 (`new AesGcm`), :654 (`catch CryptographicException`).
- **Why:** `AesGcm` throws `PlatformNotSupportedException` where the platform crypto lacks GCM (musl images without OpenSSL, some stripped containers), and `Path.GetFullPath` in `VolumeCheck.CheckFileSystem` can throw `ArgumentException`/`NotSupportedException`. Those escape `Load()`, the DI singleton fails to construct, and because `StartupReport` (a hosted service) resolves it in `StartAsync`, host start fails. It is fail-closed but takes the whole Jellyfin down, and the log does not say why.
- **Fix:** wrap the body of `Load` in a final `catch (Exception)` that sets `Unreadable(StorageUnavailable)` and logs `ex.GetType().Name`; check `AesGcm.IsSupported` once and report a dedicated reason.

### L-5 [Low] Test gaps: some acceptance criteria are asserted weakly or not at all

- **DPAPI scope `CurrentUser` is not proven.** SecretStoreTests.cs:569-595 only shows another entropy cannot unwrap. A regression that adds `CRYPTPROTECT_LOCAL_MACHINE` (0x4) passes every test. The DPAPI blob stores its flags as a little-endian dword at offset 40 (checked: `CurrentUser` gives 0, `LocalMachine` gives 4). Add `Assert.Equal(0u, BitConverter.ToUInt32(blob, 40) & 4)`. This test runs only on Windows (CI is ubuntu-only), so also run it on the dev machine before release.
- **Flip tests touch only one place.** `Flip` (SecretStoreTests.cs:761-770) changes the character 4 positions into the ciphertext. Add: a flip in the last characters (the tag), in the nonce, a truncated ciphertext, a check message copied from another store, and an entry whose JSON key name was flipped (becomes an orphan; assert it is not "refused" but is listed by the reconciler).
- **Windows run of the filesystem theory is vacuous.** Line 652 is `warn == (CheckFileSystem(...) is not null || IsWindows() && warn)`; on Windows `CheckFileSystem` returns early, so every `warn:true` row passes by construction. It is real on the ubuntu CI job. Mark it `[LinuxTheory]` so a Windows run does not look like coverage.
- **Missing cases:** empty-string env var (M-1); a valid env key must not appear in any log line (only rejected values are checked); parallel `PutAsync` for different peers (the gate); the real `SecretStore` plugged into `PeerDirectory` with a tampered entry (PeerDirectoryTests uses `MemorySecretStore`); `ResetAsync` with a valid env key and a tampered store (L-3).
- **Redact tests are positive-only** (RedactTests.cs:60-80): no case for any row in M-3, none for input length.
- Tests that do prove their claim: fresh nonces, host binding, peer-key binding, `BadKeys_*` (a fallback implementation would be `Ready` and fail), env key never creates or reads `master.key`, no token in any file under the data folder (text, base64 and hex forms), crash mid-write keeps the old file.

### L-6 [Low] `PeerCredential` / `PutAsync` accept any non-blank token and device id; `Token` is an ordinary public, serializable property

- **Where:** SecretTypes.cs:125-133, :137; SecretStore.cs:159-176.
- **Why:** DESIGN 4.2a: "Access token `^[0-9a-f]{32}$` ... before it enters an `Authorization` header or the store", and device id `shoal-<12 hex>-<8 hex>`. K5 is the only writer, but the store is the single choke point; a validation slip in K5 would persist a hostile or malformed token (CR/LF, quotes) that later reaches headers via the legacy `PeerHttp.Sanitize` path or logs. Separately, if any admin response ever serializes a `PeerCredential` by mistake (T5), `Token` is emitted.
- **Fix:** validate both fields in the `PeerCredential` constructor (throw `ArgumentException` with no value in the message), and put `[JsonIgnore]` on `Token` (and optionally `[JsonIgnore]` on the whole type via a converter that writes `"[secret hidden]"`).

### L-7 [Low] Stale temp files of `secrets.enc` and `master.key` are never cleaned

- **Where:** AtomicFile.cs:93, `DeleteStaleTempFiles` is called only for `state.json` (StateStore.cs:306).
- **Why:** A crash between temp write and rename leaves `secrets.enc.tmp-<hex>` (ciphertext) or `master.key.tmp-<hex>` containing the raw key on Linux, with 0600. Not a new exposure beyond `master.key` itself, but a never-installed key lying around is clutter that confuses incident response, and in env-key mode a leftover `master.key.tmp-*` should not exist at all.
- **Fix:** call `DeleteStaleTempFiles(root, "secrets.enc")` and `("master.key")` once at load, before reading (never when a write is in flight; the gate covers it).

### L-8 [Low] Volume check gaps

- **Where:** VolumeCheck.cs:355-358, :433-437.
- **Why:** DESIGN 3.1 names "NTFS via FUSE". NTFS-3G mounts report type `fuseblk`, which neither equals `fuse.` nor starts with it, so it is not flagged. Also not flagged: `virtiofs`, `vboxsf`, `ceph`, `lustre`, `ncpfs`, `afs`, `drvfs` (WSL), and on Windows mapped network drives (`Z:\`); only `\\` UNC paths warn. The post-write `CheckFileMode` partly backstops permission-less volumes, but not network volumes that fake 0600.
- **Fix:** add `fuseblk`, `virtiofs`, `vboxsf`, `ceph`, `drvfs`, `afs`, `lustre` and use `new DriveInfo(root).DriveType == DriveType.Network` on Windows. Keep it warning-only, as designed.

### L-9 [Low] The env-variable key is inherited by child processes (ffmpeg) and the key text is not scrubbable

- **Where:** SecretStore.cs:398, :408; ReadKeyFile :463-466.
- **Why:** The design lists `/proc/<pid>/environ` and `docker inspect` as exposures of the env form, and recommends `_FILE`. A third path: Jellyfin starts ffmpeg with the inherited environment, and ffmpeg parses media supplied by friends' servers (T36, 8.1). A decoder exploit reads its own `environ`. The key string and the key-file byte buffer are also never zeroed (managed strings are immutable; the `bytes` array in `ReadKeyFile` could be).
- **Fix:** keep the README preference for `_KEY_FILE`; additionally, after reading the key, call `Environment.SetEnvironmentVariable(KeyVariable, null)` and cache the parsed key for `ResetAsync` reloads (children then inherit a scrubbed environment); zero the `bytes` array in `ReadKeyFile`. Tell the user this does not change `/proc/<pid>/environ` of the main process.

### L-10 [Low] The stale spike page still asks for a "Peer access token" and posts it to the plugin-configuration API

- **Where:** src/Jellyfin.Plugin.Shoal/Configuration/configPage.html:23-25, :63-70.
- **Why:** After K1 `PluginConfiguration` has no `Peers` or `StubRoot`. The page still reads/writes `config.Peers[0].Token` and calls `ApiClient.updatePluginConfiguration`. Derived, not executed: Jellyfin binds that body with System.Text.Json into the config type, which ignores unknown members, so the token is not persisted to the XML. But the admin is told "The token is never shown again" as if it were stored, and the hard rule "no tokens in the XML" depends on a serializer default. Any future re-added `Peers` property would turn this into a plaintext-token writer.
- **Fix:** replace the page with the toggles-only stub now (U1 will replace it fully); delete the token input and handler.

### L-11 [Low] Two state reads in one lookup: `TryGet(peerKey)` and `peer.BaseUrl` can be from different state generations

- **Where:** PeerDirectory.cs:104-114 (working tree), SecretStore.cs:632-636 (`HostHashOf` reads `_state.Current` itself).
- **Why:** `Find` takes one snapshot for the base URL and `TryGet` takes another for the host binding. Within the few microseconds of a concurrent `PUT Peers` that changes the host, `Find` could return the old credential with the new `BaseUri`. The window is tiny and the host-change path deletes the entry first (4.4), so this is a defense-in-depth gap, but the AAD binding is the last line for exactly this case.
- **Fix:** add `TryGet(PeerState snapshotPeer)` (or `TryGet(peerKey, baseUrl)`) so the credential is opened under the same URL that will be used; `PeerDirectory.Find` passes its own snapshot.

### L-12 [Low] `Redact` is used only by `SecretStore`; the playback code logs raw peer slugs and ids

- **Where:** ShoalProxyController.cs:110, 144, 168, 219, 232; ShoalPlaybackFilter.cs:109, 148, 166, 184 (`{PeerId}`, `{ItemId}`).
- **Why:** DESIGN 8.3 T6 and 2 #35: "Info-level logs carry no peer ids", "ids hashed to 8 hex". `target.PeerId` is the peer slug and `ItemId` is a local id, not a token, so there is no secret here, but the convention K2 introduced is not applied, and K3+ will copy these lines. `ShoalProxyController.cs:201` also keeps a private `Redact(text, secret)` that only replaces the one exact token string (the snippet is documented query-free, so it is harmless today).
- **Fix:** route these through `Redact.Id`/`Redact.Peer`, delete the private helper, and add the "grep captured logs for the token and for peer ids" test T4 calls for in the same change.

### L-13 [Low] Reconciliation counts refused entries as present

- **Where:** SecretStore.cs:133-142 (`PeerKeys` includes refused entries), consumed by ReconciliationService.cs:65.
- **Why:** After a flipped byte or an unrepaired host edit the reconciler sees "paired peer has an entry" and reports nothing; only `Info.RefusedPeerKeys` shows the problem. If U6/K5 render the reconciler alone, a refused peer looks healthy and merely "Offline".
- **Fix:** have the reconciler take `Info.RefusedPeerKeys` and emit a `SecretRefused` finding; K5/U6 must show it with "Re-pair". No change to the store.

## Info (no action needed for K5; recorded so the next reviewer does not re-derive them)

- **I-1.** GCM nonce volume: every save re-seals every entry (up to 1001 messages). 2^32 messages under one key is the NIST random-nonce limit; a maximal store would need millions of saves. Key rotation stays out of scope (spec).
- **I-2.** Host-only binding is what the spec says (8.2: a scheme or port change does not change the AAD) and the test documents it. The consequence for K5: `StateValidator` checks length and control characters of `baseUrl` only (StateValidator.cs:117), not scheme or the Public-requires-HTTPS rule, and `PUT Peers/{h}` must re-run the network-class checks on a scheme or port change, because the token will still open.
- **I-3.** There is no file-level binding: an attacker who can write the data folder but not read the key can delete an entry or roll the whole file back to an older valid one (old, probably revoked tokens). Both are availability or staleness effects, not disclosure.
- **I-4.** Default Linux/Docker mode: key and ciphertext sit side by side, 0600, in one volume. Honest summary in 8.2 already says so. A one-line Info log at startup when `KeySource == KeyFile` ("key is stored next to secrets.enc; use JELLYFIN_SHOAL_KEY_FILE to keep it out of volume backups") would make the trade-off visible without waiting for the README.
- **I-5.** Non-Windows behavior: the protector is `null`, so `master.key` is 32 raw bytes at 0600 (macOS, FreeBSD included); nothing pretends to wrap it. WSL/Docker on Windows hosts that mount drives through 9p/drvfs get a warning from `CheckFileMode` after the first write if the mode does not stick. Fail-closed and explained in the docs; the only gap is the missing startup hint (I-4).

## Spec conformance table

| Spec item | Result |
|---|---|
| Key = exactly 32 random bytes as hex or base64url, strict, placeholder refused | Met (SecretKeyParser.cs) |
| `_FILE` variant; both set refused | Met |
| Bad value: `SecretsUnreadable(badKey)`, message shows how to make a key | Reason met; message not surfaced (L-1) |
| No fallback to `master.key`, no generated key, `master.key` never read or written in env mode | Met, except an empty variable counts as unset (M-1) |
| AES-256-GCM, one message per peer, fresh 96-bit nonce per save, AAD = schema, peer, host hash | Met |
| Tag verified before use; no decrypt-then-check | Met |
| Wrong key: every peer refused; tamper or host change: that peer refused | Met |
| Key never regenerated silently; `master.key` missing next to `secrets.enc` is not repaired | Met |
| DPAPI `CurrentUser` + public entropy; unreadable on account change is `KeyProtectionFailed` | Met in code; scope not pinned by a test (L-5) |
| Linux: `master.key` 0600, dirs 0700, atomic replace, directory fsync | Met |
| Network-filesystem warning | Met for cifs, smb3, smbfs, nfs*, fuse.*, 9p, UNC; gaps in L-8 |
| T4: no token in logs; Redact unit tests | Logs clean for K2; `Scrub` weak (M-2, M-3) |
| The XML and every admin JSON contain no token (grep) | Met today (no admin API yet); stale config page noted (L-10) |

## For K5, concretely

1. Fix M-1 before K5 writes the first real token (a few lines plus three test rows).
2. Do not call `Redact.Scrub` from K5 on peer-supplied text until M-2 and M-3 are fixed; log fixed sentences, status codes, `Redact.Error(ex)`, `Redact.Peer(name)`, `Redact.Id(key)`, `Redact.Host(url)`.
3. Validate the token (`^[0-9a-f]{32}$`) and device id before `PutAsync` (and preferably in `PeerCredential`, L-6).
4. Host change must call `RemoveAsync` before writing the new `baseUrl` (4.4); `RemoveAsync` throws while the store is unreadable, so `PUT Peers` must refuse the host change in that state rather than proceed.
5. Surface `Info.RefusedPeerKeys` and `VolumeWarning` in the banners; `ResetAsync` behind the typed-name body.
6. Expect `PeerDirectory.Find` to stay the only reader of `TryGet`; add a test that a tampered real `SecretStore` makes `Find` return null.

## Verdict

**GO** for K5 pairing on top of K2, with M-1 fixed first and the `Scrub` restriction in point 2. No Critical or High findings; the encryption, binding, tamper and key-selection logic matches DESIGN 8.2 and the previous review's N-7 items (key format, `_FILE`, DPAPI caveat and public entropy, AAD with peer and host) are closed. Re-review needed: only the Redact rewrite (M-2/M-3), together with the K5 review.
