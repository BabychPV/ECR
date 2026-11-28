# HANDOVER сесії координатора — 26.11.2026 (вечір), читати першим після стиснення контексту

Попередні: `HANDOVER-NEXT-PC-2026-11-22.md` (моя робота до 22.11, уся злита в main b7/b8), хандовер іншого Claude —
гілка `origin/handover/coordinator-20261125`, локально `F:\ecr-wt\handover-read\handover\coordinator-20261126\sync-local\`
(`HANDOVER-ALL-20261126.md`, `PRIORITY.md`, `ready\integ-*.md`, `DESIGN-*.md`, `REVIEW-*.md`). Пауза о 99 % — `PAUSE-2026-11-26.md` (застаріла, цей файл новіший).

## 0. Середовище і правила
- Цей ПК: репо-worktree у `F:\ecr-wt\*` (основний `F:\ecr-wt\stand-main`). Live (EcrLandUi :5094/:6094), копії live (EcrLandB14Reh), E:\EcrBackup — ЛИШЕ на іншому ПК. Тут — код + CI.
- Live SHA = 4980a1837 (b16). main на початок дня = ecdd84621 (b11). Усі нові лани — від 4980a1837.
- Людина: «продовжуй на повну» (ресурси ПК без обмежень). Агенти: Opus — ядро/R3, Sonnet — дрібне; кожна R3-лана — незалежне Opus-рев'ю; координатор сам дивиться дифи.
- Процес RC: інтеграційна гілка лише `lane/integ-*` → CI+Evidence зелені на SHA → `gh api repos/BabychPV/ECR/git/refs -f ref=refs/heads/rc/<дата>-bN -f sha=<SHA>` → `gh pr create` (тіло закінчується «🤖 Generated with [Claude Code](https://claude.com/claude-code)») → e2e (6 шардів) → `gh pr merge N --merge --match-head-commit <SHA>`. Оновити RC: `gh api -X PATCH …/git/refs/heads/rc/… -f sha=… -F force=false`.
- gh ≤ 1 виклик / 10 хв у вочерах (урок 569). `sqlcmd` UPDATE на live — `-I -b` + SELECT (урок 572).
- Заборони: EcrLegacyProd — лише читання; live/вікна/RESTORE — лише словом людини; паролі не друкувати; без force-push, `--no-verify`, `git add -A`.
- Тижневий ліміт 75 % (скидання 28.11 09:00 UTC); 5-год — скинувся ~21:00 UTC.

## 1. RC b17 — PR #1007 (rc/2026-11-26-b17), у процесі злиття
- `lane/integ-b17` = origin/main + integ-source-credentials + integ-row-label-messages + integ-audit-column-lineage (+ live-тільки b15/b16 аудит, бо лани від 4980a1837).
- Голова d7d6c661166f3693f9faf147baef9b8b1735c7e6 (фікс 2 e2e: «Row N» замість ключа; sheetLogLive чекає власного перерахунку). CI ✓ (після перезапуску нестабільного AdminDuplicateRequestHttpTests.d8), Evidence ✓, rc-ref пересунуто.
- Фоновий вочер (Bash bfv94ok30) чекає перевірок PR і сам робить `gh pr merge 1007 --merge --match-head-commit d7d6c661…`. Перевірити результат: файл виводу задачі або `gh pr view 1007 --json state`.
- Після злиття: на live — лише вікном зі словом людини (AC-0 паролів джерел робить людина: сама вводить пароль PI).

## 2. Лани в роботі (агенти; нічого не запушено, крім b17)
| Лана / worktree | Агент | Стан |
|---|---|---|
| `lane/integ-doc-ux-server` / F:\ecr-wt\doc-ux-server | a6699b4b0c65b2e96 (Opus) | ГОТОВО: c2a944e71, e443b4598 поверх; Arch 1484/1484. Контракт a28911bb8. Йде незалежне рев'ю (агент a5859405734677c53). Потім виправлення знахідок. |
| `lane/integ-doc-ux-review` (клієнт) / F:\ecr-wt\doc-ux-review | a49bc1daf5829f591 (Opus) | Відновлено з WIP 837416808: повний vitest/tsc/eslint, мутації, розбити WIP. При інтеграції зняти 2 тимчасові винятки EndpointCoverage (calc-status, required-inputs) — вони в серверній гілці 56be693b4. |
| `lane/integ-document-move` (R3) / F:\ecr-wt\document-move | abba5ecafa5768056 (Opus) | Рев'ю закрито (D-579, людина: HSE401 редагується і до початку року). Лишилось L-7: WIP cec3f9b4d — фільтр 05-rpt-views.sql генерував невалідний SQL; агент ремонтує. Репетиція/вікно — інший ПК (README tools/document-move, W-0…W-10). |
| `lane/integ-source-impact-months` / F:\ecr-wt\source-impact-months | a6d7832b3c049ee5a (Opus) | Баг людини (HSE400: змінив січень, вікно показало ще 2 міс) — причина: вікно мало своє правило; виправлено (D-581). Рев'ю: H1 override whole-year, H2 не міняти політику D-554 (closed/approved рахувати), M1 перф (200 споживачів, легкий шлях у транзакції), M2-M4, L1-L3 — WIP 509eb18ef, відновлено: збірка, тести, замір, D-581. |
| `lane/integ-sg-flaring-composition` (R3) / F:\ecr-wt\sg-flaring-composition | a0d3ce1f3f10932fa (Opus) | ✅ ГОТОВО 69d74c0ba (Arch 1485/1485; TOX рік 6,2 с — індекс пари полів у RegistrySnapshot, координатор переглянув дифф). Було: D-580 (людина: ROW.* у REG*, колонка SLOT для о. D, TOx 481 H2S у скрипті, порожня T_C = «бракує даних»). Рев'ю виправлено (H1-H4, M1-M5, L1-L6) до WIP ae1237157; відновлено: тести M3, паритет dry-run, Arch, ⚠ ПЕРФ: TOX рік 5840 рядків = 52 с → ціль ≪10 с. `_wip/` у worktree не комітити (бекап F:\ecr-wt\_wip-backup). |
| `lane/integ-row-label-messages` | a8726921b4201dc7e | Злито в b17. |
| `lane/integ-audit-column-lineage` | adb7a424f901cdf08 | Злито в b17. |
| `lane/integ-source-credentials` | add083ef5b2ccc43e | Злито в b17. |

Номери рішень: D-577/D-578 doc-ux, D-579 document-move, D-580 факели, D-581 source-impact. Наступний вільний — D-582.

## 3. Рішення людини 26.11 (обов'язкові)
- Джерела PI SQL Client/SQL: обидва входи (Windows і логін+пароль); `TrustServerCertificate=true` для SQL з паролем — заборонено (обрано суворо; людина не заперечила).
- UX документа (план `docs/build/UX-REVIEW-document-screen.md` у doc-ux worktree): Q2 автоперерахунок після правок — так, тихо; Q3 закриті/погоджені застарілі місяці — сіра примітка; Q6 порожній обов'язковий вхід методології блокує подання; Q5 смугу перерахунку проєкту з документа прибрати; Q4/Q7 — дефолти. Легенда станів комірки + рядок «Calculated by / Read by» в інспекторі — так.
- D-578: обов'язкові входи виводяться АВТОМАТИЧНО зі скрипту методології, що застосовується до рядка; блокує одразу; порожнє = «бракує даних» (не 0); послабити (Optional/Warn) — лише методолог.
- HSE401 — «файл без апрува та надсилання, просто таблиця»: редагується й до початку року, закривається після року + відступ.
- Факели: склад газу = окрема сутність «Склад газу» поверх довідників STREAM/STREAM_CASE/GAS_COMPOSITION/COMPONENT (D-476).

## 4. Відкрите людині / інший ПК
- Q-D: джерело PI AF + обліковий запис на live для подій факелів.
- Перед вікнами: діагностичний SQL `tools/diagnostics/d578-derived-required-inputs-impact.sql` (doc-ux-server) на копії live — скільки рядків почнуть блокувати подання.
- Репетиції document-move (двічі на копії), паритет факелів (`tools/hse30x/Compare-SgFlaringParity.ps1` проти legacy з 2025), live-перевірка вікна впливу HSE400 (doc 98, змінити лише січень).
- `land-stationary-arg` (#ARG doc 96) — причину шукати на копії live (інший ПК).
- Тимчасові тестові бази `EcrTest_*` накопичуються на цьому ПК — прибрати після завершення агентів (питав людину, відповіді ще нема).
- `FormulaJobFixedCostTests` (22 запити проти стелі 20 у великих прогонах) — перевірити на main CI.

## 5. Наступні кроки координатора
1. Дочекатись злиття #1007 (вочер); якщо e2e червоні — розібрати, фікс тесту/коду, PATCH rc-ref.
2. Прийняти звіти агентів; для doc-ux-server — віддати знахідки рев'ю агенту a6699b4b0c65b2e96.
3. Зібрати b18: `lane/integ-b18` від свіжого main = doc-ux-server + doc-ux-review (зняти винятки EndpointCoverage) + source-impact-months (після своїх фіксів) + document-move + sg-flaring (після рев'ю/перфу); локально Architecture → push → CI → RC → e2e → merge. Конфлікти очікувані в openapi.snapshot.json / schema.d.ts / 10-decisions.md / CHECKSUMS (перерахувати sha256: `tr -d '\r' < docs/tz/<f> | sha256sum`).
4. Після злиття — UI-прохід у браузері на локальному стенді (v4-api 5088 / v4-web 5188, `.claude/launch.json`) по екрану документа.

## 6. Ніч 26→27.11 (людина відійшла до ранку)
- Людина: «контролюй періодично включно з лімітом, заверши всі задачі; все перевірити на стенді вживу через UI; питання збирай — відповім зранку; бери найкращі дефолти». Питання → `QUESTIONS-MORNING-2026-11-27.md`.
- b17: e2e shard 1 червоний — адреса інспектора «this row» замість «Row N»; виправлено в коді (addressChip бере rowLabel) 6c5f40683, запушено. Вочер b2jl8cqsd: CI → PATCH rc → e2e → merge.
- doc-ux-server: рев'ю a5859405734677c53 — H1-H4, M1-M7, L1-L5 + прогалини тестів передано агенту a6699b4b0c65b2e96; M5 людина: БЕЗ позначки (D-442).
- Тижневий ліміт 80 % (скидання 28.11 09:00 UTC) — ≥95 % → пауза.
- ScheduleWakeup кожні 30 хв — нічний контроль.
- 00:40 UTC: b17 CI зелений на 6c5f40683, RC пересунуто, вочер b2jl8cqsd чекає e2e → merge.
- Готові до b18: sg-flaring 69d74c0ba; source-impact-months f949cae71 (повний Arch після реєстрації Forecast.cs не перезапущено — зробити в b18); doc-ux-review клієнт f1c645fd0 (vitest 9189/9189). Чекаємо doc-ux-server (фікси рев'ю) і document-move (L-7 прогін Reporting).
- document-move ГОТОВО 50e31b97b (L-7 закрито; Arch 1484/1484; повний Reporting 169 після CRLF — перевірить CI b18). Лишився лише doc-ux-server.
- 01:30 b17 e2e знову червоний (адреса «row 1» з малої) → 79eebf2a4 (велика літера), вочер bppbs9re0.
- 02:30 doc-ux-server ГОТОВО 6e5ce7ff6 (+ дрібний фікс «колонки нема в таблиці» в роботі). b18 передзлиття: integ-b18 (4 лани + фікс тесту коду помилки), Arch 7 падінь — серверні, закриваються гілкою doc-ux-server.
- 03:20 b18 = 733be065d запушено (lane/integ-b18 від lane/integ-b17 + 5 лан; Arch 1489/1489, tsc ✓). Вочер b18-ship.sh: CI лани → чекає злиття #1007 → далі вручну rc/2026-11-27-b18 + PR (тіло scratchpad/b18-body.md). b17: RC пересунуто на 79eebf2a4, вочер bppbs9re0 чекає e2e.
- ~02:50 UTC 27.11: b17 ЗЛИТО в main (PR #1007, merge 77bdadfae). b18: CI на 577ad5206 (стеля перф-тесту TOX 45 с) іде; вочер bu64xj73c.
- 04:30 b18 CI червоний: (1) міграція YearEditWindow в ідемп. скрипті → EXEC, (2) перф-тести SG на CI 24-47 с → стелі 150/30 с — коміт 122d5d259 (не запушено); (3) бюджет DocumentPage 253.3>250 КБ: мій порт навігації → 251.6 (незакомічено), решту доробляє агент Sonnet a6542f30a05572901 у integ-b18. Потім push → CI → RC b18.
- 05:00 b18 2fa79308b запушено (бюджет DocumentPage 247.2 КБ, Arch 1489/1489). Вочер br7qdfck6: CI → rc/2026-11-27-b18 → PR → e2e → merge (усе автоматично).
- 05:10 b18 CI ✓ → rc/2026-11-27-b18, PR #1009; вочер br7qdfck6 чекає e2e → merge.
- 05:55 b18 PR #1009 e2e червоні: 9 спеків (банери → рядок повідомлень, автоперерахунок блокує подання одразу після правки). Передано клієнтському агенту a49bc1daf5829f591 у integ-b18 (лог F:\ecr-wt\e2e18-b18.log). Після фіксу: push → CI → PATCH rc/2026-11-27-b18 → e2e → merge.
- 06:20 e2e-фікси 3e95d67d3 (продукт: назва чипа без опису — keyboardPath; «подання чекає перерахунку» з кнопкою «Submit again») + 1833b6127 (спеки під рядок повідомлень). Arch 1489/1489. Push; вочер b18-retry.sh: CI → PATCH rc → e2e → merge #1009.
- 06:45 UI-перевірка вживу (стенд v5 :5089/:5189, база EcrLandMsLegacy3, користувач ux-check — створила людина скриптом tools-ms\New-UxCheckUser.ps1, пароль у STAND-v5-uxcheck.local.txt). ✓: рядок повідомлень, легенда, «Type of fuel *», Read by в інспекторі, тихий автоперерахунок (~1 хв), лічильник обов'язкових + ‹›, «Required» у комірці, діалог подання. Знахідки 1-7 (чип vs «not finished», Escape легенди, сирий id довідника, ключ рядка в інспекторі, Status не переносить, обрізання рядка, порожній ключ правила мовчки) → лана lane/integ-docux-polish (F:\ecr-wt\docux-polish, агент Opus a6d0305fed5ac8f5e). Вплив HSE400 локально не перевірити (немає споживачів) — інший ПК. Дані doc 3 рядок 20 повернуто (Fuel=37, Power=37).
- 07:20 docux-polish (d0d762c09, 7 комітів) перевірено вживу на стенді v6 (:5091/:5191, код docux-polish) — усі 7 знахідок ✓; дані doc 3 повернуто. Лана запушена (без PR); після злиття b18 → b19.
- 07:30 b18 e2e раунд 2: 4 спеки (liveStatus notice-live-status, tableChatLiveRefresh 2-й тест, staleCalculations calc-status, staleResultsFlow route.fetch) → агент a6d0305fed5ac8f5e у integ-b18 (лог F:\ecr-wt\e2e18-b18-r2.log).
- 07:55 b18 e80ecdbd3 запушено (пріоритет live-status 4 + спеки; Arch 1489/1489). docux-polish = 7765645d0 (злито b18). Вочер b18-retry.sh: CI → PATCH rc → e2e → merge #1009.
- 08:40 UI-рев'ю всіх 22 розділів + вкладки проєкту/шаблону/документа (стенд v6, ux-check). Знахідки A-L передано агенту a6d0305fed5ac8f5e у docux-polish (b19); J (дубль місяців на Periods) — питання людині п.7.
- ~09:20 b18 ЗЛИТО в main (PR #1009). Далі b19 = main + docux-polish (після звіту агента про A-L).
- 10:00 b19 = 105fae680 (main+polish, Arch 1489/1489) запушено; вочер зупинено: регресія «Row 0» для динамічних рядків в аудиті/історії → агент a6d0305fed5ac8f5e фіксить в integ-b19. I — не баг (перевірено 1093<1105). Стенд v6 тепер на integ-b19.
- 10:30 b19 5552711c0 (Row N у аудиті/історії, перевірено вживу «Row 20») запушено; вочер b19-ship.sh: CI → rc/2026-11-27-b19 → PR → e2e → merge.
- 11:15 b19 CI: впав застарілий тест AuditReaderChangedColumnsTests (чекав TableRow.Ordinal) → оновлено af8dcc76a (71/71 аудит/історія), запушено; вочер blnz9egmi.
- 12:40 b19 PR #1010 e2e: requiredInputsBanner (ru «Нужно методологии» → «Требуется для методологии», e19cbf941) + sourceChangedAfterApproval (нестабільний з b17, чекаю перезапуску). Arch 1489/1489. Вочер b19-retry.sh.
- 13:10 видалено 57 тестових баз EcrTest_* (~22 ГБ) за згодою людини.
- 13:30 Людина: поріг паузи тижневого ліміту — 98 % (замість 95 %). Борги 1-8 → лана lane/integ-debts-1127 (F:\ecr-wt\debts-1127, агент Opus; Quartz-відновлення першим). Periods-дубль → lane/integ-periods-dedup (F:\ecr-wt\periods-dedup, Sonnet). Після злиття b19: b20 = main + debts + periods → CI → RC → e2e → merge → перевірка вживу на стенді v6.
- 14:00 b19 e2e раунд 2: лише periodNavBounds (scrollY, у попередньому прогоні проходив — нестабільний) → rerun --failed, вочер b19-merge.sh. Periods-дубль готовий f911ce958 (Reopen тепер у «Details»). ⚠ Ще один нестабільний e2e — periodNavBounds; додати до боргу №8.
- ~14:40 b19 ЗЛИТО в main (PR #1010, d90ea8d0d). Далі b20 = main + debts-1127 + periods-dedup (після звіту агента боргів).
- 15:20 борги 1-8 готові (D-582 Quartz recovery, D-583 script MissingInputs, …). b20 = ebb5bc203 (main+debts+periods; Arch 1489/1489, tsc ✓) запушено; вочер b20-ship.sh: CI → rc/2026-11-27-b20 → PR → e2e → merge. Потім перевірка вживу на v6 (переключити на integ-b20).
- 15:30 Перевірка вживу b20 (стенд v6 на integ-b20, Quartz-режим): Periods Details ✓ (картка відкриває деталі й рядок); режими методології людською мовою ✓; D-582 ✓ — правка Power 37→38, PendingDelayedJob записано (due +20 с), API зупинено до запуску, рестарт → стару позначено Failed, нову поставлено й виконано (Succeeded, calc-status свіжий, lastFailure null). Power повернуто 37. «Row (added by a user)» — лише для видалених рядків, покрито тестами.
- 16:10 b20 PR #1011 e2e: requiredInputsBanner:374 «сітка зрушила» (267→303, +36px) — у b19 проходило; rerun --failed, вочер b20-merge.sh. Якщо повториться — регресія (кандидати: holdScrollPosition #8b, рядок повідомлень після автоперерахунку).
- ~16:50 b20 ЗЛИТО в main (PR #1011) — requiredInputsBanner:374 пройшов на rerun (нестабільний; спостерігати). Усі борги 1-8 + Periods у main.
- 17:00 Людина: «Reopen» на картки місяців — так; розібрати нестабільний requiredInputsBanner:374. Лана lane/integ-reopen-grid (F:\ecr-wt\reopen-grid, від main 3c488becb), агент Sonnet. Потім b21 → CI → RC → e2e → merge + перевірка вживу.
- 17:20 b21 = lane/integ-reopen-grid 313066caf (Reopen на картках dc5dc7889; спека requiredInputsBanner вимірює після стабілізації — зсув 36px був під час завантаження сторінки, не від правки; сам елемент не встановлено — дрібний пункт на потім). Вочер b21-ship.sh. Reopen на картці вживу не перевірити: на стенді немає закритих місяців (покрито 4 vitest). Стенд v6 тепер на reopen-grid.
- 18:50 b21 PR #1012: впав periodNavBounds:91 (scrollY 120→0 знову, попри #8b). Гіпотеза: після перемикання періоду сторінка коротша (менше рядків) → scroll не може бути 120 → спека має чекати min(y0, scrollHeight-innerHeight). Rerun --failed, вочер b21-merge.sh; після злиття — лана з правкою спеки.
- ~19:30 b21 ЗЛИТО (PR #1012, ddf710e16): Reopen на картках, спека grid-move. b22 = lane/integ-periodnav-spec 728d4a47a (спека periodNavBounds чекає min(y0, доступна прокрутка)) — вочер b22-ship.sh. Якщо periodNavBounds впаде знову — значить сторінка була висока, і це справжній баг продукту.
- 21:50 b22 PR #1013: periodNavBounds впав знову → гіпотезу «коротша сторінка» спростовано. Відтворено вживу: після «Previous period» уся сторінка на ~100 мс схлопується до висоти вікна (1010→720), прокрутка 0 і не повертається. Агент Sonnet фіксить у F:\ecr-wt\periodnav-spec (Suspense/перемонтування/startTransition). Після — push, PATCH rc/2026-11-27-b22, вочер на merge #1013.
- 22:40 periodNavBounds — справжній баг: (1) документ на мить замінювався коротким скелетом (агент: пам'ять позиції + min-height, 2b9c4f720); (2) заголовок H1 при повторному монтуванні брав фокус із прокруткою → preventScroll (07b57d624, тест f07e69eca). Перевірено вживу: scrollY тримається 120 весь час, фокус на H1. b22 f07e69eca запушено; вочер b22-retry.sh (CI → PATCH rc → e2e → merge #1013).
- 28.11 ~00:10 b22 ЗЛИТО (PR #1013): periodNavBounds пройшов з першого разу — стрибок прокрутки при перемиканні періоду виправлено в продукті.
