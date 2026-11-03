---
name: BOS Core Documentation
description: A precise, evidence-led operator documentation system.
colors:
  mineral-ground: "#f7f8fb"
  reading-surface: "#ffffff"
  muted-surface: "#f0f3f8"
  ink: "#172033"
  body-copy: "#30394d"
  muted-ink: "#5d687d"
  faint-ink: "#626d81"
  rule: "#d9dfE9"
  strong-rule: "#bac4d3"
  action-blue: "#2452d6"
  action-blue-deep: "#193b9b"
  action-blue-wash: "#eaf0ff"
  code-surface: "#111827"
  code-ink: "#edf2ff"
  warning-ink: "#7a4a00"
  warning-wash: "#fff4d6"
  focus: "#8eabff"
typography:
  display:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "clamp(2.35rem, 5vw, 3.45rem)"
    fontWeight: 760
    lineHeight: 1.04
    letterSpacing: "-0.038em"
  headline:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "1.7rem"
    fontWeight: 730
    lineHeight: 1.18
    letterSpacing: "-0.025em"
  title:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "1.16rem"
    fontWeight: 700
    lineHeight: 1.35
  body:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.7
  lead:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "1.08rem"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "ui-sans-serif, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "0.7rem"
    fontWeight: 720
    lineHeight: 1.3
    letterSpacing: "0.075em"
  code:
    fontFamily: "Cascadia Code, SFMono-Regular, Consolas, Liberation Mono, monospace"
    fontSize: "0.82rem"
    fontWeight: 400
    lineHeight: 1.65
rounded:
  focus: "0.125rem"
  mark: "0.2rem"
  inline: "0.25rem"
  compact: "0.3rem"
  control: "0.35rem"
  container: "0.4rem"
  panel: "0.45rem"
  dialog: "0.65rem"
spacing:
  xs: "0.5rem"
  sm: "0.75rem"
  md: "1rem"
  lg: "1.5rem"
  xl: "2rem"
  2xl: "3rem"
  3xl: "4rem"
components:
  primary-link:
    backgroundColor: "{colors.action-blue}"
    textColor: "{colors.reading-surface}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    padding: "0.65rem 0.9rem"
  primary-link-hover:
    backgroundColor: "{colors.action-blue-deep}"
    textColor: "{colors.reading-surface}"
  search-trigger:
    backgroundColor: "{colors.reading-surface}"
    textColor: "{colors.muted-ink}"
    rounded: "{rounded.panel}"
    padding: "0.48rem 0.55rem 0.48rem 0.72rem"
  navigation-active:
    backgroundColor: "{colors.action-blue-wash}"
    textColor: "{colors.action-blue-deep}"
    rounded: "{rounded.control}"
    padding: "0.38rem 0.7rem"
  code-block:
    backgroundColor: "{colors.code-surface}"
    textColor: "{colors.code-ink}"
    typography: "{typography.code}"
    rounded: "{rounded.panel}"
    padding: "1.15rem 1.3rem"
  review-strip:
    backgroundColor: "{colors.warning-wash}"
    textColor: "{colors.warning-ink}"
    height: "2.125rem"
  search-dialog:
    backgroundColor: "{colors.reading-surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.dialog}"
    width: "min(42rem, calc(100vw - 2rem))"
---

# Design System: BOS Core Documentation

## Overview

**Creative North Star: "The Operator's Dependable Map"**

BOS Core is a restrained technical reading system: a light mineral frame, a white document sheet, cool rules, ink text, one blue action family, and an amber review warning. It favors orientation, legibility, and evidence over marketing gestures or decorative spectacle.

The interface is compact around navigation and generous in the reading column. Its hierarchy comes from weight, measure, rules, and tonal surfaces rather than illustration or heavy elevation. Public content lives in `site/content/`; `site/build.mjs` owns semantic structure, `site/src/styles.css` owns this visual system, and `site/src/site.js` owns only navigation, search, copy, and table-of-contents behavior.

