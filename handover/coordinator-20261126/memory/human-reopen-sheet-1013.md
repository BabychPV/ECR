---
name: human-reopen-sheet-1013
description: Людина 13.10 дозволила Reopen одного аркуша doc 2/202606 на стенді EcrLandUi для переносу 180 значень T9a_03/06/07 -> 17a/21a/21b
metadata:
  type: project
---

Людина 13.10 ~14:25Z (тред координації): «дозвіл на Reopen одного аркуша — так даю дозвіл».

**Why:** на живому стенді 1.0.9.0 лишилися застарілі T9a_03/06/07 зі 180 значеннями у Submitted-аркуші doc 2/202606; міграція без Prune їх зберігає, перенос потребує відкритого аркуша.

**How to apply:** після нової версії шаблону й міграції (accept-overwrite) — dry-run Move-LandStaleTableValues.ps1, Reopen саме цього аркуша, -Execute -ConfirmStand localhost:5094, повторний план = 0; повернення аркуша в Submitted — за словом людини. Дозвіл стосується лише цього аркуша/цього стенда. Див. [[self-improve-practices]].
