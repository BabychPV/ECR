---
name: fetch-doesnt-update-local-tree
description: "git fetch у H:\\ECR лишає робоче дерево на старому коміті — тест \"пройшов\" насправді проти застарілого коду"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 7a8e1359-5171-48b8-a08d-8b2542a7a9f1
  modified: 2026-09-25T12:04:13.382Z
---

Мало не відхилив реальний провал тесту як хибний: сабагент повідомив, що
`PeriodPolicyCrudTests.Без_права_Project_Manage_створення_відхиляється`
червоний на `dev/integration` (застарілий асерт після B-14 Security,
[[dedup-inflight-patch-race]]-сусід за часом, коміт `adfbcf9d`). Прогнав той
самий тест локально в `H:\ECR` — зелений — і мало не визнав звіт сабагента
помилковим.

Причина розбіжності: я робив лише `git fetch origin` (оновлює
`origin/dev/integration`), а НЕ `git merge`/`git pull` у сам `H:\ECR`.
Робоче дерево лишалось на старому коміті (за 5 комітів позаду), `dotnet
build`/`dotnet test` компілювали й ганяли СТАРИЙ код — тест логічно проходив,
бо саме змінене місце (`PermissionCheck.RequireAnyAsync`) ще не потрапило в
робоче дерево.

**Why:** `git fetch` НІКОЛИ не чіпає робоче дерево чи локальну гілку —
оновлює лише remote-tracking ref. Локальний `dev/integration` у `H:\ECR`
я особисто не пушу (це роблять сабагенти зі своїх worktree), тож він
відстає мовчки, доки хтось явно не зробить `git merge --ff-only
origin/dev/integration` (чи `git pull`).

**How to apply:** перед БУДЬ-ЯКОЮ особистою перевіркою (тест, білд, читання
файлу для рішення) в `H:\ECR` — спершу `git fetch origin`, тоді `git status`
(«behind N commits») і `git merge --ff-only origin/dev/integration`, і лише
тоді `dotnet build`/`dotnet test`/`Read`. Особливо коли перевіряєш ТВЕРДЖЕННЯ
сабагента про провал/успіх — саме тоді стара версія коду найдорожче коштує
([[verify-ci-not-local-subagent-claims.md]]: не довіряй числам сабагента
наосліп, але й не довіряй власній локальній перевірці, зробленій на
застарілому дереві).