**Key Characteristics:**
- Flat, cool-toned documentation surfaces with a single blue interaction family.
- Dense navigation surrounding a calm, readable central sheet.
- Persistent, unmistakable separation between review evidence and release certification.
- Small system-sans type and restrained radii rather than expressive display styling.

## Colors

The palette is role-driven and intentionally narrow; the frontmatter values are normative and map directly to the CSS custom properties in `site/src/styles.css`.

### Primary
- **Action Blue** (`action-blue`): product mark, list markers, primary links, and interactive emphasis.
- **Deep Action Blue** (`action-blue-deep`): default text links, active navigation text, and primary hover states.
- **Action Wash** (`action-blue-wash`): active navigation and informational blockquote surfaces.

### Neutral
- **Mineral Ground** (`mineral-ground`): page frame, masthead tint, and mobile navigation.
- **Reading Surface** (`reading-surface`): article sheet, search controls, and dialog.
- **Muted Surface** (`muted-surface`): hover fills, table headers, and compact control fills.
- **Ink / Body Copy** (`ink`, `body-copy`): headings and long-form text.
- **Muted / Faint Ink** (`muted-ink`, `faint-ink`): metadata, supporting copy, navigation, and table of contents.
- **Rule / Strong Rule** (`rule`, `strong-rule`): structural dividers and control borders.
- **Code Surface / Code Ink** (`code-surface`, `code-ink`): dark command and source blocks.

### Secondary
- **Warning Ink / Warning Wash** (`warning-ink`, `warning-wash`): the persistent review-build status strip only.

**The One Blue Voice Rule.** Blue communicates action, selection, or linked structure; do not introduce competing accent hues.

**The Evidence Amber Rule.** Amber is reserved for review and certification caveats, never routine decoration.

## Typography

**Display and Body Font:** the native UI sans stack (`--font-ui` and `--font-body` are identical).

**Label/Mono Font:** Cascadia Code with SFMono-Regular, Consolas, Liberation Mono, then monospace.

**Character:** The system uses one practical sans family for both navigation and prose, differentiated by weight and measure. Monospace is reserved for executable or literal material.

### Hierarchy
- **Display** (`typography.display`): article H1, balanced and limited to 18ch; mobile uses `clamp(2.15rem, 11vw, 3rem)`.
- **Headline** (`typography.headline`): H2 section breaks with substantial top space; compact screens reduce it to 1.5rem.
- **Title** (`typography.title`): H3 subsections.
- **Body** (`typography.body`): long-form prose on a 70ch maximum measure; the opening paragraph uses `typography.lead` and a 62ch measure.
- **Label** (`typography.label`): uppercase navigation groups and table-of-contents labels; ordinary controls and links remain sentence case.
- **Code** (`typography.code`): fenced blocks; inline code uses the same family at 0.86em.

**The Literal Material Rule.** Use monospace for commands, code, identifiers, and version literals, not as a decorative brand font.

## Layout

The desktop shell is centered at a 100rem maximum and uses `16rem / minmax(0, 52rem) / 14rem` columns. The article sheet carries `2.6rem clamp(2rem, 5vw, 4.6rem) 4rem` padding, while the prose itself stops at 70ch. The masthead is 4rem high and the review strip is 2.125rem; both remain sticky, as do the left navigation and right table of contents.

Spacing follows the frontmatter scale for common rhythm, with deliberate in-between values retained where the reading flow needs them. Section headings use larger editorial gaps, controls stay compact, and 1px rules provide most grouping.

- **At 75rem and below:** remove the right table of contents and use a `15rem / minmax(0, 52rem)` shell.
- **At 50rem and below:** reduce the masthead to 3.65rem, replace the left rail with an off-canvas drawer up to `min(19rem, 86vw)`, stack quick paths and adjacent-page links, and use `2rem clamp(1.15rem, 5vw, 2.25rem) 3rem` article padding.
- **At 30rem and below:** hide the Docs suffix and review-build middle label, hide heading anchors, slightly reduce code type, and keep copy controls visible.
- **Print:** remove navigation, status, page controls, and copy controls; flatten the sheet and append external link destinations.

