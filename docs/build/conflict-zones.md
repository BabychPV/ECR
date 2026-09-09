# Карта конфліктних зон

Координація файлів між паралельними задачами (аудит фази 2, фікси фази 3),
щоб два незалежні агенти не правили один і той самий файл одночасно.

⚠ Це **не** механізм зон, що раніше керував правами PK1/PK2 (прибрано
`PR #69`) — тут немає заборон, лише мапа «цей файл уже зачіпає відкритий
`Q-`», якою маршрутизація фази 2.5 користується автоматично: якщо знахідка
аудиту лежить у зоні нижче, вона не заводить окремий запис, а стає
додатковим критерієм приймання для вже відкритого `Q-`.

Оновлюється при відкритті чи закритті будь-якого запису тут перерахованого.

## Активні зони (відкриті `Q-`, `questions.md`)

| Q- | Файли | Тип |
|---|---|---|
| `Q-145` | `tools/br07-load-test.ps1`, `tools/Ecr.DataGen/GateBenchmark.cs` | SCOPE — чекає стенду |
| `Q-147` | `docs/build/roadmap.md` (таблиця «Етап II» проти діаграми) | SCOPE — облік кроків |
| `Q-148` | `src/Ecr.Application/Documents/PatchCellsHandler.cs`, `CreateRowHandler.cs` | SCOPE — рішення про `Fixed`-таблиці |
| `Q-149` | `src/Ecr.Application/Documents/PatchCellsHandler.cs` | SCOPE — канал сповіщення про провал перерахунку |
| `Q-151` | `src/Ecr.Application/Calculations/RunCalculationHandler.cs` | SCOPE — обсяг перерахунку проєкту |
| `Q-153` | `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` (`sec.Permission`, `sec.RolePermission`) | SCOPE — хто отримує `Report.EditDefinition` |
| `Q-154` | `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` (`rpt.ReportDef`) | SCOPE — склад колонок 6 державних форм |
| `Q-155` | `src/Ecr.Application/Workflow/SubmitSheetHandler.cs` | SCOPE — версії методологій у зрізі подання |
| `Q-156` | `src/Ecr.Application/Integration/IntegrationHandlers.cs` (`GetJobStatusHandler`) | SCOPE — право бачити свою фонову задачу |
| `Q-160` | `src/Ecr.Application/Recalculation/RecalculationService.cs`, `src/Ecr.Infrastructure/Jobs/FormulaRecalculationJob.cs`, `src/Ecr.Infrastructure/Jobs/RecalculationJob.cs` | SCOPE — явний маршрут «перерахувати формули шаблону» |
| `Q-162` | `src/Ecr.Infrastructure/Jobs/RecalculationJob.cs` (`RecalculationRequest.DocumentId`) | SCOPE — сенс `DocumentId = 0` |

⚠ **Перетин:** `Q-153` і `Q-154` зачіпають ОДИН файл
(`09-seed.sql`) — окремі блоки (`sec.RolePermission` проти
`rpt.ReportDef`), але правка одного не повинна ламати роботу над іншим
у паралельному PR.

⚠ **Перетин:** `Q-160` і `Q-162` зачіпають ОДИН файл
(`RecalculationJob.cs`) з різних причин (маршрут формул шаблону проти
семантики `DocumentId = 0`) — та сама обережність.

## Зони фази 1 (у роботі)

`Q-157` закрито (`PR #71`) — фаза 1 наразі без активних задач; решта
відкритих `Q-` вище — задокументована прогалина (`SCOPE`), не робота в
процесі.

## Як користуватись (фаза 2.5, маршрутизація)

1. Знахідка аудиту лежить у файлі з таблиці вище → не заводити нову
   знахідку окремо, а дописати її як додатковий критерій приймання до
   зазначеного `Q-` (у `questions.md`, секція «Що потребує людини»/деталі).
2. Знахідка лежить поза цими файлами → звичайний шлях воронки
   (дедуплікація → ре-валідація на `audit/baseline` → фаза 3).
3. Закрили `Q-` чи задачу фази 1 — приберіть рядок звідси в тому ж PR, що
   закриває запис.
