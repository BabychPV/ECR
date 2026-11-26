---
name: direct-push-since-0929
description: З 2026-09-29 (7992b139) кожна сесія сама fast-forward пушить у dev/integration; lane лише для ризикових змін
metadata:
  node_type: memory
  type: project
  originSessionId: 4f26388c-991f-4666-b4f8-5a47388600fc
  modified: 2026-09-29T17:21:36.994Z
---

З вершини 7992b139 (2026-09-29, людина погодила 16:05Z/16:16Z, CLAUDE.md «✎ 2026-09-29») кожна сесія пушить сама: fetch → rebase незапушених на origin/dev/integration → dotnet build + Ecr.Architecture.Tests + свої класи вузьким --filter (клієнт — vitest своїх файлів) → `git push origin HEAD:dev/integration`; non-ff → повтор; force/no-verify ніколи.

Ризикові (міграції, контракт openapi/schema.d.ts, .github, lock-файли, shared/ui) — спершу lane із зеленим CI, потім fast-forward сама. Повні набори/smoke/e2e локально не ганяти; smoke/e2e на вершині ганяє «Аудит». Червона вершина через мій коміт — лагоджу негайно (наступний коміт або revert). Два пуші, що правлять tools/*.ps1 (стенди), — по одному, з повідомленням «Аудиту» для smoke/e2e.

**Why:** 86 гілок і ~2 год від «готово» до вершини через єдиного інтегратора.
**How to apply:** замінює [[lane-branches-must-be-linear]] і [[integrator-audit-lanes]] для неризикових змін; див. [[author-runs-targeted-only]].
