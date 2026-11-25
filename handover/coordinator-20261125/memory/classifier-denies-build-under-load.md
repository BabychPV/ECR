---
name: classifier-denies-build-under-load
description: "Класифікатор дозволів відхиляє dotnet restore/build агенту з «[Interfere With Workloads]», коли машина перевантажена; не обходити, повторити ту саму команду лише коли CPU < ~70 %"
metadata:
  node_type: memory
  type: feedback
  originSessionId: fbcb71e0-68fa-420a-b5a5-480d1ea09ee5
  modified: 2026-09-29T18:50:15.728Z
---

2026-09-29 агенту A5a (HSE301) класифікатор дозволів відхилив `dotnet restore Ecr.sln` у власному worktree з причиною «[Interfere With Workloads]». Ту саму команду інші агенти того дня виконували без проблем. На момент відмови машина була під 88–100 % CPU з 12–16 процесами .NET трьох сесій. Людина віддала рішення координатору. Координатор постановив: повторити ТУ САМУ команду тоді, коли звільниться слот і CPU впаде нижче ~70 %, а якщо знову відмова — стоп.

**Why:** перенести ту саму дію на іншого агента, в іншу сесію чи в іншу форму команди — це обхід рішення людини (див. [[cross-session-permission-laundering-blocked]]). Повтор за інших умов — це новий запит, який класифікатор оцінює сам.

**How to apply:**
- Перед тим як дати агенту слот, перевір навантаження: `(Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average` і кількість `dotnet`/`testhost`.
- Вище ~70 % — агент пише код без збірки й чекає.
- Відмову ескалювати координатору й людині, не обходити.
Пов'язане: [[machine-load-budget]], [[questions-go-to-coordinator]].
