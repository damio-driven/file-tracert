# Product

## Register

product

## Users

Single power user on their own Windows machine: a photographer / videographer / collector with files spread across many internal and removable drives. They work at the desktop, often with some drives physically unplugged, trying to find, tidy and relocate large media without remembering which disk holds what. Technical enough to care about volumes, filesystems and USN; not a sysadmin.

## Product Purpose

FileTracert scans, catalogues and organizes files across local and removable drives, keyed to stable Volume GUIDs rather than drive letters. It indexes only the file types the user chooses, lets them search and browse the catalogue **even while drives are offline**, and queues move/rename/organize operations that execute automatically when the involved volumes reappear. Success = the user trusts the catalogue as the source of truth for "where is everything and what's about to move", and never loses a file to a half-finished operation.

## Brand Personality

Precise, technical, calm. Three words: **trustworthy, instrumented, unshowy.** It should feel like a well-built instrument panel: every figure is real and dated, nothing is hidden, the freshness of offline data is always honest. Mono type for technical values (paths, GUIDs, USN, counts) signals "this is the actual data, not marketing".

## Anti-references

Consumer cloud-storage marketing UI (Dropbox/Google Drive web). No friendly illustrations, no rounded pastel cards, no gradient hero metrics. Not a flashy "AI organizer". Not Bootstrap/Material default chrome. Avoid anything that hides technical truth (drive letters as identity, undated estimates presented as live).

## Design Principles

- **Honest freshness.** Live data and last-known snapshots are always visually distinct; offline figures carry a dated "stima" badge. Never present stale as live.
- **Identity is the GUID, the letter is a hint.** Surface the stable identity; treat drive letters as informational, mutable, sometimes absent.
- **The catalogue works unplugged.** Offline volumes are first-class: browsable, searchable, dimmed but present, never an error state.
- **Density with rhythm.** Dense technical tables are welcome, but spacing and panels keep them readable; mono for values, sans for structure.
- **No destructive surprises.** Queued operations are visible, dated, reversible; the UI states what will happen before it happens.

## Accessibility & Inclusion

Dark theme, WCAG AA for text contrast on the dark surfaces. Status is never color-only: pills pair a color with a label (Online/Scollegato/Bloccato) and a dot, so color-blind users read state from text. Respect `prefers-reduced-motion` (the mockup's rise/pulse animations degrade to static). Keyboard-navigable nav and tables.
