---
name: human-permit-import-mapping-1106
description: "Human quiz 06.11 on permit row import mapping — shared mapping per permit type, Region/Area from NAE tree, .xlsx with sheet choice"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-06T10:36:59.370Z
---

Human 2026-11-06 (quiz via coordinator) for permit row import (Permit → Sources (NAE) → «Import file…»/«Paste from Excel…»):
- Q1 «Спільний»: saved column mapping is shared per permit type (server, cfg.RegistryImportProfile).
- Q2 «Так, з дерева NAE»: Region/Area missing in the file are filled from the NAE source's place in the NAE tree — only empty cells, marked in the preview.
- Q3 «Так»: «Import file…» reads .xlsx with a sheet choice (parsed on the server).
Also: lookups resolve by code OR name; unresolved values previewed; all-or-nothing rule (19.10) stays.

**Why:** the human's files differ in layout; NAE/LAND_REGION/AIR_AREA lookups could not be imported.
**How to apply:** don't re-ask these; lane lane/integ-permit-import-map. Related: [[human-quiz-answers-1104]].
