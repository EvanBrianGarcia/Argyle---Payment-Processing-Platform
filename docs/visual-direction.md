# Visual Direction — Argyle Payments Operations Dashboard

This document is the everyday reference for the dashboard's visual language. The authoritative token values live in `frontend/src/styles/tokens.css` (mirrored from `stitch/argyle_operations_system/DESIGN.md`); ADR-0015 records the rationale. Use this doc when you need a quick mental model.

## Brand mood

Warm-purple corporate fintech. The marketing site at argylepayments.com sets the tone: deep indigo, lavender accent, white surfaces, signature argyle/diamond pattern, humanist sans-serif. The dashboard inherits the brand chrome (logo, indigo accents, the 4px argyle strip at the very top of the page) but its working surfaces are dense — table-led, monospaced IDs, tabular numerals — because this is an instrument an engineer trusts at 2am during an incident.

## Color tokens

| Token | Value | Use |
|---|---|---|
| `--color-surface` | `#FCF8FF` | Page background. A near-white purple — never pure white. |
| `--color-surface-container-lowest` | `#FFFFFF` | Elevated panels and cards. |
| `--color-surface-secondary` | `#F7F7FB` | Table header strip, filter bar background. |
| `--color-surface-subtle` | `#EDECF7` | Hover tint for ghost interactions; active filter chip fill. |
| `--color-divider` | `#E6E6EE` | Hairline 1px dividers and table grid lines. |
| `--color-on-surface` | `#1A1A2D` | Primary text. |
| `--color-text-secondary` | `#5A5A75` | Secondary text. |
| `--color-text-muted` | `#9494AB` | Muted text, currency suffixes, "Showing N of M". |
| `--color-primary` | `#282662` | Brand primary (logo, navigation active state). |
| `--color-primary-container` | `#3F3D7A` | Primary buttons, focus rings, accent strip. |
| `--color-secondary` | `#5B5893` | Secondary brand text. |
| `--color-secondary-container` | `#BFBAFD` | Soft brand surface (action chips). |
| `--color-status-settled` / `-bg` | `#16A34A` / `#E8F7EE` | Settled status badge. |
| `--color-status-failed` / `-bg` | `#DC2626` / `#FCEBEB` | Failed status badge. |
| `--color-status-refunded` / `-bg` | `#D97706` / `#FEF3E2` | Refunded status badge. |
| `--color-status-pending` / `-border` | `#5A5A75` / `#CBD5E1` | Pending outlined badge. |
| `--color-error` / `-container` | `#BA1A1A` / `#FFDAD6` | Error notices. |

Status colors are semantic. They never appear on non-status elements.

## Typography

Inter is the UI typeface. JetBrains Mono is reserved for technical identifiers — payment IDs, idempotency keys, trace IDs, JSON payloads.

| Token | Spec | Use |
|---|---|---|
| `--text-display-lg` | Inter 32px / 700 / -0.02em | Dashboard summaries, detail-page amount. |
| `--text-headline-md` | Inter 24px / 600 / -0.01em | Page titles. |
| `--text-headline-sm` | Inter 18px / 600 | Section headers (Event timeline, Details). |
| `--text-body-lg` | Inter 16px / 400 | Lead paragraphs, empty-state copy. |
| `--text-body-md` | Inter 14px / 400 | Default body. Tabular numerals on table cells. |
| `--text-body-sm` | Inter 13px / 400 | Secondary text, dense tables. |
| `--text-label-md` | Inter 12px / 600 / +0.02em / UPPERCASE | Eyebrow labels, table column headers, status badges. |
| `--text-code-md` | JetBrains Mono 13px / 400 | Payment IDs, idempotency keys, trace IDs. |
| `--text-code-sm` | JetBrains Mono 11px / 400 | Timestamps in the timeline. |

Tabular numerals (`font-variant-numeric: tabular-nums`) are enabled globally so amounts and counts align in tables without extra spans.

## Layout & spacing

Spacing baseline is 4px. The exposed scale on the `--space-*` tokens is `4 / 8 / 12 / 16 / 24 / 32 / 48 / 64`. Content max-width is 1280px; outer page margin is 32px desktop. Grid is 12 columns, 24px gutters.

## Radius & elevation

- Interactive controls (buttons, inputs, chips): 6px (`--radius-sm`).
- Structural panels (cards, dropdowns): 10px (`--radius-md`).
- Status badges: pill shape (`--radius-full`).
- Shadow: one step only — `0 1px 2px rgba(27, 27, 46, 0.05)` (`--shadow-1`). Hover does not increase elevation; it shifts surface tint to `--color-surface-subtle`.

## The argyle strip

A 4px-tall strip across the very top of the page renders the tiled argyle/diamond pattern in the brand indigo. The same pattern appears at 5% opacity as a wash behind empty states and the 404 panel. It is purely brand chrome — never tiled across working content.

## Button system

Four button kinds:

1. **Primary** — `--color-primary-container` fill, white text, UPPERCASE label, `--text-label-md`, 6px radius. Hover darkens to `--color-primary`.
2. **Secondary outlined** — white fill, `--color-primary-container` border + text, same uppercase label treatment. Hover fills with `--color-surface-subtle`.
3. **Tertiary lavender** — `--color-secondary-container` fill, dark text, same uppercase label. Used for brand actions like "Contact Support".
4. **Ghost** — no border, `--color-text-secondary` text, sentence-case (not uppercase). Used inside data UI for in-table actions like "Retry" and pagination.

## States

- **Loading:** flat skeleton rectangles in `--color-divider`. No shimmer animation. Sized to match the real layout to prevent layout shift.
- **Empty:** a centered block on the page surface with a subtle argyle pattern wash behind it; one primary message line and one secondary line; a single ghost CTA.
- **Error:** an inline notice with a left red bar, monospace error `code`, primary message, and the `requestId` from the `ErrorEnvelope` rendered selectable in muted monospace.

## Accessibility floor

WCAG AA contrast on every text/background pair. Focus ring is 2px `--color-primary-container` inset for table rows, 2px outline for buttons. Keyboard: arrow keys on the row cursor, `/` focuses the search input, `Esc` clears the active filter. Skeletons and the argyle strip both respect `prefers-reduced-motion`.

## When to deviate

You shouldn't. If a screen feels like it needs a value not in the tokens, that's a signal to extend the token set in `DESIGN.md` (and update this doc) rather than to drop a literal into a component file.
