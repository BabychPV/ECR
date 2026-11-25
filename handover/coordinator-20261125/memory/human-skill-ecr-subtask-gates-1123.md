---
name: human-skill-ecr-subtask-gates-1123
description: "⛔ Людина 23.11 — скіл ecr-subtask-gates v3 (.sync-local\\gates) керує всім циклом розробки; використовувати завжди, без нагадувань"
metadata:
  node_type: memory
  type: feedback
  originSessionId: d15bf7c2-34d1-400b-ad82-057f935d757c
  modified: 2026-11-23T18:49:16.803Z
---

23.11 ~20:20 людина: «мені потрібний повноцінний скіл яки закриє всі розробку». Скіл встановлено в `~\.claude\skills\ecr-subtask-gates`. Джерело — `H:\ECR\.sync-local\gates\`: SKILL.md, ref\01…07, картки, board.py, where_is.py, overlap.py, wt_audit.py, route.py.

**Why:** двічі за день я сказав «працює» / «на стенді» без доказу (NAE 16:20, B74 19:25); листи готовності занедбано.

**How to apply:**
- Використовувати автоматично на кожному етапі E0–E10, людина не має його називати.
- Python для скриптів: `%LOCALAPPDATA%\Programs\Python\Python312\python.exe -X utf8`; змінна `$env:GIT` — git із VS.
- Перед словами «готово» / «на стенді» / «працює» — `where_is.py`.
- Перед агентом або важким прогоном — `route.py`.
- Щогодини — `board.py` і `wt_audit.py`.
- Картки — `.sync-local\ready\integ-*.md`.
- Пов'язано: [[human-work-rules-tokens-models-batches-stand-1122]], [[self-improve-practices]].
