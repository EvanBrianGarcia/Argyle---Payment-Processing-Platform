# ADR-0015: Visual direction — Argyle brand chrome over a data-dense operational surface

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 5
**Related:** master plan §13 Phase 5 deliverable ("a real visual direction (Swiss / data-dense, not stock card-grid Tailwind)"), ECC `web/design-quality.md`, the Stitch design system at `stitch/argyle_operations_system/DESIGN.md`, brand reference `argylepayments.com`

## Context

The Phase 5 deliverable explicitly says the dashboard must have "a real visual direction" and must not look like stock card-grid Tailwind. Two anchors apply: (a) the existing Argyle brand — deep indigo, lavender accent, signature argyle/diamond pattern, humanist Inter type, generous whitespace — visible on `argylepayments.com`; (b) the operational surface itself is data-dense, table-led, and needs to read at a glance during incidents. The Stitch design exploration consolidated both into a single `DESIGN.md` token set. This ADR adopts that token set as authoritative and records the constraints code review will enforce.

## Decision

**Tokens are the contract.** All color, type, spacing, radius, and elevation values come from `stitch/argyle_operations_system/DESIGN.md` and are mirrored verbatim into `frontend/src/styles/tokens.css` as CSS custom properties. No component file declares a raw color, font size, or pixel-radius value — only token references.

**Palette anchors.** Argyle deep indigo `#3F3D7A` for primary actions and focus rings; primary brand color `#282662` for emphasis surfaces; lavender `#8783C2` for secondary brand actions; status colors (Settled green `#16A34A`, Failed red `#DC2626`, Refunded amber `#D97706`) with paired tinted backgrounds. Page background is the near-white purple `#FCF8FF` rather than pure white — this is the warm-purple cohesion the brand depends on.

**Typography.** Inter throughout the UI; JetBrains Mono reserved for technical identifiers (payment IDs, idempotency keys, trace IDs, JSON payloads). `display-lg` (32px/700) for hero amounts and dashboard summaries; `label-md` (12px/600 uppercase, +0.02em tracking) for eyebrow labels and table headers; `code-md` for all monospaced identifiers.

**Layout.** 12-column grid, 1280px content max-width (the Stitch system specifies 1440px for the outer container; we cap interactive content at 1280px to keep line lengths readable), 24px gutters, 32px outer margin on desktop. 4px spacing baseline. Border radius: 6px on interactive controls (buttons, inputs, chips); 10px on structural panels (cards, dropdowns). One soft shadow step only: `0 1px 2px rgba(27, 27, 46, 0.05)`. No stacked shadows.

**Brand chrome.** A 4px "argyle strip" — the tiled diamond pattern in the brand indigo — sits at the very top of the page above the nav. The same pattern appears at 5% opacity as a wash behind empty states and the 404 panel. It never tiles across working data surfaces.

**Banned patterns** (carried forward from ECC `web/design-quality.md`):

- Tailwind dependency, including its preflight reset.
- Card-grid hero with uniform padding, gradient backgrounds, or decorative blobs.
- Dark-mode-by-default.
- Glassmorphism, neumorphism, or stacked drop-shadows.
- Center-screen spinners for loading states (skeletons instead).
- Generic empty-state illustrations.

**Required qualities.** Every working surface demonstrates at least four of: hierarchy via scale contrast (`display-lg` amount next to 13px secondary text), intentional spacing rhythm (4px baseline), depth through tonal layering (Level 0 background + Level 1 panel + 1px border), typography pairing with semantic intent (Inter for prose, JetBrains Mono for IDs), color used semantically (status colors do not appear on non-status elements), hover/focus/active states that visibly differ from default.

## Consequences

**Positive.** The token file is the single source of truth, so a code review can mechanically reject a raw hex literal or a `box-shadow: 0 4px 16px ...`. The brand identity carries across product surfaces without us hand-rolling a brand book.

**Negative.** A reviewer accustomed to a Tailwind workflow will need to learn the token names. A small `docs/visual-direction.md` reference card mitigates this.

**Neutral.** When Stitch's token set evolves we update `DESIGN.md` and regenerate `tokens.css`; we treat them as the same artifact.

## Alternatives

- **Pure Swiss / International style (Helvetica, hard grid, monochrome).** Cleaner editorial feel but loses the Argyle warmth and the indigo brand recognition. Rejected — the marketing site sets the brand expectation; the dashboard should not feel like a different company's product.
- **A Tailwind + shadcn/ui base.** Faster to scaffold; loses the anti-template guarantee and accumulates default-look pressure with every component import. Rejected.
- **Material Design 3 tokens directly (the Stitch DESIGN.md leans on M3 surface naming).** We keep the surface names internally as a convenience but do not import a Material runtime. The visual language is our own.

## Notes

When in doubt, the screenshots in `stitch/payments_list_default/screen.png` and `stitch/payment_detail_failed/screen.png` are the visual contract for Tasks 4 and 5 respectively. The component sheet at `stitch/argyle_component_library/screen.png` is the contract for Task 3.
