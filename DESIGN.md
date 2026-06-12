# Design

Captured from `src/frontend/filetracert-mockup.html`, the committed visual source of truth. The Angular design system (`src/frontend/src/app/styles/`) implements these tokens 1:1.

## Theme

Dark, single theme. Deep blue-black background with two faint radial brand glows (teal top-right, lime bottom-left). The app is a framed "desktop window": titlebar + 204px left nav + scrolling main. Mood: instrument panel — quiet, dense, technical. Color strategy: **Restrained** — tinted dark neutrals carry the surface; teal is the single accent for primary/selection/identity, with a small semantic state palette (lime/amber/red/blue) reserved for status.

## Color

OKLCH for any new tints; the canonical hexes below come from the mockup and are preserved exactly.

### Surfaces (dark neutrals, cool)
- `--bg: #0c1116` — app body / deepest
- `--bg-2: #0f151c` — app frame
- `--panel: #141b23` — panels, cards, nav
- `--panel-2: #19222c` — panel headers, hover rows
- `--panel-3: #1f2a36` — bar track, idle pill
- `--line: #28333f` — borders
- `--line-soft: #1e2731` — inner row separators

### Ink
- `--txt: #e7eef4` — primary text (AA on all surfaces)
- `--txt-dim: #8997a6` — secondary / labels
- `--txt-faint: #566373` — mono technical values, captions

### Brand + semantic state
- `--teal: #2ec4b6` — primary accent: actions, active nav, selection, identity. `--teal-dim: rgba(46,196,182,.13)`
- `--lime: #a8e063` — online / done / success
- `--amber: #f0a830` — warning / waiting / stale estimate
- `--red: #e2596a` — blocked / error / video tag
- `--blue: #5aa2ff` — running / info / image tag

Status is never color-only: every pill carries a dot + a text label.

## Typography

- **IBM Plex Sans** (400/500/600/700) — all structural UI: headings, labels, body, buttons.
- **IBM Plex Mono** (400/500/600) — technical values only: paths, Volume GUIDs, USN checkpoints, file counts, sizes, letters, timestamps, the "stima · 2g fa" badge. Mono = "real data".
- No display face. Fixed rem scale (product register), not fluid.
- Scale: h1 21px/600/-.4px · card value 27px/600/-.5px · body 13–13.5px · label 10.5px uppercase tracked .5–.7px · mono 11.5px.

## Components

- **Titlebar** — gradient `panel→bg-2`, 26px teal→lime rounded logo "F", brand `FileTracert` + faint `by FAD.iT`, right tray with a lime `pulse` dot = "servizio attivo".
- **Nav (204px)** — `--panel`, uppercase section labels (`Workspace`/`File`/`Operazioni`), nav items radius 9px; active = `teal-dim` bg + teal text + 3px teal left indicator bar; `.soon` items 40% opacity, non-interactive; amber count badge (`qbadge`) for queue; mono footer with pulse + scan summary.
- **Card (stat)** — `--panel`, 1px `--line`, radius 11px, faint radial `--accent` glow top-right. Uppercase label + 27px value with small `<small>` unit + faint meta line.
- **Panel** — bordered container, header (`--panel-2`) with title + right-aligned faint mono caption; wraps tables and key/value detail.
- **Table** — uppercase faint th, 12×17px td, `--line-soft` row separators, `--panel-2` row hover; selected row teal-tinted. `.kv` variant = label column 175px `--txt-dim`.
- **Pill** — radius 20px, 6px dot + label. Variants: `on`(lime) `off`(dim) `run`(blue) `block`(red) `wait`(amber) `done`(lime) `idle`(panel-3). Online/running dots carry a soft glow.
- **Bar** — 5px track `--panel-3`, fill width%; fill color encodes headroom (teal ok / amber tight / lime low-use) or progress (blue).
- **Stale badge** — amber mono "stima · Ng fa" next to last-known figures on offline volumes.
- **Button** — `primary` (teal bg, ink text), `ghost` (transparent, `--line` border, brightens on hover). Radius 9px. Disabled = dimmed, no hover.
- **Inputs / chips** (search, step 7) — `--panel` bg, teal focus border; chips toggle to teal-outline when active.
- File-type tag chip (`fic`) — 28px rounded, category-tinted mono label (IMG/MP4/DOC/DIR).

## Layout

- Flexbox everywhere (per brief). App = column (titlebar / shell). Shell = row (nav / main). Main scrolls independently with a styled thin scrollbar.
- Cards: responsive grid `repeat(auto-fit, minmax(…,1fr))` (mockup fixes 4 cols; collapse gracefully).
- Radius scale: 16px app frame · 11px panels/cards · 9px nav items/buttons/inputs · 20px pills/chips · 7–8px small tags/logo.
- Huge lists (Catalogo/Ricerca, step 7) → CDK Virtual Scroll.

## Motion

- `rise` — content children fade+translateY(10px)→0, 0.42s ease-out cubic, staggered 50ms per child on screen enter. Product-appropriate (state/reveal, brief).
- `pulse` — service-alive lime dot, 2.4s expanding ring.
- Transitions 120–160ms on hover/focus (nav, rows, chips, buttons).
- `prefers-reduced-motion: reduce` → no rise/pulse, instant/crossfade only.
