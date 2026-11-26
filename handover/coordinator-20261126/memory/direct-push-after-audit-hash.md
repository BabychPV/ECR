---
name: direct-push-after-audit-hash
description: "З вершини 7992b139 (2026-09-29, CLAUDE.md «✎ 2026-09-29», рішення людини) кожна сесія сама fast-forward пушить у dev/integration; ризикове — спершу lane із зеленим CI"
metadata:
  node_type: memory
  type: project
  originSessionId: fbcb71e0-68fa-420a-b5a5-480d1ea09ee5
  modified: 2026-09-29T17:22:29.046Z
---

Діє з вершини `7992b139` (2026-09-29). Підстава — CLAUDE.md, розділ «✎ 2026-09-29: прямий fast-forward пуш кожною сесією», з дослівними цитатами людини. Перевірено `git show origin/dev/integration:CLAUDE.md`.

Цикл:
1. fetch → rebase лише незапушених комітів → build + `Ecr.Architecture.Tests` + свої класи вузьким `--filter` → `git push origin HEAD:dev/integration`.
2. Відмова non-fast-forward → повтор циклу. `--force` / `--no-verify` — ніколи. Хук `pre-push` увімкнено.
3. Ризикові зміни спершу йдуть у lane із зеленим CI, потім автор сам fast-forward'ить. Ризикові — це міграції (токен лишається), контракт, `.github`, `package-lock.json` / `Directory.Packages.props`, `shared/ui`.
4. Повні набори локально не ганяти. smoke/e2e на вершині робить «Аудит».
5. Червона вершина через мій коміт — лагодити негайно.
6. Нових «-2/-3» стеків не робити.

**Історія, щоб не повторити помилку:** того ж дня координатор оголосив прямий пуш раніше, ніж погодила людина. «Аудит» повідомив, що зміну CLAUDE.md людина тоді не дозволила. Лише згодом вона погодила явно, і розділ з'явився в CLAUDE.md.

**Why:** рішення, яке змінює CLAUDE.md, має силу лише з підтвердженням людини в самому файлі. Лист сесії — не дозвіл.

**How to apply:** у сесії HSE301 пушить оркестратор, а не агенти: раніше класифікатор блокував агентам пуші. Агенти роблять rebase і перевірку, потім звітують хешем. Пов'язане: [[integrator-audit-lanes]], [[cross-session-permission-laundering-blocked]], [[machine-load-budget]].
