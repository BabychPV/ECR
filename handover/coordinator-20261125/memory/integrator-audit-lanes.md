---
name: integrator-audit-lanes
description: З 2026-09-27 лише сесія «Аудит» пушить у dev/integration; інші пушать lane/<сесія>/<задача>
metadata:
  node_type: memory
  type: project
  originSessionId: cb9a6802-7e74-4b2a-a2e8-5d1277869b04
  modified: 2026-09-28T14:39:05.995Z
---

2026-09-27 людина погодила план координації (`H:\ECR\.sync-local\COORDINATION-PLAN.md`):
єдиний інтегратор — сесія «Аудит проекту та вимог». «Аналіз» і HSE301 пушать лише
гілки `lane/<сесія>/<задача>` від `origin/dev/integration` і пишуть «Аудиту» хеш;
«Аудит» зводить і пушить одним пушем (~раз на 45 хв), розсилає вердикт 7 гейтів.
Зони: HSE301 — вирази/одиниці/PI SQL-розрахунки; «Аналіз» — PI-збір/джерела/покриття;
«Аудит» — DIRECTIVE-16 + CLAUDE.md/.github/lock-файли. Токен міграцій передається листом.

**Why:** `ci.yml` має `cancel-in-progress` на PR #463 — кожен push скасовував прогін,
сесії жили «тихими вікнами». Один пушер це знімає.

**How to apply:** не пушити в dev/integration з інших сесій; CLAUDE.md розділ 2026-09-20
(«кожна сесія пушить») ще не виправлено — див. [[merge-cadence]], [[push-batching-ci-cancellation]].
2026-09-28: четверта сесія «Безпека і шлях запису» (local_09630f3f-9e02-4099-bd72-73ddc636f512, бриф .sync-local/BRIEF-SESSION-SECURITY.md) забрала в «Аудиту» зону безпеки/шляху запису; «Аудит» — інтегратор + CI/.github/CLAUDE.md. CI вмикається на lane/** (рішення людини 28.09).
CI: вузьке місце `server` 1.5–15.7 хв (медіана ~9), решта ≤ 3.5 хв.
