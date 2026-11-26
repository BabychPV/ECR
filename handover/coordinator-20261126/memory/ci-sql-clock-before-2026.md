---
name: ci-sql-clock-before-2026
description: ADD PERIOD FOR SYSTEM_TIME на таблиці з рядками падає Msg 13542 («start of period in the future»); у CI на Linux майже завжди. Лік — DEFAULT з запасом −1 с
metadata:
  node_type: memory
  type: project
  originSessionId: fbcb71e0-68fa-420a-b5a5-480d1ea09ee5
  modified: 2026-09-28T14:26:24.299Z
---

2026-09-28 міграція RK03 (RT-04: temporal-таблиці `dic.RegistryEntry` / `dic.RegistryValue`) падала в CI у тесті `D148ScalePrecheckTests` — єдиному, що доводить до RK03 базу з рядками. Повідомлення: `ADD PERIOD … open records with start of period set to a value in the future` (Msg 13542). Три червоні поспіль на Linux-контейнері CI, локально на Windows — 0 з 2000.

**Корінь (підтверджено зондом):** RK03 одним ALTER додає `PeriodStart … DEFAULT SYSUTCDATETIME(), PERIOD FOR SYSTEM_TIME`. Перевірка ADD PERIOD порівнює з годинником у мить САМОЇ перевірки, а не в мить обчислення DEFAULT, до того ж datetime2(7) округлюється до (3) вгору. Швидкий Linux встигає до перевірки й падає майже завжди, Windows — зрідка.

**Фікс:** `da8ab49c` — DEFAULT `DATEADD(second, -1, SYSUTCDATETIME())` для наявних рядків. Після нього CI 7/7 з першого прогону (run 36434797054). Доказ — `RK03PeriodStartMarginTests`: SQL з `IMigrator.GenerateScript`, годинник у DEFAULT детерміновано зсунуто на +500 мс, старе тіло дає 13542.

Перша гіпотеза («годинник CI раніший за 2026») була хибною: дата-заповнювач у тесті ні до чого.

**How to apply:** будь-яке додавання системного версіювання до таблиці з даними — початок періоду з запасом у минуле. Падіння «лише в CI, на Linux» спершу перевір на час перевірки проти часу обчислення, а не на регресію коду. Пов'язане: [[verify-ci-not-local-subagent-claims]].
