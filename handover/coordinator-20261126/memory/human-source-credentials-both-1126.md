---
name: human-source-credentials-both-1126
description: "Людина 26.11 — джерела PI SQL Client/Sql: потрібні ОБИДВА входи — Windows (Integrated) і логін+пароль; пароль не в рядку адреси, а окремим зашифрованим полем"
metadata:
  node_type: memory
  type: project
  originSessionId: d15bf7c2-34d1-400b-ad82-057f935d757c
  modified: 2026-11-26T07:42:37.490Z
---

Людина 26.11 (скрини New connection PiSqlClient і ODBC Login WIN-ABAANR1OQJE\Administrator): «мені також потрібно мати підключення по юзеру та паролю чи можу я ввести в строку?» → обрала «б» (поля в ECR) → «потрібно і вин та по ористувачу вхід».

**Why:** тестовий PI AF у робочій групі (не домен) — Integrated Security обліковкою служби ECR не дістає; у замовника може бути домен.

**How to apply:** картка `integ-source-credentials` (R3: DESIGN + огляд до брифу). Перемикач режиму Windows / User+Password; пароль write-only, зашифрований як SmtpSettings.PasswordProtected; `RequireNoCredentials` (заборона пароля в самій адресі, Q15-06) лишається. Наявні джерела після міграції = Windows. Тимчасово для тесту — `cmdkey` під обліковкою служби (людина вводить пароль сама). Див. [[human-pentest-b141-1116]].

✎ 26.11 пізніше: людина — «достатньо, що паролі джерел шифруються ключами»: сертифікат DataProtection на live не потрібен (ключі в sec.DataProtectionKey, AllowUnprotectedKeys=true), P-1126-DPKEYS.
