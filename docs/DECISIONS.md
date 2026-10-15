# Owner decisions log

Decisions made by the owner. These override open questions in DESIGN.md and ui-spec.md.

## 2026-10-14

| # | Topic | Decision |
|---|---|---|
| D1 | Name | **Shoal** ("Shoal for Jellyfin"). Tagline: "Your friends' libraries, next to yours. Nothing copied, nothing exposed." |
| D2 | Visual direction | **Native Plus** (Lumen), built per `docs/ui-spec.md`. |
| D3 | Who sees a friend's library (default) | Default-deny for other users. When a peer library is added, **the admin who added it is pre-ticked** so they can test immediately; nobody else is ticked. |
| D4 | Rolling it out | Each federated library has an access mode: **"Only selected users"** (default, see D3) or **"Everyone on this server"**, a single switch the admin flips once they've tested it. "Everyone" includes users created later. Switching back to "Only selected users" restores the previous selection. |
| D5 | Offline peer card | Show the stored backdrop **greyed out** (not the striped "No preview" panel). Titles stay listed and fail fast with a clear message. |
| D6 | "Friends watching from you" | Stays at the **bottom of the Overview** page (no separate tab). |
| D7 | Friend needs Shoal installed? | **No** for the MVP: a restricted share user + Quick Connect is enough. Installing Shoal on the peer is optional (two-way sharing, invite path, peer-side hardening). |
| D8 | Peer sees watched titles | OK (never who watched). |
| D9 | Peer-side stream hardening | Ships in **log-only** mode with a one-click enforce. |
| D11 | Native apps (RELEASE BLOCKER) | Shoal must work in the **Moonfin apps on iOS, Android and TV**, not just the browser. Treat any of these failing as a blocker. Prove compatibility EARLY (client source analysis + request-replay tests in the lab + Moonbase installed on the lab server to mirror production), before the full MVP is built. |
| D12 | Which apps the MVP supports (2026-10-15) | **Moonfin is the target** (all platforms: iOS, tvOS, Android, Android TV, Fire TV, Smart-TV, Roku) plus jellyfin-web. The official Jellyfin Android TV app, Swiftfin, Findroid and external players stream with no credentials, so Shoal answers 401 for them in the MVP (DESIGN 6.12, G1 Option C); documented as a known limitation. Option A (grant-matched anonymous access) only after a Warden review. |
| D13 | Streams per account (2026-10-15) | Up to **4** simultaneous federated streams per local user, across all friends' servers (`MaxStreamsPerUser` clamped 1..4, default 4). |
| D14 | Real-device testing (2026-10-15) | Owner has an **iOS phone** for Moonfin testing now. Other platforms read "should work" in the help text until they pass on a real device (W4-15). |
| D15 | Conversions per friend server (2026-10-15) | Up to **4** simultaneous conversions (transcodes) per friend server, Home side (`maxConcurrentTranscodes` default 4, 1..`maxConcurrentStreams`) and the Peer-side share default (8.4 (c)). Owner accepts the friend-CPU risk; the admin can lower it per server. |
| D16 | Full v1 scope kept (2026-10-15) | K21 ShareService (the sharing account is created and locked automatically), K22 invite pairing, K23 PeerGuardFilter and the "Friends watching from you" dashboard (D6, U2) all stay in **v1**. A brief cut to Home-only was reverted the same day. |
| D10 | Team | Add a second coder ("Kiro 2") once DESIGN v2 lands: Kiro = backend core (pairing, sync, playback security path); Kiro 2 = admin UI + speed diagnostics. Separate git worktrees; manager merges; Pilot serializes lab deploys. |
