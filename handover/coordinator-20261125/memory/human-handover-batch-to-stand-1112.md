---
name: human-handover-batch-to-stand-1112
description: "12.11 human: all changes that came with the handover (audit 2026-11 lines) go to the stand (live) as one batch; I then check first-hand that everything works and the UI is convenient"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-12T05:11:36.933Z
---

Human, 12.11: «все нові зміни які прийшли з хандовером мають попасти на стенд пачкою, яку ти потім сам перевіриш від першого обличчя, що все працювало та UI був зручним».

**Why:** the audit fixes touch many areas (permits, migration, formulas, import, export, UI); merged PRs alone don't prove the product works for a user.

**How to apply:**
- Merged audit lines are not deployed one by one. Collect them into ONE live window (after W15r), when a coherent set has merged (at least wave 1).
- After AU's window DONE, I do a first-person live UI pass. Each merged finding gets a scenario: what the user does and what they must see. Check usability too (clarity, extra clicks, messages, both themes), not only correctness. Take screenshots and write a report. P0/P1 → fix-forward lane; usability issues → queue rows.
- Track the batch list in `.sync-local\reports\AUDIT-batch-stand.md` (finding, PR, merged SHA, scenario, result).

Related: [[human-audit-2611-on-hold-1111]], [[coordinator-is-final-tester]].
