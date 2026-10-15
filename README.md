# Shoal

A Jellyfin plugin that lets friends' servers share libraries. See `docs/DESIGN.md`.

## Notes for operators

- **Transcode limit (Warden L7).** The per-share transcode limit covers sharing accounts only, and only a request that names a `PlaySessionId`. Anonymous transcodes (the routes core serves without a sign-in) are not counted when the hardening mode is `LogOnly` or `Off`; `Enforce` refuses them without a sign-in. Treat the limit as a courtesy to the host, not a hard cap.
- **Pairing behind a reverse proxy (Warden L8).** The Peer cannot check that the invite request reached it over HTTPS: the token is handed to Home in the answer to `POST /Shoal/Pair`. If the Peer sits behind a reverse proxy, serve it over TLS only and do not expose a plain-HTTP port. Home refuses to redeem an invite over cleartext unless the address is on a Tailnet.

## License

GPL-3.0. See [LICENSE](LICENSE).