**The Reading Measure Rule.** Do not widen prose beyond 70ch to fill available space; the surrounding rails may grow or disappear instead.

## Elevation & Depth

The system is flat by default. White, mineral, muted, blue-wash, and warning-wash surfaces are separated by 1px rules rather than shadows. Elevation is reserved for transient overlays.

### Shadow Vocabulary
- **Search dialog** (`0 1.25rem 4rem rgba(22, 32, 51, 0.22)`): modal separation over a dark translucent backdrop with 3px blur.
- **Mobile navigation** (`1rem 0 3rem rgba(22, 32, 51, 0.16)`): lateral separation while the drawer is open.

**The Flat-by-Default Rule.** Persistent reading and navigation surfaces use tone and rules; shadows belong only to transient layers.

## Shapes

Corners are gently compact, not pill-shaped. Inline code and keycaps use the smallest radii, navigation and action controls use the control radius, code and search triggers use the panel radius, and only the modal search dialog reaches the dialog radius. The product mark is a square four-cell grid with subtly softened corners. Borders are normally 1px; no component uses a fully rounded silhouette.

## Components

### Masthead and Navigation
- The translucent mineral masthead uses a 1px bottom rule and 12px backdrop blur. The wordmark is compact and weight-led; the blue grid is the only brand mark.
- Left navigation uses grouped uppercase labels, sentence-case links, muted defaults, a muted-surface hover, and the `navigation-active` token for the current page.
- The right table of contents is quieter than the left rail and indicates the observed section through deep-blue text only.
- On mobile, the menu button opens a focus-managed drawer, inerts background regions, and adds a dismissible scrim.

### Actions and Search
- `primary-link` is the filled action used by the not-found page; its hover uses `primary-link-hover`.
- `search-trigger` is a bordered white field-like button on desktop and an icon-sized control on mobile. The dialog uses `search-dialog`, a border, the modal shadow, and a scrollable result list.
- Icon controls are 2.35rem square with transparent default surfaces and muted-surface hover fills.

### Reading Surfaces
- Quick paths are a border-block row with three equal cells; they stack into bordered rows on mobile.
- Code blocks use `code-block`, a subtle dark border, horizontal overflow, and a copy control that appears on hover or keyboard focus; it remains visible on compact screens.
- Tables scroll inside a bordered container. Headers use muted surfaces, rows use rules, and hover is a near-white tonal change.
- Blockquotes use an action-blue left rule and action wash. Inline code uses a pale neutral fill, cool border, and deep-blue text.

### Status and Accessible States
- `review-strip` is a sticky full-width note, not a badge. It remains horizontally scrollable on mobile so certification language is not wrapped away.
- Every keyboard target receives a 3px `focus` outline with 3px offset. A skip link moves into view on focus; the search dialog has a label and live results; overflow tables are keyboard focusable.
- Motion is limited to 140-180ms opacity or transform transitions and smooth document scrolling. Under `prefers-reduced-motion: reduce`, smooth scrolling is removed and transitions collapse to 0.01ms.

## Do's and Don'ts

### Do:
- **Do** preserve the white reading sheet, mineral frame, cool rules, and single blue interaction family.
- **Do** keep release caveats in the persistent amber review strip and retain their exact meaning.
- **Do** keep long-form prose at 70ch or less and preserve the desktop-to-drawer responsive model.
- **Do** provide visible focus, reduced-motion behavior, keyboard-operable overflow, and semantic interaction states.

### Don't:
- **Don't** add marketing hero treatments, ornamental illustration, gradients, or competing accent colors.
- **Don't** turn the amber review state into a decorative badge or imply production certification.
- **Don't** add shadows to persistent cards, navigation, code, or reading surfaces.
- **Don't** move public content or behavior into the stylesheet; content stays in `site/content/`, structure in `site/build.mjs`, and interaction in `site/src/site.js`.
