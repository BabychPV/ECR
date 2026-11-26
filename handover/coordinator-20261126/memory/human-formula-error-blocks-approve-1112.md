---
name: human-formula-error-blocks-approve-1112
description: "12.11 PO: a formula error blocks APPROVE as well as submit (audit C-1, D-475)"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-12T08:21:33.236Z
---

On 12.11 the human (PO) extended the audit C-1 decision. The 11.11 decision blocked only submit when a formula error exists. Now: «якщо є помилки то затвердити теж не можна».

**How to apply:** C-1 (AN, lane/integ-l6-formula) blocks both submit and approve while a formula error exists on the sheet; D-475 says so. F13 checks both paths. Related: [[human-audit-2611-on-hold-1111]].
