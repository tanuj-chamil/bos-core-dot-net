---
version: 1
slug: "site-src-styles-css"
primary_target: "site/src/styles.css"
related_targets: ["site/build.mjs","site/src/site.js"]
---

# BOS Core documentation site

Mode: Read. Primary target: `site/src/styles.css`. Related targets: `site/build.mjs`, `site/src/site.js`.

Audience: operators and API integrators deploying, securing, exercising, and recovering BOS Core. The primary job is to find the next safe action quickly; architecture and release evidence remain available to maintainers and reviewers.

Content: canonical Markdown under `docs/`, led by overview, API usage, examples, operations, and security boundaries. The output is static and publishes through GitHub Pages. Markdoc and Stripe Docs set the conventional documentation craft bar.

## Direction contract

**THESIS:** BOS Core documentation is an operator's dependable map: practical paths are obvious, precise evidence is close at hand, and the site refuses both a marketing homepage and a visually theatrical reading column.

**OWN-WORLD:** A light mineral ground supports an ink-blue navigation frame, white reading sheet, precise cool-gray rules, and one clear blue action color. Compact sans navigation, generous humanist body copy, square code surfaces, and small status lozenges form a restrained technical system.

**STORY:** The reader starts with setup and first API use, then moves through authentication, operations, examples, and recovery. Reference, architecture, and measured build evidence stay one navigation step away without competing with the task path.

**FIRST VIEWPORT:** A slim product masthead sits above a three-column documentation shell: persistent grouped navigation at left, the overview title and direct opening in the center, and an on-this-page index at right. A compact quick-path row leads into a real initialization command, with the repository action in the masthead.

**FORM:** Conventional technical documentation shell, selected as the standing canon over three more expressive systems; `seed:c6d66b6bfd6443eb`. Markdoc supplies editorial clarity and Stripe supplies the finish bar for code, navigation, and responsive behavior.

**FINISH:** unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

Constraints: preserve `docs/` as the canonical source, preserve review-build caveats, avoid invented claims, meet keyboard and contrast requirements, work from 390px through desktop, and require no server runtime after build.
