# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Delegated: compile curated public Markdown documentation with Markdoc into a static site that GitHub Pages can host without a server runtime.

## Users

Primary users are developers and operators evaluating, integrating, securing, and recovering a single-tenant BOS Core deployment.

## Product Purpose

BOS Core is a single-tenant .NET business API with SQLite-backed records, identity, typed operations, and atomic YAML flows. The documentation site should let a technical reader move from local evaluation to authenticated API use, business workflows, secure deployment, and routine recovery.

## Positioning

BOS Core keeps business data, authorization, idempotent operations, and deterministic flow execution inside one reviewable host and workspace. Its documentation must distinguish measured review-build evidence from production certification.

## Operating Context

Readers work from a terminal, an HTTPS deployment, JSON and YAML definitions, HTTP clients, and offline backup or recovery procedures. Public website content lives under `site/content/`; internal architecture, acceptance, provenance, and release-engineering records remain under `docs/` and are not website navigation.

## Capabilities and Constraints

- The site must render curated public documentation through Markdoc.
- The output must be static and deployable through GitHub Pages CI.
- Commands, contracts, security warnings, and review-build caveats must remain precise.
- The site must work on desktop and mobile and remain keyboard accessible.
- Internal architecture, decisions, acceptance tracking, dependency inventories, implementation plans, and build reports must not be published as product documentation.

## Brand Commitments

Use the name BOS Core and its existing direct, technical, evidence-led voice. Do not turn review-build measurements into production claims.

## Evidence on Hand

The repository contains implementation evidence under `docs/` and public example definitions under `docs/examples/`. Public guidance must be based on verified product behavior without exposing internal release records as navigation. No customer claims, testimonials, or external performance benchmarks are available and none should be fabricated.

## Product Principles

- Lead with the operator's next concrete action.
- Keep public source pages readable in and out of the site.
- Make security boundaries and release caveats conspicuous.
- Keep internal engineering evidence in the repository without presenting it as end-user documentation.
- Prefer a small, inspectable static toolchain.

## Accessibility & Inclusion

Use semantic HTML, visible keyboard focus, sufficient contrast, reduced-motion support, responsive navigation, and a readable long-form measure.
