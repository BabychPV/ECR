---
name: never-build-in-h-ecr-1116
description: "H:\\ECR is a stale detached checkout with someone's old WIP — never build/test there; F13 scripts take an explicit absolute worktree path and refuse a missing project"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T18:47:34.581Z
---

Never run `dotnet build/test` (or npm) in H:\ECR. It is a stale detached checkout (#563, October) holding someone's uncommitted WIP (coordinator backed it up 16.11, PLAN B157). Use only worktrees (E:\ecr-wt\f13-*).

**Why:** 16.11 my pentest F13 script had `$p` (file path) overwrite `$P` (project table) — PowerShell variables are case-insensitive ([[powershell-case-insensitive-vars]]) — so `dotnet test` ran with no project in the cwd H:\ECR and built the stale tree; 22 «mutations» were void and H:\ECR bin/obj got touched.

**How to apply:** every mutation script: explicit absolute worktree path in each dotnet call; `if (-not (Test-Path $proj)) { refuse }` before `dotnet test`; never reuse a name that differs only by case; log `build OK` from the absence of `error CS` (rule 410, [[rule-410-mutation-counts-only-if-builds-1116]]).
