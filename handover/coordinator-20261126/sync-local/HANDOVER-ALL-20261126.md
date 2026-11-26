# HANDOVER — координатор ECR, 26.11.2026 ~13:30 (Київ)

Замінює `HANDOVER-ALL-20261125.md` (розділи 6 і правила з нього чинні; тут — новий стан). Тижневий ліміт 98 %, скидання 29.11 17:00 Київ. Людина: «99% стеля продовжи».

## 1. Стан live (EcrLandUi, API :5094, web :6094)
- **LIVE-SHA 4980a1837** (пачка b16 = `lane/integ-audit-filters-contract`, включає `lane/integ-audit-filters-followup` 973c9c6da і b15 6260abdb8). App `F:\ecr-backup\land-ui\app-b16`, API PID 17852, web PID 38160, старт detached (WMI).
- Набір вікна `E:\EcrBackup\land-ui\b16`: `launch-live-b16.ps1 -ExpectHash 4980a1837a1b536b46f5e48b24ec6752754fc4d4`, `start-live-b16.ps1`, `stop-live.ps1 -ExpectApp app-b16`, `new-live-temp-account.ps1`, `Start-LiveDetached.ps1`.
- Вікно b16 26.11 12:25: бекап `E:\EcrBackup\land-ui\b16\bak\EcrLandUi-before-b16-*.bak` (VERIFYONLY ok, sha256), міграцій немає, лічильники до/після 0 різниць, простій ~22 с. Журнал: `.sync-local\WINDOW-b16.md`. UI-прохід PASS (вхід людини land-owner, перевіряв координатор у браузері).
- Попередні вікна сьогодні: b15 6260abdb8 (09:12, аудит: тайм-аут 503 doc 96 → ~0,7–1,4 с). `WINDOW-b15.md`.
- Перезапуск live після збою ПК — лише за словом людини: `powershell -File E:\EcrBackup\land-ui\b16\launch-live-b16.ps1 -ExpectHash 4980a1837a1b536b46f5e48b24ec6752754fc4d4`.
- Тимчасові акаунти f13-* на live — усі IsActive=0 (останній f13-b16-sa).

## 2. Картки — стан (`.sync-local\ready\integ-*.md`)
**live-verified 26.11:** audit-filters-live (b15), audit-filters-followup + audit-filters-contract (b16: Document першим, назва замість #id, «Row N», логін у By user лише при збігу імен, колонки з сервера), overhauled-default (код з b13, live-крок у b16).

**Черга після 29.11 17:00 (пріоритет):**
1. **integ-source-credentials** (R3) — вхід до PI SQL Client / SQL логіном і паролем + Windows. `DESIGN-source-credentials.md` rev 2 після незалежного огляду `REVIEW-source-credentials.md` (24/24 знахідки закрито в §12), AC-0..AC-15 у §14, поділ на 4 порції у §15 (foundation міграція+контракт → сервер A → сервер B → клієнт, ~2100 рядків). Рішення людини: обидва режими; ключі DataProtection без сертифіката — достатньо (P-1126-DPKEYS); поріг блокування AF «десь 20 спроб» (P-1126-AFLOCK); ключі ODBC — агент визначає на AC-0. Наступний крок: бриф opus-агенту від LIVE-SHA, лейн `lane/integ-source-credentials`, репетиція на копії, live не чіпати.
2. **integ-document-move** (R3, DESIGN rev 2 + REVIEW B1–B6).
3. **integ-land-stationary-arg** (#ARG doc 96 Stationary, січ–сер).
4. **integ-sg-flaring-composition**.
5. Нові 26.11: **integ-row-label-messages** (Issues doc 100 показує сирий GUID рядка «Row adc95dc2…»), **integ-audit-column-lineage** (Column: 19 із 103 назв двічі після переносу проєкту).
6. Решта planned/briefed/in-progress — `Get-ChildItem .sync-local\ready\integ-*.md` + «Статус:».

## 3. Агенти / сесії
- Агентів у роботі немає. Гілки запушено: `lane/integ-audit-filters-followup` (973c9c6da), `lane/integ-audit-filters-contract` (4980a1837) — обидві на live, у main ще ні (наступний RC).
- Cron у сесії: :13 hourly, :37 ретро (у новій сесії — CronCreate заново, §6 старого HANDOVER).

## 4. Прибрати (лише словом людини / білим списком; ECR, EcrLandUi, FLERT, EcrLegacyProd — НІКОЛИ)
- Бази: EcrLandB13Reh, EcrLandB14Reh (у ній акаунт test-audit-perf — для репетицій аудиту), EcrLandB14Flert, EcrReqBanner, EcrE2EReqBanner.
- Теки: C:\ecr-wt\b13-bld, b14-bld, b15-bld, b16-bld, b13-reh, b14-docui, b14-numfmt, b14-flert, audit-filters, audit-followup, audit-contract (worktree влитих на live гілок — після main), handover; G:\EcrData\b13reh, b14reh, b14flert. H:\ECR-worktrees\docs, task (не git-теки).
- Диск F: 9 ГБ вільно — нових агентів/стендів на F: не ставити.

## 5. Рішення людини 26.11 (PRIORITY.md P-1126-*, пам'ять human-source-credentials-both-1126)
- Джерела PI SQL Client / SQL: обидва входи — Windows і логін+пароль (поля в ECR, пароль зашифрований, не в адресі).
- Аудит: Document — у рядок фільтрів першим («перенеси це поля сюди це логічно»); без #id після вибору; три доробки API — «зроби зараз» (зроблено, b16).
- Ключі шифрування: «достатньо, що паролі джерел шифруються ключами». Поріг блокування AF ~20.
- Стеля 99 % тижневого — тоді стоп агентів, пуш, цей HANDOVER.
- Уроки 560–574 у `memory\self-improve-practices.md` (нові: 569 gh ≤1/10 хв у брифах; 570 червоне лише з завершеного run; 571 відкладений AC — окрема картка; 572 UPDATE на live лише sqlcmd -I -b з SELECT-перевіркою, висновок «0» з API — звіряти з сирою відповіддю; 573 набір вікна містить усі скрипти; 574 виправлення класу відображення → пошук того ж класу на інших екранах).

## 6. Перенесення скіла — див. `HANDOVER-ALL-20261125.md` §6 (без змін). Копія стану цього дня — гілка `handover/coordinator-20261125`, тека `handover/coordinator-20261126/`.

## 7. For the next Claude (English) — delta to the 25.11 handover
- Live is **b16 = 4980a1837**. All 26.11 audit work is live-verified. No agents running.
- **First task after the weekly reset:** brief an opus agent for `integ-source-credentials` from `DESIGN-source-credentials.md` rev 2 (§14 ACs, §15 split; foundation commit first). It is R3: migration + contract + a secret in the DB. Rehearse on a copy (EcrLandB14Reh), never on live; the live window only on the human's word, with AC-0 done by the human (they type the PI password themselves).
- In briefs: limit `gh` to one call per 10 min, never `gh run watch` (we exhausted the GitHub API quota on 26.11).
- On live, any `sqlcmd` UPDATE needs `-I -b` plus a SELECT check in the same call (lesson 572).
- Rules and traps from the 25.11 handover §7 still apply.
