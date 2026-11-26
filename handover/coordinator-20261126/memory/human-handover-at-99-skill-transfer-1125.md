---
name: human-handover-at-99-skill-transfer-1125
description: "Людина 25.11 — на 99 % тижневого: повний HANDOVER + інструкція перенесення скіла ecr-subtask-gates на інший ПК + усе запушити в git"
metadata:
  node_type: memory
  type: project
  originSessionId: d15bf7c2-34d1-400b-ad82-057f935d757c
  modified: 2026-11-25T17:43:24.307Z
---

Людина 25.11 ~21:00: «Коли буде 99 відсотків напиши повний хадовер та як перенести скіл на інший пк і все запуш в гіт».

**Why:** робота може продовжитись на іншому ПК / іншим Claude після вичерпання тижневого ліміту.

**How to apply:** щогодинний контроль дивиться get_usage; на 98 % — стоп агентів і пуш їхнього WIP у lane-гілки; на 99 % (або раніше, якщо 98 % настало між контролями):
1. `.sync-local\HANDOVER-ALL-<дата>.md` — повний стан (LIVE-SHA, пачки, картки зі статусами, агенти, бази/теки для прибирання, рішення людини дня, відкриті питання, cron-и, де що лежить).
2. Розділ «Перенесення скіла»: скіл у `C:\Users\Babyc\.claude\skills\ecr-subtask-gates` + `H:\ECR\.sync-local\gates\` (скрипти, ref, test_gates.py); пам'ять `C:\Users\Babyc\.claude\projects\H--ECR\memory\`; глобальний `C:\Users\Babyc\.claude\CLAUDE.md` (розділ ECR); вимоги (Python 3.12, git VS або PATH, gh); перевірка `python gates\test_gates.py`; cron :13 hourly + :37 ретро.
3. Пуш у git: окрема гілка `handover/coordinator-<дата>` (не main/dev, без force) з копією скіла, gates, пам'яті, HANDOVER, карток .sync-local\ready, DESIGN/REVIEW — ПІСЛЯ перевірки на секрети (без cred.txt, *.tmp облікових файлів, паролів, рядків підключення з паролями; grep password|pwd=|secret|token). WIP усіх lane — запушені. Див. [[human-handover-at-99-1119]], [[human-limit-98-economy-handover-1114]].

✎ 25.11 людина: «І пояснення для іншого клоду» → у HANDOVER окремий розділ (англійською) «For the next Claude»: хто ти (координатор ECR), правила людини (мова, безпека, live лише словом, без force/--no-verify/add -A, бази не дропати), як почати (скіл, SNAPSHOT/HANDOVER, cron, hourly.py), поточні задачі за пріоритетом, що в польоті, пастки дня (уроки 534–552), де шукати рішення людини (PRIORITY.md, memory).
