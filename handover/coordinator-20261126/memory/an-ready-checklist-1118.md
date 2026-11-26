---
name: an-ready-checklist-1118
description: "AN's pre-READY client checklist after batch-1118-2 went red twice: full vitest, test:a11y, catalogue-count guards"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-18T03:36:42.133Z
---

Before READY on ANY lane (server-only included), at the tip:
1. Full `npx vitest run` (not only touched files). Rule 405.
2. `npm run test:a11y` (vitest.a11y.config.ts) for touched folders. `*.a11y.test.tsx` run ONLY under that config, so plain vitest silently skips them.
3. A new permission or seed key means grepping the client guards that count the seed catalogue (e.g. permissionLabel.test.ts `codes.size`).
4. Client change: `npm run build` + `node scripts/check-bundle-budget.mjs` (P-0118-7).

Also: Mantine `Alert` always renders role="alert" and overrides a passed role, so never assert `[role="status"]` on it.
Source-scan guards (noMojibake, noDisabledTests) time out on disk F: under load. Rerun them alone with `--testTimeout=180000` before calling it a failure.

**Why:** 18.11 batch-1118-2 was red twice from AN lanes. First, B80 N1 added a 50th permission; the client guard still said 49 and was never run. Second, the L7c banner's a11y test asserted role=status and was never run locally.

**How to apply:** treat this as part of the READY template, next to «Architecture on <SHA>» ([[aa-rules-424-425-1117]], [[rules-405-406-one-sha-own-architecture-run-1116]]).
