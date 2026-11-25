# HANDOVER — координатор ECR, 25.11.2026 ~23:45 (Київ)

Причина: тижневий ліміт 99 % (скидання 29.11 17:00 Київ). Слово людини: «Коли буде 99 відсотків напиши повний хадовер та як перенести скіл на інший пк і все запуш в гіт» + «І пояснення для іншого клоду».

## 1. Стан live (EcrLandUi, API :5094, web :6094)
- **LIVE-SHA 7afb19c97** (пачка lane/integ-b14), app `F:\ecr-backup\land-ui\app-b14`, API PID 22168, web PID 10620, старт detached через WMI (набір вікна `E:\EcrBackup\land-ui\b14`: `launch-live-b14.ps1 -ExpectHash`, `start-live-b14.ps1`, `stop-live.ps1 -ExpectApp app-b14`, `new-live-temp-account.ps1`).
- Вікно b14 25.11 22:51–22:58: бекап `E:\EcrBackup\land-ui\b14\bak\EcrLandUi-before-b14-20261125-2251.bak` (VERIFYONLY ok, sha256 поруч), idempotent ×2 = 0 змін, лічильники до/після 0 різниць, простій ~2 хв. Журнал: `.sync-local\WINDOW-b14.md`.
- UI-прохід b14 (акаунт людини land-owner) PASS: Board за роком, компактна смуга проблем документа, формат чисел, FLERT «Connected» (через API).
- Попередні: b13 d75032896 (25.11 16:33), b12 (25.11 ранок).
- Перезапуск live після збою ПК — лише за словом людини: `powershell -File E:\EcrBackup\land-ui\b14\launch-live-b14.ps1 -ExpectHash 7afb19c97` (порти мають бути вільні).

## 2. Картки (`.sync-local\ready\integ-*.md`) — стан
**live-verified (25.11):** b13 — nav-mygroups, number-display, chunk-reload, required-banner, registry-as-of, project-delete (AC-0 чекає document-move); b14 — documents-board-yearly, doc-notices-compact, number-format-consistent, source-test-flert; audit-ux (b12, закрито 25.11 після питання людини). SHA в AC — cherry-pick SHA пачок (так вимагає board.py).

**Черга після 29.11 (за пріоритетом):**
1. **integ-audit-filters-live** (briefed; бриф у картці; робоча копія `C:\ecr-wt\audit-filters` на `lane/integ-audit-filters-live` від 7afb19c97, порожня): D1 Cell changes за документом 96 → SQL timeout 503 + безкінечні повтори; D2/D3 поля Document/Column приймають лише внутрішні id (хибне «No changes»); D4 однакові назви тимчасових акаунтів у By user. Людина питала «Ти виправляєш аудит?» — це перше.
2. **integ-document-move** (R3, DESIGN-document-move rev 2 + REVIEW PASS-WITH-FIXES B1–B6): разовий скрипт перенесення doc 98 (HSE400) і 93 (HSE401) у LANDUI2026; рішення людини — у пам'яті human-one-project-live-1125. Лейни: P (owner-aware HSE400 loader; вікно редагування HSE401 = параметр шаблону «рік проєкту + зміщення»; міграція/контракт) і T (tools/document-move + hse400/hse401 v14). Порожні проєкти HSE400_2026/HSE401_2026 видалити одразу у вікні; HSE400_GOLD видалити.
3. **integ-land-stationary-arg** (planned): doc 96 Stationary Equipment рядки 1–3, січ–сер — StepError `#ARG` (ECW_EC_tons_NOx, ECW_EC_gsec_301) після перерахунку людини 16:56; причину не встановлено.
4. **integ-sg-flaring-composition** / DESIGN-sg-flaring (+ `.sync-local\design\FLARES_TOX_HSE400_DATAFLOW.md` людини; паритет 2025: TOx 0 event frames, FG січ–чер на складі HSE400 2026, PI лише лип–гру 2025, SG реальний увесь рік).
5. Решта planned: stale-banner-audit («updated by another user»), source-test-categories, config-fallback-guard (флейк ConfigurationKeysTests: дві DefaultPermit 30/20), project-delete-large, e2e-airlimits-isolation, doc-flow-ui-polish («No access» замість 404), server-language-rest.
- Перелік незакритих карток пачок b12–b14: `Get-ChildItem .sync-local\ready\integ-*.md` + рядки «Статус:» / «Пачка:»; `board.py --window <live SHA>` показує, що «не їде».

