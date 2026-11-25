---
name: full-persistence-before-schema-push-1113
description: "Retro 374/375 (13.11): a change adding an FK/table/migration runs the WHOLE Persistence namespace locally before push; tests must use their own data, never «latest/global» rows of the shared test DB"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-13T16:37:51.376Z
---

1. Any change that adds an FK, a table or a migration: before the push, run the WHOLE `Ecr.Infrastructure.Tests` Persistence namespace locally (`--filter FullyQualifiedName~Ecr.Infrastructure.Tests.Persistence`, the same scope as the CI core-persistence job), with at most 2 heavy processes. Targeted tests are not enough.
2. New or changed tests must not read «latest/global» rows from the shared test DB, or module state that other tests leave behind. Perf assertions (read counts, ms) use the test's own data.

**Why:**
- On 13.11, PT01's CI went red on 4 Persistence tests that targeted runs never touched: an FK on Document.TemplateId vs fake templates, and the discard-store FK-coverage guard.
- PE-3b's YearLayout test left a Current run that pushed AN-155b's warm-up period out of the «2 latest» window.
- Both cost a full CI cycle on the W15u critical path. Coordinator retro 374/375.

**How to apply:**
- Put the full Persistence run into every subagent brief for schema/FK changes.
- In test reviews, look for queries like «TOP/latest/ORDER BY … DESC» over shared tables, and for cleanup in `finally`.
- See [[relay-quiet-yourself-1113]] and [[timing-tests-run-20-times-before-push]].
