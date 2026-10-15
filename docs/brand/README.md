# Shoal brand

The mark is three fish swimming into one circle: friends' libraries pooled across servers. It is hand-built SVG (no fonts, no raster). The lockup wordmark is stroke-built letters, so it renders the same everywhere. Only the small "FOR JELLYFIN" line uses a system font stack.

## Colors

| Role | Hex | Contrast on white / on #101010 |
|---|---|---|
| Fish 1, cyan | `#0f9bd0` | 3.2 / 6.0 |
| Fish 2, blue | `#4a7fe0` | 3.9 / 4.9 |
| Fish 3, violet | `#8b5fd6` | 4.5 / 4.3 |
| Tile navy (lockup, avatar, social) | `#0f1a24` | |
| Wordmark ink on navy | `#eef6fb` | |
| Tagline on navy | `#9db3c4` | |

The three fish colors are flat fills, no gradient. They sit near Jellyfin's cyan-to-purple range without copying its gradient. The mark meets 3:1 on white and on Jellyfin dark, so there is no separate dark or light variant. The lockup brings its own navy tile instead.

## Clear space and minimum size

- Clear space: keep empty space around the mark equal to one fish eye-to-tail-fork distance, about one quarter of the mark's diameter. Nothing else (text, borders, other logos) goes inside it.
- Minimum size: the mark alone, 16 px (favicon only; the three fish read as a notched circle). Use 24 px or larger anywhere people are expected to recognize it. The lockup, 150 px wide.
- Do not recolor the fish, add a gradient or outline, rotate the mark, or place the wordmark on a light background without the navy tile.

## Which file goes where

| File | Use |
|---|---|
| `shoal-plugin.png` (600 x 300) | Plugin catalog image (Jellyfin shows it at about 300 x 150) |
| `shoal-social-1280x640.png` | GitHub repository social preview (Settings > Social preview) |
| `shoal-avatar-512.png` | GitHub organization or account avatar. The mark is padded to survive the circle crop |
| `shoal-favicon-32.png`, `shoal-favicon-16.png` | Browser tab icons, if a page needs one |
| `shoal-mark.svg` | The mark alone, transparent: README header, docs, any small use |
| `shoal-lockup.svg` | Mark plus wordmark on the navy tile, 2:1 (300 x 150), rounded corners |
| `shoal-avatar.svg` | Source for the avatar PNG |

The Jellyfin dashboard menu item uses Material's `share` glyph through `MenuIcon` (a name, not an image), so the SVG is not used there.

PNGs are rendered from the SVGs with headless Edge at exact size (square corners for the PNG lockups). `preview.html` shows everything on dark and light surfaces.
