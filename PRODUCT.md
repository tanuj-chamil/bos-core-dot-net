# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Delegated: compile the canonical Markdown documentation with Markdoc into a static site that GitHub Pages can host without a server runtime.

## Users

Primary users are BOS Core operators and API integrators preparing, securing, exercising, and recovering a single-tenant deployment. Maintainers and release reviewers are a secondary audience for architecture and conformance evidence.

## Product Purpose

BOS Core is a single-tenant .NET business API with SQLite-backed records, identity, typed operations, and atomic YAML flows. The documentation site should let a technical reader move from first deployment to authenticated API use and routine operations without losing access to specification and release evidence.

## Positioning

BOS Core keeps business data, authorization, idempotent operations, and deterministic flow execution inside one reviewable host and workspace. Its documentation must distinguish measured review-build evidence from production certification.

## Operating Context

Readers work from a terminal, an HTTPS deployment, JSON and YAML definitions, HTTP clients, and offline backup or recovery procedures. Existing files under `docs/` are the canonical source and remain useful outside the website.

## Capabilities and Constraints

- The site must render existing canonical documentation through Markdoc.
- The output must be static and deployable through GitHub Pages CI.
- Commands, contracts, security warnings, and review-build caveats must remain precise.
- The site must work on desktop and mobile and remain keyboard accessible.

## Brand Commitments

Use the name BOS Core and its existing direct, technical, evidence-led voice. Do not turn review-build measurements into production claims.

## Evidence on Hand

The repository contains the product specification, architecture, usage guide, examples, decisions, implementation contracts, acceptance matrix, dependency inventory, build report, and measured build manifest under `docs/`. No customer claims, testimonials, or external performance benchmarks are available and none should be fabricated.

## Product Principles

- Lead with the operator's next concrete action.
- Keep canonical documentation readable in and out of the site.
- Make security boundaries and release caveats conspicuous.
- Separate practical guidance from specification and evidence without hiding either.
- Prefer a small, inspectable static toolchain.

## Accessibility & Inclusion

Use semantic HTML, visible keyboard focus, sufficient contrast, reduced-motion support, responsive navigation, and a readable long-form measure.