## 3. Агенти / сесії
- Агентів у роботі немає (audit-filters зупинено до першого коміту).
- Cron у сесії: :13 hourly (`hourly.py`), :37 ретроспектива (кожні 2 год). У новій сесії їх треба створити заново (CronCreate) — див. §6.

## 4. Прибрати (лише після слова людини / білим списком; ECR, EcrLandUi, FLERT, EcrLegacyProd — НІКОЛИ)
- Бази: EcrLandB13Reh, EcrLandB14Reh, EcrLandB14Flert, EcrReqBanner, EcrE2EReqBanner.
- Теки: C:\ecr-wt\b13-bld, b14-bld, b13-reh, b14-docui, b14-numfmt, b14-flert, audit-filters (worktree, порожня); G:\EcrData\b13reh, b14reh, b14flert.
- Тимчасові акаунти live f13-* — усі IsActive=0 (останній f13-b14-sa).
- Диск F: 10 ГБ вільно — нових агентів/стендів на F: не ставити.

## 5. Рішення людини цього дня (пам'ять, `memory\human-*-1125.md`)
- Один проєкт на live LANDUI2026 (Land 96, HSE400 98, HSE401 93, тест 100); перенесення — разовий відрепетируваний скрипт.
- HSE401 — «просто таблиця для вводу», без надсилання/погодження; вікно редагування — параметр шаблону.
- Перенесення doc 98: переклад місяців + видалення січневих копій; purge HSE400_2026/HSE401_2026 — одразу у вікні.
- Аудит: «перевірте всі вкладки та поля фільтрації» → зроблено, дефекти в картці audit-filters-live.
- Уроки дня 543–559 у `memory\self-improve-practices.md` (556 PATH dotnet до зупинки live; 557 UTF-8 службових файлів; 558 доказ вікна без обрізання; 559 UI-прохід за `board.py --window`, а не з пам'яті).

## 6. Перенесення скіла `ecr-subtask-gates` на інший ПК
Що скопіювати (усе є в гілці `handover/coordinator-20261125`, тека `handover/coordinator-20261125/`):
| Звідки (цей ПК) | Куди (новий ПК) | Що |
|---|---|---|
| `C:\Users\Babyc\.claude\skills\ecr-subtask-gates\SKILL.md` | `%USERPROFILE%\.claude\skills\ecr-subtask-gates\SKILL.md` | опис скіла для Claude Code |
| `H:\ECR\.sync-local\gates\` (скрипти, `ref\01–07`, `CARD-TEMPLATE.md`, `test_gates.py`, `wt-baseline.json`) | `<клон ECR>\.sync-local\gates\` | механіка: hourly/board/where_is/overlap/route/wt_audit |
| `C:\Users\Babyc\.claude\projects\H--ECR\memory\` | `%USERPROFILE%\.claude\projects\<ім'я теки проєкту>\memory\` (ім'я = шлях клону з `\`→`-`, напр. `H--ECR`) | довга пам'ять, MEMORY.md — індекс |
| розділ «## ECR: координатор» з `C:\Users\Babyc\.claude\CLAUDE.md` | `%USERPROFILE%\.claude\CLAUDE.md` | автозапуск скіла й cron після /compact |
| `.sync-local\ready\integ-*.md`, `DESIGN-*.md`, `design\`, `PRIORITY.md`, `TRACKING.md`, `RESUME-Coordinator-1118.md`, `WINDOW-b1*.md`, цей файл | `<клон ECR>\.sync-local\` | стан роботи |

Вимоги: Python 3.12 (`%LOCALAPPDATA%\Programs\Python\Python312\python.exe`, лише stdlib), git (тут — з Visual Studio: `...\Team Explorer\Git\cmd`, або в PATH; інакше змінна `GIT` / `--git`), gh (winget, `GitHub.cli`), .NET 10 SDK + dotnet-ef, Node ≥ 22.12, SQL Server default instance з `-E`. Шляхи `H:\`, `C:\ecr-wt`, `E:\EcrBackup`, `F:\ecr-backup` — під цей ПК; на новому замінити в RESUME/скриптах вікна.
Перевірка: `python -X utf8 .sync-local\gates\test_gates.py` (усі зелені), потім `python -X utf8 .sync-local\gates\hourly.py --repo <клон> --roots <теки worktree>`.
Cron у новій сесії (CronCreate): `13 * * * *` — prompt «Coordinator control (hourly…)» (текст — `gates\ref\07-kontrol-uroky.md`), `37 */2 * * *` — ретроспектива (CLAUDE.md п.13).
⚠ Без секретів: `cred.txt`, `*.tmp` з паролями, рядки підключення з паролями на інший ПК копіювати ЛИШЕ руками людини, не через git.

## 7. For the next Claude (English)
**Who you are.** The ECR coordinator for repo BabychPV/ECR (Windows, PowerShell 5.1). You do not write product code yourself: agents write code from cards and briefs; you plan, brief, accept, assemble batches, run live windows, and verify on live yourself with evidence (counts before/after, screenshots, the human's own screens).

**Human's rules (non-negotiable).**
- Talk to the human in **Ukrainian**; write to agents in **English**. Short, concrete, no jargon.
- EcrLegacyProd is **read-only**. Live (EcrLandUi :5094/:6094), windows, RESTORE — **only on the human's explicit word**. Never print passwords; never type the human's password (they sign in themselves in the browser pane). Credentials file `C:\Users\Babyc\Downloads\cred.txt` — never print, never commit.
- Never `--force`, `--no-verify`, `git add -A`. Never drop ECR, EcrLandUi, FLERT, EcrLegacyProd. Delete nothing during gate processing; cleanup only by whitelist and the human's word.
- No debts: every finding becomes a card (or a new AC) in the same turn. Keep `.sync-local\TRACKING.md` updated every turn.
- Batches go to live in windows (≤ 1/day, not at 02:15/03:30). Before GO, tell the human «що побачите / чого ще не буде».

**How to start.**
1. Invoke skill `ecr-subtask-gates`; read `gates\SKILL.md` and the `ref\` file of the stage you are in.
2. Read this HANDOVER, then `.sync-local\RESUME-Coordinator-1118.md` (SNAPSHOT-11 + addenda), `PRIORITY.md` (human's words, newest at the bottom), `TRACKING.md` tail, and `memory\MEMORY.md`.
3. Create the crons (:13 hourly.py, :37 retro). Run `hourly.py`; resolve each `[!]`.
4. Check `get_usage`; at 98 % weekly stop agents and push WIP; at 99 % write a HANDOVER like this one.

**Current priorities.** (1) audit-filters-live — re-launch the agent with the brief in the card (D1 first, on the live copy EcrLandB14Reh, never on live). (2) document-move (R3; design reviewed; human decisions recorded). (3) land-stationary-arg (#ARG in doc 96 Jan–Aug). (4) SG flaring on gas composition. Then the remaining planned cards.

**Traps of the last days (lessons 534–559).**
- Live carries **cherry-pick SHAs** of the batch; card AC rows must use them or hourly/board flag «different SHA on live» (539).
- `Get-ChildItem` shows stale size/time for files held open — the live API *does* write `%ProgramData%\ECR\logs`; read real length via FileStream (550).
- Before asking the human, look in their md files and existing concepts (549, 551).
- e2e `workflow_dispatch` needs a **branch name** as ref, not a short SHA (552); check **every** workflow on a SHA incl. Evidence (553).
- `sqlcmd` UPDATE needs `-I`. In the coordinator shell `dotnet` may be missing from PATH — check `dotnet ef --version` **before** stopping live (556). Read/write `.sync-local` files as UTF-8 explicitly (557). Store window evidence untruncated (558). UI pass list comes from `board.py --window`, not memory (559).
- The browser pane cannot type the human's password; the documents route is `/`, admin screens are `/admin/*`.

**Where the human's decisions live.** `PRIORITY.md`, `memory\human-*.md` (indexed in MEMORY.md), card headers («Рішення:» line with the human's exact words), DESIGN-* files with «Відповіді людини» sections.
