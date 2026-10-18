# Jellyfin Federation: Roadmap

Share libraries between friends' Jellyfin servers on different networks (public HTTPS or private Tailscale node sharing), with no files copied and nothing exposed that the owner didn't choose to expose. Works in any client (web, Moonfin, mobile) because federated titles become normal library items.

## Principles
1. **Secure by default.** Every endpoint authenticated. Admin-only config. Peers get a scoped, revocable identity that can only read the libraries the owner shares. No secrets in `.strm` files, URLs on disk, or logs.
2. **Local wins.** If you already own a title, your copy plays. A peer's better copy can appear as an alternate version.
3. **Fail soft.** An offline peer hides or greys out its titles; it never breaks your server or hangs a scan.
4. **Tell the truth about speed.** Measure each peer link and say what will play smoothly before anyone presses Play.

## Phases
| Phase | Goal | Owner | Exit criteria |
|---|---|---|---|
| 0 Research + lab | Answer the risky unknowns; two-server lab on container | Scout, Pilot | `docs/research/scout-01.md`; lab up with test media |
| 1 Design | MVP architecture + security model | Atlas | `docs/DESIGN.md` reviewed by Warden |
| 2 Spike | Prove a federated movie plays, seeks and transcodes in web + Moonfin | Kiro, Pilot | Video evidence of playback in the lab |
| 3 MVP | Pairing, catalog sync (movies), authenticated stream handoff, dedup (local wins), config page | Kiro | Builds clean; Warden finds no High issues; Pilot passes test plan |
| 4 Speed + diagnostics | Latency + throughput per peer, per-quality verdicts, bottleneck hints, Tailscale relay detection | Kiro | Dashboard shows real numbers against a throttled lab link |
| 5 TV + two-way | Series/season/episode sync, both servers sharing | Kiro | Pilot passes TV test plan |
| 6 Release | Public GitHub repo, CI builds via jprm, plugin repository manifest, README with Tailscale guide | Manager | Installable from a repo URL on a clean Jellyfin |

## Deferred / needs owner approval
- Putting production Jellyfin (container) on Tailscale (needs a container restart).
- Creating the public GitHub repo and first release (publishing is outward-facing).
- Pairing with a real friend's server.
