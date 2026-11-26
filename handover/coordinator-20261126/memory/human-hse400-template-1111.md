---
name: human-hse400-template-1111
description: "11.11: human asked to build the HSE400 template (input, from HSE400_v1.0.1.0 2.xlsm) and the document (result like HSE400.Year.xlsx) in ECR; brief saved in .sync-local\\reports\\HSE400-human-brief-1111.md"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-11T05:06:10.891Z
---

On 11.11 the human gave two files in Downloads: «HSE400_v1.0.1.0 2.xlsm» (input template: gas composition, 6 gas types, 12 month columns) and «HSE400.Year.xlsx» (report export with input + calculated values). They asked: create the template and the document itself. They also gave a long description of the legacy HSE400 chain (AF EF → SQL → CLR calc → CalcResult → consumers: flaring, thermal oxidizer, 11 Land methodologies); saved verbatim in `.sync-local\reports\HSE400-human-brief-1111.md`.

**Why:** HSE400 feeds composition/density/sulphur/LHV to gas calculations; ECR today only has the imported registry LAND_HSE400_COMPOSITION.

**How to apply:** AN does facts+plan first (no code), I show the plan to the human before building; acceptance = 2026 recomputed from xlsm inputs equals HSE400.Year.xlsx. Both files are read-only, scratch copies only, never committed. Related: [[human-hse401-template-1104]], [[human-budget-full-then-moderate-1108]].

**Quiz 11.11 ~05:40Z:** plan approved (Yearly project, 1 doc/year, template 1.0.1.0, 6 tables, 12 months, calculated rows, AF formulas, same roles). Human on the registry: «загалом навіщо довідник ми маємо брати дані з 400 для інших методологій з самого документу!» — Land methodologies read composition directly from the HSE400 document; no projection into LAND_HSE400_COMPOSITION (registry becomes read-only/deprecated, not deleted).

**11.11 ~09:40Z:** pentanes FG_Export «Як у старій системі» (keep legacy cross-mapping). Human: component composition must come from DB FLERT (added locally, localhost, EXEC dbo.getFlaringGasCompositions; 523 streams, summer/winter / SOR/EOR variants; fuel gases «No composition» → HSE400). Variant choice «як у старій системі». FLERT read-only.
