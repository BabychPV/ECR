# Пакет видачі: готовність 2026-10-26 (AU-3 / AU-4)

Документ готує Аудит. Він НЕ видає збірку: видачу `ecr-msi` робить людина за своїм словом (слово людини 26.10: «видати зараз»). Деталі — лише в посиланнях на звіти (`.sync-local/reports/`, поза репозиторієм).

## 1. Вердикт і що видається

**Вердикт: P0/P1 немає.** Два незалежні проходи (A і B) на одному заміреному SHA F, далі дві чисті дельти на F'. Це не гарантія відсутності дефектів і не «готово до промислової експлуатації»: див. розділ 4.

| Поле | Значення |
|---|---|
| main (основа, `git rev-parse origin/main`) | `6069d3ac4fedb16bc4668279858fd2e3c6d5007d` (мерж #617, rc/2026-10-26-f-prime) |
| Попередній мерж у main | `77e710911` (#616, p52), він же F проходів |
| push-CI на 6069d3ac4 | CI run 40300934811 (№4482) на момент підготовки `in_progress`; e2e-stand run 40301049589 (№630) `in_progress`. [ЗАПОВНИТИ: Аудит-2, conclusion обох після завершення] |
| CI на F (77e7109) | CI №4469 success (усі гейти); e2e-stand №627 success |
| MSI-артефакт `ecr-msi` | збирається job `msi (windows)`, ставиться job `msi-install (windows)` у push-CI на 6069d3ac4. [ЗАПОВНИТИ: Аудит-2 — run ID і результат обох job] |
| Версія MSI | [ЗАПОВНИТИ: Аудит-2 після push-CI на 6069d3ac4] (для F було 0.0.4469; для 6069d3ac4 не вгадую) |
| SHA-256 MSI | [ЗАПОВНИТИ: Аудит-2 після push-CI на 6069d3ac4] (звірити з `.sha256` артефакта; артефакт не завантажувався) |

## 2. Докази якості

Усі проходи — чорна скринька на стендах; Land/EcrLandUi не чіпались. Звіти: `TESTER-PASS-A-F77e7109-1/-2.md`, `PASS-A-P2BC-REPRO-F77e7109.md`, `TESTER-PASS-A-DELTA-F039d2de.md`, `PASS-B-F-20261026.md`, `PASS-B-FPRIME-20261026.md`, `AU-13-PLAN-2f4462c44.md`.

| Прохід | Виконавець | SHA | База | P0 / P1 / P2 знайдено | Закрито |
|---|---|---|---|---|---|
| A (повний) | Тестувальники 1 і 2 | F = 77e7109 | EcrPassA_77e7109 | 0 / 0 / 2 нові P2 (Т2: НЕ чисто) + кандидат P2 від Т1 (місячні колонки) | усі три P2 (A, B, C) закрито в F' |
| B (повний) | Аудит-2 | F = 77e7109 | B0 (9d1f3fb) + дані -> F | 0 / 0 / 0 | n/a; P3: PB-F-01, PB-F-02 |
| A (дельта) | незалежний тестувальник | F' = 039d2de | EcrPassA_039d2de | 0 / 0 / 0 | P2-A/B/C підтверджено закритими; нових P3: 5 |
| B (дельта) | Аудит-2 | F' = 039d2de | EcrPassB_Fp | 0 / 0 / 0 | PB-F-02 не відтворився; PB-F-01 лишився |

Фікси P2 (D-456): P2-A (запис у місячну колонку керує станом ЇЇ періоду) `4957c83d1`, `79871e382`, раніше `bf83a3de4`; P2-B (прихована таблиця = неіснуюча, S18) `1c6c48587`, `9fcb83697`; P2-C (Deny на реєстр групи `PRMG_*` блокує запис члена) `34d2d335f`. Також закрито до проходів: scoped View/Deny (p48, p50, D-455), editassign-sec `bccafd0fc` (D-454), PERF approve `1bb8e7929`.

Перевірено на B-проході: upgrade B0 -> F і main(F) -> F' двічі (міграції 91->98 / 98->98, друга перевірка == перша, row-count і UiString без розбіжностей), idempotent-скрипт (SHA-256 `4B41D940...CE5A8`, `sqlcmd -I -b` x2), `has-pending-model-changes` чисто, NUL у `Sql/*.sql` = 0, `smoke -ExistingDatabase` 25/25, UI/a11y sweep 348 сторінок (en/ru/kz, light/dark, 1440/500): `⟦⟧`=0, 5xx=0, axe serious/critical (крім target-size)=0, live-сценарії Playwright D-440…D-452 PASS (два червоні першого прогону — стан фікстури, не продукт).

Динамічна безпека: `SECURITY-P2-DYNAMIC-*.md` (S18-оракул, Deny-матриця), огляд `REVIEW-SECURITY-D440-D452-2f4462c44.md`, `REVIEW-P50-6a-scopeddeny.md`.

CI-гейти: вісім обов'язкових для `main` (`build`, `test`, `honesty-guard`, `server`, `client`, `a11y (dark)`, `a11y (light)`, `pr-source`). Агрегатор `windows`, `smoke (linux)`, `e2e-stand (linux)` за CLAUDE.md (2026-10-10) НЕ обов'язкові в ruleset — зараз дивимось на них очима; зробити обов'язковими може лише людина.

## 3. Ключові зміни для замовника (D-440…D-456, `HANDOFF-BRIEF-1021.md`)

- Подання найранішого Open/Grace періоду; учасники документа через EditAssignment з видимістю; правка погоджувача; «4 очі» скасовано всюди (D-440…).
- «My tasks» (черга погоджень), листи аркуша (D-451) — вимкнені за замовчуванням; вікна формул (D-452).
- Scoped-права `Registry.View` і Deny на проєкт/таблицю/реєстр групи (D-455, D-456): прихована таблиця відповідає як неіснуюча.
- Місячні колонки (`MonthsInColumns`) керуються станом СВОГО періоду; пакет з забороненою коміркою відхиляється цілком (all-or-nothing; Closed/Submitted/Approved -> 403, Grace -> пізня правка, Scheduled -> дозволено).
- Групи типів дозволів (Permits); швидший approve (`1bb8e7929`); міграція версії з швидким заголовком (D-439).

## 4. Відомі P3 / борги (відкриті)

| Що | Суть | Де |
|---|---|---|
| PM-PASS-A-P3 (10 пунктів) | S18-оракул часу (6,2 vs 4,2 мс); кому йде лист «повернуто» (D-451, питання людині); Reopen для SysAdmin/PAAdmin без ролі кроку (слово людини); Apply імпорту не-поточного погоджувача -> 200 з 0; апостроф при експорті xlsx; HSTS/Secure-cookie лише на проді; сирий enum у тексті відмови; JSON true->1, межа Int; HTML в описі групи | `WORK-QUEUE.md` |
| PM-EDITASSIGN-P3 | QueryCount/перф `OwnLevelOver`, тест 403 `editAssignmentLevelAboveOwn`; Excel-імпорт не фільтрує за `Registry.View` (слабкий оракул існування коду); пікер реєстру не враховує scoped View; PeriodAdministrator/Bootstrap без Write-грантів не покриті тестами | `WORK-QUEUE.md`, `REVIEW-EDITASSIGN-SEC-*` |
| KNOWN-ISSUES 2026-10-21 | відкриті P3 (UI Permits, діалог міграції KI-26…29, CI-флейки KI-20…22) | `KNOWN-ISSUES-2026-10-21.md` |
| p53 (Аналіз, in progress) | Excel all-or-nothing по місячних колонках + audit `periodKey` для M-колонок | `WORK-QUEUE.md` |
| Q-17 | два джерела правди для M-колонки; дефолт «як є» до слова людини | питання координатора |
| AN-134 | зрізи `calc.SubmissionSnapshot` ~10 МБ на подання, політики прибирання немає; реалізація `DeleteEnabled=false` ЗА ЗАМОВЧУВАННЯМ (ВИМКНЕНО), суперечить D-67 — не вмикати без слова людини | `WORK-QUEUE.md` |
| Інше з HANDOFF §4 | таймер `RecurringScheduleService` лишено як є; kz-переклади для термінолога; порядок блокувань учасника може дати 1205 (повтор) | `HANDOFF-2026-10-24.md` |

## 5. Що НЕ покрито (чесно)

- UI-прохід агентами: авторизований UI тестувальників A не проходився (пароль не вводився); покриття UI — CI a11y dark/light, e2e WALK і Playwright-сценарії B; решта (T4 вікна формул, My tasks в UI, листи D-451) — «CI + людина» за STAND-TEST-GUIDE.
- Land: запис у Land/EcrLandUi, Reopen doc 2/202606, вікно 11, V2 — не виконувались (потрібне слово людини).
- MSI на ЧИСТІЙ VM: VM немає (hypervisor є, Get-VM/VirtualBox/Sandbox відсутні) — лише артефакт CI і лог `msi-install (windows)`. MSI не збирався й не встановлювався локально.
- Authenticode/підпис MSI — не перевірено; HTTPS-сертифікат; оновлення поверх РЕЛІЗНОГО MSI і відкат MSI на VM; D3; AU-7/R2.
- Листи D-451 вимкнені за замовчуванням, SMTP на проді не перевірявся. PeriodAdministrator/Bootstrap без `Registry.View` — не перевірялось (прийнято).
- Upgrade перевірено на синтетичних/стендових даних, не на копії прод-бази.

## 6. Встановлення, оновлення, відкат

Повний опис параметрів: `tools/deploy-ecr.ps1` (`Get-Help`), `docs/admin/admin-guide.md`, `docs/build/04-environment.md`.

Передумови: SQL Server (не нижче 2016 SP1; Express — лише з `-AllowExpress`), диск під файлові групи (~15,5 ГБ на нормальному профілі), ASP.NET Core Hosting/.NET runtime відповідної версії, сертифікат DataProtection (`-DataProtectionThumbprint`), рядок підключення і пароль bootstrap-адміністратора (одноразовий файл `bootstrap.secret`, не в конфіг), обліковий запис служби (`-ServiceAccount`).

Оновлення, у порядку:
1. Повний бекап БД (скрипт перевіряє свіжість; `-SkipBackupCheck` не використовувати).
2. Зупинити службу `EcrApi` (і `EcrWorker`).
3. Встановити MSI: `msiexec /i <ecr.msi> /qn /l*v <install.log>` (або `-MsiPath` у `deploy-ecr.ps1`).
4. Міграції і скрипти `Sql/*.sql` (`deploy-ecr.ps1`; ідемпотентний скрипт EF; `sqlcmd -I -b`), сід виконує застосунок при старті.
5. Старт служби; перевірка `/health/live`, `/health/ready` (Healthy/Degraded), вхід, `smoke`.

Приклад (параметри — ті, що в скрипті): `powershell -File tools\deploy-ecr.ps1 -Server <SQL> -Database <БД> -MsiPath <ecr.msi> -ServiceAccount <акаунт> -DataProtectionThumbprint <відбиток> -HttpsThumbprint <відбиток>`. [ЗАПОВНИТИ: Аудит-2/людина — підтвердити точний виклик для цільового сервера.]

Перевірка «upgrade main -> SHA x2 + smoke + ідемпотентність» виконана Аудитом-2 (`PASS-B-F-20261026.md`, `PASS-B-FPRIME-20261026.md`).

Відкат: Down міграцій обмежений (D440/D443 кидають THROW) — відкат даних лише RESTORE з бекапу (D-439, `admin-guide.md`, розділ про міграцію); після RESTORE до повторної міграції в проєкт не писати. Відкат MSI: `msiexec /x` і встановлення попередньої версії (на VM не перевірялось). Fast-header — відкат тільки через backup RESTORE. AN-134 не вмикати.

## ⚠ ПОТРІБНЕ ВІД ВАС

1. Слово на видачу `ecr-msi` (після заповнення версії/SHA-256 і зеленого push-CI на 6069d3ac4).
2. Q-17 (M-колонка) і `DeleteEnabled`/AN-134 проти D-67 — дефолт «як є» / ВИМКНЕНО; потрібне слово, якщо інакше.
3. Чи робити `windows` і `e2e-stand (linux)` обов'язковими в ruleset `main` (дія людини).
4. Запис у Land/EcrLandUi, Reopen doc 2/202606, вікно 11/V2 — лише за вашим словом.
5. Листи D-451 на проді (SMTP, адресати «повернуто» — PM-PASS-A-P3 п. 2) і Reopen для SysAdmin/PAAdmin без ролі кроку (п. 3).
6. AU-7/R2, підпис MSI (Authenticode), HTTPS-сертифікат, проби на чистій VM — рішення про строки/доступ до VM; PeriodAdministrator/Bootstrap без `Registry.View` — прийнято, підтвердьте.
