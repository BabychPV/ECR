---
name: gate-policy-change-grep-tests-by-error-code
description: "Зміна політики/воріт валідації (код помилки) = git grep тестів і доків за кодом ДО пушу, не лише власні класи; Api.Tests теж ганяти"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-10-13T13:36:48.563Z
---

13.10 (WW-02, «С» RC37): я послабила ворота ECR-TMPL-4227 (лише Scope=Column; Cell на вхідній колонці → 200) і прогнала лише Application/Infrastructure/Expressions/Architecture. CI api-2 впав детерміновано на `CellFormulaAuthoringApiTests…неіснуюча_адреса_дає_404` (очікував 422/4227, отримав 200). Координатор: «зміна політики/воріт валідації = grep тестів за кодом помилки ДО пушу, не лише власні класи».

**Why:** повні набори ганяє CI, а локально я обирала «свої» класи — тест в іншому проєкті (Ecr.Api.Tests), що перевіряв стару політику, пройшов повз. Це та сама пастка, що в [[full-suite-before-push]].

**How to apply:** перед пушем будь-якої зміни воріт/політики/коду помилки зробити `git grep -n -E "<код>|<messageKey>|<ім'я прапора>" tests/ docs/ src/Ecr.Web` по ВСІХ тестових проєктах (Api, Infrastructure, Application, Adapters, Architecture, клієнт) і вузько прогнати знайдені класи з ECR_TEST_SQL; у промпті агенту-виконавцю прямо вимагати цей grep і перелік у листі готовності. Docs під checksum (docs/tz/**) не правити — лише перелічити.
