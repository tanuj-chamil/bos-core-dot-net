# BOS Core

BOS Core is a single-tenant business API for versioned data, role-based access, registered operations, and atomic YAML workflows. It runs as one .NET service with a local SQLite workspace.

> **Preview:** The current release is `1.0.0-preview.1`. APIs and stored formats may change before 1.0. Evaluate it in a controlled environment before using it for important data.

## Build around four concepts

**Types** define the shape and validation rules for business data. You edit a draft, validate it, and publish an immutable version.

**Records** are validated instances of a published type. Every change uses an expected revision, so conflicting updates fail instead of silently overwriting data.

**Operations** are named business actions with versioned input and output contracts. They are the boundary for rules that should not be bypassed by generic record updates.

**Workflows** compose operations with conditions, bounded loops, validation, and a typed result. Command workflows commit all business changes together or roll them all back.

## Choose a path

- [Run BOS Core locally](quickstart.md) and make your first authenticated request.
- Learn the [API conventions](api.md) before building a client.
- Define [types and records](records.md) for your own data.
- Compose operations with the [workflow engine](workflows.md).
- Follow the [Material Request tutorial](material-request.md) using the included example files.
- Prepare a [secure deployment](deployment.md) and tested [backup process](backup.md).

## What this preview includes

- Versioned type drafts and immutable published definitions
- Validated records, optimistic revisions, typed filtering, and pagination
- Human sessions, roles and grants, and scoped service tokens
- Registered business operations and restricted YAML workflows
- Offline diagnostics, backup, restore, and administrator recovery
- Authenticated OpenAPI and capability discovery

## Current boundaries

BOS Core is intentionally small. It does not provide multitenancy, background jobs, arbitrary code execution, workflow-to-workflow calls, or network access from workflows. It also does not automatically retry interrupted business work.

The preview has stronger Linux validation than Windows validation. Do not treat it as a certified production release or assume compatibility with future preview builds.
