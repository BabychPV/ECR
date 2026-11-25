---
name: live-restart-w15u-hse400-flag-1114
description: "З W15u перезапуск live — лише restart-w15u.ps1 -Target W15u з $env:W15U_HSE400_FLAG='1'; інакше Land втрачає склад HSE400"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-14T03:32:07.779Z
---

Після вікна W15u (13.11 21:57Z) live = `F:\ecr-backup\land-ui\app-w15u` (a1ff81973).

Прапорець `Calculations:Hse400CompositionFromDocument=true` задано ЛИШЕ змінною оточення: збірка тримає appsettings = false.

**Правило перезапуску:** якщо live впав поза вікном, перезапускаю той самий білд скриптом `.sync-local\ready\w15u-drafts\restart-w15u.ps1 -Target W15u`, попередньо виставивши `$env:W15U_HSE400_FLAG = '1'`. Старий `restart-w15q.ps1` для live більше не годиться: з ним прапорець зникне, і розрахунки Land молча втратять склад газу з документа HSE400.

**Why:** AU позначив це в журналі вікна (`.sync-local\reports\W15u-window.md`, рядок 40) і у своїй пам'яті `w15u-toolkit-lessons-1113`.

✎ 14.11 03:45Z: AU додав постійний файл `F:\ecr-backup\land-ui\live-env.json` з прапорцем HSE400=true. `restart-w15u.ps1` читає його при кожному старті live, у тому числі під час відкату. Ручний `dotnet` старт live прапорець усе одно втрачає, тому перезапуск — лише скриптом.

**How to apply:** після перезапуску перевірити `launch-flags.json` поруч із застосунком: `hse400CompositionFromDocument=true`. Борг: зробити прапорець постійним (PLAN B105).

Пов'язано: [[w15u-window-1113]].

✎ 14.11 11:05Z: після W15v live = app-w15v (363dc0baa). Перезапуск — лише через інструментарій поточного вікна: .sync-local\ready\w15v-drafts\restart-w15v.ps1. Точну команду для звичайного рестарту (не вікна) питаю в AU. Скрипт має читати live-env.json.

✎ 16.11: live лежав 04:12Z–06:17Z — був дочірнім процесом shell сесії, перезапуск Claude Desktop убив дерево. AU підняла той самий білд ВІДВ'ЯЗАНО (WMI Win32_Process Create) і додала -CrashRestart у restart-w15v.ps1 (рядок задачі Running після kill не блокує старт). Правило: кожен старт live (вікно чи рестарт) — лише через відв'язану обгортку; після рестарту Claude Desktop одразу перевіряти live.
