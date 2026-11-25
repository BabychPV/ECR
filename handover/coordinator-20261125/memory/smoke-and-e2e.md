---
name: smoke-and-e2e
description: "Як запускати tools/smoke.ps1 на цій машині і чому це найцінніша перевірка проєкту"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-18T09:42:24.101Z
---

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 -Server localhost -Database EcrSmokeX -Port 5098
```

⚠ `-Server localhost` **обов'язково**: дефолт скрипта — `localhost\SQLEXPRESS`,
а тут стоїть default instance (`MSSQLSERVER`). Без цього падає на кроці 1.
`-Database`/`-Port` задавай різні, якщо женеш кілька worktree паралельно.
Повний прогін — близько 3–4 хвилин, 22 кроки.

⚠ .NET 10 має бути в `PATH` ДЛЯ САМОГО СКРИПТА (він кличе `dotnet` усередині),
тобто `$env:PATH` треба доповнити перед викликом, а не покладатись на повний
шлях (див. [[toolchain-paths]]).

**Чому це найцінніша перевірка.** 2026-09-17 вона знайшла дефект, якого не
побачив ЖОДЕН із 1800+ тестів і жоден із семи гейтів: ідентифікатор фонової
задачі містив `#`, а той в URL починає фрагмент, тож `GET /api/v1/jobs/{id}`
давав 404. Клієнт застосунку рятував себе `encodeURIComponent`, тому з
інтерфейсу дефект був невидимий. Скрипт ходить тим самим шляхом, що й
зовнішній споживач, — без клієнтських обгорток.

Мораль на майбутнє: **перед тим, як оголосити дефект «не живим», проженіть
smoke** — я вже один раз помилково закрив саме цей дефект на підставі того,
що клієнт усе кодує.

`tools/e2e-stand.ps1` — друга така перевірка. 2026-09-18 запущена вперше й
доведена до 21/21; потребує Node ≥ 20, `npm ci` у `src/Ecr.Web` і
`npx playwright install chromium`.

⛔ **Стенди запускай із ОКРЕМОГО worktree.** Бази розведені
(`WorkspaceTag()`), а папка збірки — ні: піднятий `Ecr.Api` тримає DLL, і
`dotnet build` падає з `The file is locked by: "Ecr.Api (<pid>)"`. Скрипт
рапортує «крок 1 — розгортання не пройшло», що читається як зламаний `main`,
хоча код цілий. 2026-09-18 я на це спіткнувся двічі поспіль.

⚠ Перш ніж убивати чужий `Ecr.Api` — подивись, хто його підняв
(`Get-CimInstance Win32_Process -Filter "Name='Ecr.Api.exe'"`, поле
`CreationDate`): паралельна сесія може тримати власний стенд.
