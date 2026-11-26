---
name: w15t-window-1113
description: W15t live window 13.11 — live now app-W15t e8a5c9ffc (116 migrations FE01); toolkit w15t-drafts fixes; recalc needs a full-read account
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-13T09:38:48.937Z
---

W15t DONE 13.11 11:26 local: live EcrLandUi = app-W15t main e8a5c9ffc (116 migrations, last FE01_FormulaError; UiString 22872), api/web PIDs in F:\ecr-backup\land-ui\land-ui.pids.json. K1 9D9AEBF3… unchanged. Backups E:\EcrBackup\land-ui\w15t\bak (after = …-after-W15t-20261113-1126.bak, 2843AA19…). Downtime ≈11.5 min (migration + CHECKDB + backup). Consistency on live 260 stale → 0.

Toolkit fixes in .sync-local\ready\w15t-drafts (carry into W15u): SeedReviewed (path→blob) for reviewed non-tuple seed files (99-epilogue); case-sensitive «: False» greps (appsettings "…": false tripped them); datasnap allows rpt.ViewSlot rows ADDED only when 05-rpt-views.sql is in the derived apply-sql set (`$W15t.SqlApply` is a list of hashtables — compare `.Name`); exit-7 text.

**Why:** the next window (W15u: HSE400 B61 steps 3–8 + B55) repeats this; my standing account gets 403 recalcNeedsFullRead on whole-doc recalc of doc 96 (a table is hidden from it) — the coordinator recalcs. B61 step 3 was blocked by yearlyProjectNeedsYearLayout (builder publishes Layout 0) — AN to fix (B71).
**How to apply:** clone w15t-drafts → w15u like before; rehearse on a fresh after-W15t restore into EcrScratch_W15j (E:); run live behind a CPU wrapper needing 2×30 s < 45 % and ask AN/PE for a quiet slot by letter; check C: ≥ 15 GB right before launch. Related: [[w15s-window-1112]], [[human-live-windows-autonomous-1029]], [[scratch-api-port-owner-check]].
