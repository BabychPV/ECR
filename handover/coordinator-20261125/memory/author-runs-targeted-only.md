---
name: author-runs-targeted-only
description: "З 2026-09-29 автор lane-гілки локально ганяє лише збірку+Architecture+Application, Api/Infrastructure — вузьким фільтром; повний набір — CI гілки"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 4f26388c-991f-4666-b4f8-5a47388600fc
  modified: 2026-09-29T10:36:50.492Z
---

Локально автор lane-гілки: `dotnet build` + Architecture + легкі проєкти (Domain/Application). Ecr.Api.Tests і Ecr.Infrastructure.Tests — лише `--filter` на свої класи. Повний набір довіряти CI на lane/** (він там запускається). Не більше 1 важкого прогону на сесію; слот «Аудиту» має пріоритет.

**Why:** 2026-09-29 під час пачки 38 бігло ≥4 чужі прогони Api.Tests — у «Аудиту» таймаут SQL, Api 21–34 хв замість 13; гальмує зведення всім (рішення координатора).

**How to apply:** писати це правило в кожен промпт сабагента з важким прогоном; широкі фільтри (`~Security|~Role|…` на сотні тестів) не давати. Див. [[machine-load-budget]], [[full-suite-before-push]].
