# PRIORITY — читати на початку КОЖНОГО ходу і перед стартом нової задачі (правило 428)
Пункт тут має перевагу над будь-якою чергою. Підтвердження — «ACK <id>» першим рядком наступного листа координатору.

- P-0117-1 [PE] 17.11 06:55Z: тести B147 (F13 FAIL, 3 тести A2/B3/E2) — ПЕРШИМ, до B158a. Блокує вікно W15w ввечері.
- P-0117-2 [AN] 17.11 06:55Z: B79 — пуш на 6bb2aebed якнайшвидше (останній у складі W15w).
- P-0117-3 [PE] 17.11 07:25Z: після тестів B147 (P-0117-1) — B5 L5 (.sync-local\RESUME-PE-L5.md) і залишки L7 клієнта: річний Land на live (B163/B6). Раніше за B161/B158/B155. Ціль — вікно W15x 18.11.
- P-0117-4 [AU] 17.11 07:25Z: після W15w — план і репетиція B6 L8 (перенос doc 96 LAND_UI 1.0.18 PerMonthCopies → річна версія, звірка з місячними результатами, перф на великому бекапі).
- P-0117-5 [ALL] 17.11 09:10Z людина: «важкі прогони — на пачках, де багато змін». У своїй lane — лише швидкий CI (build + Architecture + тести зачеплених класів + e2e зачеплених spec grep'ом); повний CI/F13/RC — на пачці lane/integ-batch-* (зводить координатор). 20×/мутації — лише клас ризику; після rebase лише docs/контракт-побайтно — НЕ перепрогонювати.
- P-0117-6 [ALL, особливо AU] 17.11 11:50Z людина: «оновимо стенд завтра щоб там були всі зміни». СЬОГОДНІ ВІКНА НЕМАЄ. Одне вікно 18.11: B79, B151, batch-1117-1b, batch-1117-2 (річний Land L3/L7ab/L5 + перенос doc 96 з Reopen, B158, B161), B155. Тихий слот — завтра, оголосить координатор після GO-репетиції.
- P-0117-7 [AU] 17.11 15:30Z: (1) Людина 11:45Z «це тестове середовище тому дозволяю відкрити повторно» — Reopen поданих/погоджених аркушів doc 96 за 2026 для переносу L3 ДОЗВОЛЕНО: штатний Reopen, причина «перенесення на річний Land (B6)», спершу копія, потім стенд у вікні 18.11, перелік у звіті; видалення/RESTORE/закриті періоди — ні. (2) B155 — варіант 1 «переносити як є» (людина 07:35Z). (3) Акаунти test-/f13- у будь-якій БД дозволено (людина 07:45Z). (4) Порядок F13: batch-1b → batch-2 (B79 уже PASS) → B6 L8 на копії → batch-3 увечері.
- ⚠ AU: листи координатора до AU можуть не доходити (AU 15:30Z) — координатор дублює важливе сюди; AU читає цей файл на початку КОЖНОГО ходу.
- P-0117-8 [AU] 17.11 16:05Z: batch-1117-3 @f37099479 ЗБУДОВАНО НА batch-1117-2 (stacked; зливати ПІСЛЯ batch-2): L5 (Sql/05, D-544), B158d (AA), B166 (фікстури, прод — старий шлях), B158b (PE, D-546). F13: L5 — повна (rpt), решта легка. Повний CI іде.
- P-0117-9 [PE] 17.11 16:25Z: ПЕРЕД B155 p1 — 4 тести-прогалини L3 з F13 AU (частини дозволів → C_Mmm; гранти колонок; хеш плану від мапи місяців; часткові прив'язки → unmappedBindings). Один тестовий коміт на lane/pe/b5-l3 → SHA координатору → у batch-1117-3.
- P-0117-10 [AU] 17.11 16:50Z: B6 L8 GO на копії: рецепт = клон чернетки з 1.0.18 → -Layout YearWithMonthColumns -Stage YearMonths (+ L0b аліаси) → публікація 1.0.19 → перенос doc 96; AU виконує, перелік команд для вікна — на 10-хв огляд PE; land-owner з land-accounts.local.txt лише на копії (не друкувати); якщо без Template.Edit — test- адмін шаблонів (дозвіл людини 07:45Z). Звіт: .sync-local\reports\B6-L8-rehearsal.md.
- P-0117-11 [ALL] 17.11 17:05Z: ЗЛИТО #972 (batch-1b) і #973 (batch-2: L3, L7ab, B79, B158a/c, B161) → main 10af1ab30. batch-1117-3 @f37099479 чекає тестових комітів PE (L3, 4 прогалини) і AN (LD2) → додаю, перезапускаю CI → F13 AU. B6 L8 — на копії; GO-кандидат = main після batch-3.
- P-0117-12 [AU] 17.11 17:25Z: batch-1117-3 нова вершина @810496775 (+a5d28664f тести L3 PE, +810496775 тест LD2 AN); вердикт F13 — на 810496775; LD2 Deny-мутація GetSheetActivityHandler.cs:185. Листи до AU — через send_message.
- P-0117-13 [AU, PE] 17.11 17:35Z: людина (квіз) — «Так, тимчасово»: ЯКЩО перенос doc 96 вимагає, дозволено тимчасово відкрити ЗАКРИТІ періоди 01–09.2026 → перенести → одразу закрити назад; спершу копія, потім стенд у вікні 18.11; у звіт. PE: тест-прогалина L5b (читання прогону власника включає КОПІЇ аркушів, мутація oi.SheetCopyId IS NULL) → тестовий коміт у batch-1117-3.
- ⚠ P-0117-14 [AU] 17:40Z: ВІДПОВІДІ НА ТВОЇ ПИТАННЯ B6 L8 — у файлі .sync-local\ready\TO-AU-1117.md (рецепт 1.0.19 — так; land-owner на копії — так; Reopen і тимчасове відкриття закритих періодів 01–09 — дозволено людиною). GO.
- ⛔ P-0117-15 [PE, AU] 17:45Z: БЛОКЕР B6: Build-LandTemplate -Stage YearMonths лише план (застарілий затвор :297–303) → немає річної версії LAND_UI для переносу doc 96. PE: lane/pe/b5-ym-apply (зняти затвор, клон чернетки з 1.0.18 Layout=YearWithMonthColumns, застосувати план через API, публікація 1.0.19; ідемпотентно; Pester + реальний прогін на EcrLandPe), ціль READY ~23:00Z. AU: готує обв'язку L8 на EcrLandL8 (відновлення з COPY_ONLY, API з main :5197, скрипти dry-run/паритет/перф). Якщо не встигаємо до раннього 18.11 — перенос doc 96 випадає з вікна (код L3/L7/L5 інертний при Layout 0).
- P-0117-16 [AU] 17:50Z: PE — L3 не відмовляє на закритих періодах; лише sheetsLocked (на копії 0) → відкривати періоди 01–09 НЕ треба.
- P-0117-17 [AU, низький пріоритет] 19:20Z: B168 — read-only на live EcrLandUi: recovery_model_desc, log_reuse_wait_desc, розмір/зайнятість журналу (DBCC SQLPERF(LOGSPACE) або sys.dm_db_log_space_usage), останній бекап журналу (msdb.backupset type L). Відповідь — одним рядком координатору. Нічого не змінювати.
- ⛔ P-0117-18 [ALL] 19:35Z людина «так»: ЕКОНОМІЯ ДО ВІКНА (ліміт 79 %). AN: пауза B80 N1 і B162 (чистий стан, без нових агентів). AA: лише фікс batch-1117-3, B167 — пауза. PE і AU: повний темп тільки на критичному шляху (YearMonths-apply, F13, L8, batch-3). Сабагенти — лише критичний шлях. Після вікна — економний режим (рішення людини 14:30Z).
- P-0117-19 [AU] 19:50Z: batch-1117-3 нова вершина @1bdf7126c (+2 тестові коміти AA: застаріла передумова (b), арми B158b/d вузькі й коректні; продукт не змінено). Повний CI іде; вердикт F13 — легка дельта на 1bdf7126c. Ще очікується тест L5b від PE.
- P-0117-20 [AU] 20:10Z: batch-1117-3 вершина @8ab91fb77 (+тест L5b PE 0d753c270: копія аркуша не маршрутизується; 5 мутацій автора червоні). Легка дельта F13; злиття після зеленого CI.
- ⛔ P-0117-21 [ALL, ПОСТІЙНЕ] 20:15Z людина: «завжди оптимально використовувати токени». Контекст ≥40 % і немає критичного шляху → оновити .sync-local\RESUME-<сесія>.md (lane/SHA/worktree/БД/порти, токен міграції, паузи, дозволи людини, наступний крок) → /compact. Листи — збирати в один; сабагент ≤1 (opus лише безпека/дані/F13), журнали — лише хвіст і помилки, важкі прогони — на пачках.
- P-0117-22 [AU, PE] 20:30Z: людина — стиснути AU і PE вже зараз. На найближчій чистій точці (між кроками) записати .sync-local\RESUME-AU.md / RESUME-PE.md з усім живим станом (lane/SHA, БД/порти/PID, крок прогону + команда продовження, вердикти F13, слова людини, шлях до файлу пароля без значення), відповісти координатору «RESUME-… ready»; далі людина робить /compact.
- P-0117-23 [AU] 20:55Z: YearMonths-apply READY (b9067e578), у batch-1117-3 @35de42954. AU: F13 → збирач на EcrLandL8 → L8. Деталі: .sync-local\ready\TO-AU-1117.md.
- ⛔ P-0117-24 [ALL] 21:45Z координатор: AU L8 #3 (rpt-в'юхи річного LAND_UI > 1024 колонок, B170) → публікація LAND_UI 1.0.19 і перенос doc 96 ВИПАДАЮТЬ з вікна 18.11 (варіант b). Вікно: код main після batch-3, B151, B155, NTLM, постперевірка. PE: T8_00 доробити без нічного поспіху + аналіз B170 (варіанти форми в'юх, споживачі) → питання людині через координатора ДО коду. AU: паритет дорахувати, звіт B6-L8 зафіксувати, далі F13 batch-3 (ym-apply + 4d3070648/844455689) → GO-репетиція; RESUME-AU.md на чистій точці.
- P-0117-25 [ALL] 22:20Z: batch-1117-3 ЗЛИТО (#974, rc p412) → main = d440b6fe9 (L5, B158b/d, B166, YearMonths-apply + перенос прив'язок). Нові lane — від d440b6fe9. AU: GO-репетиція на d440b6fe9 (свіжий COPY_ONLY) → READY-FOR-GO. Наступний RC p413.
- P-0118-1 [PE, AU] 00:20Z людина (квіз): B170 — «Так, прибрати _Text»: лише для таблиць понад 1024 колонки у в'юсі не включати _Text-супутники МІСЯЧНИХ колонок; числа й немісячні _Text лишаються, одна в'юха на таблицю. D-547 (координатор). Land 1.0.19 + doc 96 повертаються у вікно 18.11 лише якщо T8_00 + B170 READY і повтор L8 PASS до 06:00Z.
- P-0118-2 [PE, AU] 00:35Z людина (квіз) УТОЧНЮЄ P-0118-1: B170 = A2 «У всіх річних» — у річному шаблоні (Layout 1) місячні розрахункові колонки НІКОЛИ не мають _Text у в'юхах (версійних і стабільних); немісячні _Text лишаються; одна в'юха на таблицю; плюс (a) пропуск-і-продовження 50422 як страховка. D-547.
- P-0118-3 [ALL] ретро 22:45Z, урок 444: репетиція на копії після першої відмови ПРОДОВЖУЄТЬСЯ діагностично (dry run наступних кроків, rpt-в'юхи, перерахунок) — усі блокери за один прохід; межі платформи (1024 колонки в'юхи, 1000 стабільної, CommandTimeout 60 с) рахувати в dry run ДО публікації; репетиція зміни форми шаблону — ≥24 год до вікна. Урок 445: варіанти поведінки — спершу лист власника з цифрами, потім квіз людині (через координатора).
- ⛔ P-0118-4 [ALL] 23:10Z GO W15w (AU) на d440b6fe9: K1, NTLM, run-w15w, postcheck, B155. До DONE від AU: жодного нового важкого IO на диску 0 (E:/F:), жодних restore/backup/стендів; збирання й тести — дозволено (CPU-затвор AU вирішує). Land 1.0.19/doc 96 — НЕ в цьому вікні.
- P-0118-5 [AU, PE] 23:55Z batch-1118-1 @bb4e84a18 (від main d440b6fe9): B170 rpt A2 (e61600f69) + L3 T8_00 якір (ec234e706, d58856ff0, bb4e84a18; КОНТРАКТ + СІД MIGR-nonmonth-conflict-refusal). Повний CI іде. AU: ПІСЛЯ DONE W15w — F13 пачки → повтор L8 на EcrLandL8 (збирач → dry run → apply → паритет → rpt-в'юхи 83/83) → окреме вікно Land.
- P-0118-6 [ALL] ~00:00Z: W15w DONE на live (d440b6fe9, міграції 117→123, B151, B155, NTLM; постперевірка чиста). Тиша/пауза IO ЗНЯТА. AU: F13 batch-1118-1 @bb4e84a18 → повтор L8. Наступне злиття RC p413.
- P-0118-7 [ALL] 00:20Z урок 446: lane з клієнтськими змінами — перед READY 
pm run build + 
ode scripts/check-bundle-budget.mjs (≤ 250 КБ gzip на маршрут, D-132), рядок «bundle: …» у READY. batch-1118-1 червоний саме на цьому (DocumentPage 250.2) — фікс PE.
- P-0118-8 [AU, AN, AA] 00:55Z batch-1118-2 @beaf1ea8c (від d440b6fe9, незалежна від 1118-1): B164 + B80 N0 + B80 N1 (МІГРАЦІЯ + КОНТРАКТ + СІД; D-548). Повний CI іде. AU: F13 після зеленого CI (Deny-рядок — у READY AN b80-n1). Слот диска 0: AN N1 перф ~40 хв (00:55–01:35Z) — AU без важкого IO на E:/F: до звіту AN. Токен міграції → PE (B155 p1).
- P-0118-9 [AU] 01:10Z batch-1118-2 → @31d1e2f81 (+ D-132 фікс бюджету f20b582c8 + B162 2c80532cd); batch-1118-1 → @b5a8c3559 (+ той самий фікс бюджету; main d440b6fe9 сам був 250.2 КБ). Слот диска 0 від AN ЗВІЛЬНЕНО (01:03Z). F13 обох — після зеленого CI.
- P-0118-10 [ALL] ретро 02:50Z урок 447: зміна сіду прав/ключів/каталогів (Permission, UiString, ErrorCodes) → повний vitest + Architecture перед READY навіть без клієнтського коду. Урок 448: вказівки координатора про поведінку — перевіряти на коді й заперечувати ДО коду (як AN з L7c-2).
- P-0118-11 [ALL] 04:30Z людина: «трохи прискорся». Пачки пушаться одразу, як є готове (не чекати наступного коміту заради одного CI); листи коротші; без простою між вердиктами. Вершини: 1118-1 @a5bf2a88d, 1118-2 @bf977d33a (зелений, дельта F13 AU), 1118-3 @8d6de1f3f.
- P-0118-12 [ALL] 05:00Z batch-1118-2 ЗЛИТО #975 (p413) → main = da7c2a601 (B80 N0/N1 + міграція, B162, B164, L7c/L7c-2, D-132 бюджет). PE: B155 p1 — rebase на da7c2a601 (токен міграції в PE). Наступний RC p414.
- ⛔ P-0118-13 [AU, PE, ALL] 04:50Z людина: вікно річного Land «давай сьогодні ввечері» (W15x, 18.11 ввечері, ~17:00Z+; виняток з ≤1/добу). Порядок: злиття batch-1118-1 (p414) → (B167 якщо встигне) → репетиція AU на свіжому COPY_ONLY від фінального main → READY-FOR-GO → GO координатора. Обсяг: збирач (+CommandTimeout override B169) → 1.0.19 → doc 96 → перерахунок → rpt → постперевірка.
- ⛔ P-0118-14 [AU] 05:00Z людина: RESTORE live ДОЗВОЛЕНО лише на W15x і лише при збої кроку даних Land (бекап перед вікном) → звіт. Акаунт test-w15x-landbuilder на live — ТАК (дозвіл 17.11 07:45Z), випадковий пароль ніде не друкувати, ролі як land-owner, вимкнути після вікна, у звіті. Права наявних людей не чіпати.
- P-0118-15 [ALL] 05:15Z batch-1118-1 ЗЛИТО #976 (p414) → main = 2dee84da2 (річний Land повністю). W15x — від 2dee84da2. Людина: стиснути контексти сесій зараз — RESUME-<сесія>.md на чистій точці + «RESUME-… ready».
- P-0118-16 [ALL] 05:35Z batch-1118-4 @33e67090c = B155 p1 сервер (PE, МІГРАЦІЯ 20261118044516 + КОНТРАКТ ApprovalRoutes); клієнт AA — у ту ж пачку. Токен міграції — у координатора (на запит). AU: F13 після зеленого CI, після W15x. PE далі B165(1).
- P-0118-17 [ALL] 06:50Z batch-1118-3 (B167) ЗЛИТО #977 (p415) → main = aaa55e225. W15x ЛИШАЄТЬСЯ на 2dee84da2 (репетиція йде саме на ньому); B167 — у наступне вікно. Відкрита пачка: 1118-4 @f2369eeec (B155 p1 сервер, B171, docs D-539…D-548, B165(1) D-549). Наступний RC p416.
- P-0118-18 [ALL] ретро 07:00Z, урок 451: тихі слоти CPU для репетицій/вікон оголошуються заздалегідь рядком тут; під час «⛔ QUIET CPU до HH:MMZ» — жодних dotnet build/test/vitest (код писати можна). Вечірнє вікно W15x: QUIET CPU буде оголошено окремо.
- P-0118-19 [ALL] 07:45Z QUIET CPU ЗНЯТО (репетиція W15x PASS). ⛔ ЗАПЛАНОВАНО: QUIET CPU + диск 0 18.11 16:45Z–19:00Z (вікно W15x на live, GO координатора о 17:00Z) — жодних dotnet build/test/vitest, restore/backup/стендів; код писати можна. AA: e2e B155 на F: — завершити до 16:00Z.
- P-0118-20 [ALL] 09:10Z batch-1118-4 ЗЛИТО #978 (p416) → main = 6cf34eaa1 (B155 p1 сервер+клієнт з міграцією, B165, B169, B171, B172, docs). W15x лишається на 2dee84da2. Нове: B173 (дедуп перед UQ_AR_Scope, PE, після W15x). Тижневий ліміт 88 % — нових задач поза планом не брати.

- P-0118-21 (11:05Z): #979 batch-1118-5 merged (p417, main 8c138a0e0): B155 names + B173 preflight 50155. W15x unchanged: 2dee84da2, quiet 16:45–19:00Z.

- P-0118-22 (11:50Z, human «я дивлюся ти простоюєш»): economy pause lifted for AN/PE/AA — each takes top open row in own PLAN column; no methodology UI/model, nothing on the stand, heavy local runs stop by 16:40Z; AU stays on W15x.

- P-0118-23 (12:10Z, human quiz B26): Volume for ECW_C09_02_01 in T7b_00 = NEW manual-entry monthly column in T7b_00 (option a). PE codes it in the Land builder (no methodology model change); stand no earlier than the window after W15x.

- P-0118-24 (~11:55Z, human quiz): B26 CHANGED — Volume for ECW_C09_02_01 comes from T7b_00 row S3_9 (Water Report volume) of the same month, one value for all substance rows; NO manual Volume_Mmm columns. Supersedes P-0118-23 option (a).

- P-0118-25 (~12:05Z, human quiz): B26 S3_9 exclusion = option A, row activity filter D-425 (hidden status column in T7b_00, substance rows Running, S3_9 empty; runningOnly flag on the methodology CONTAINER; version/rules untouched); builder fills status for existing 7b instances.

- P-0118-26 (13:25Z): local FULL vitest runs use --maxWorkers=50% (load caused 1–4 timeouts per full run today; AA B145 data). CI unchanged. New load-only timeouts (AppLayout.densityMenuRemount, RegistryConstructorPage.tabs, DocumentInspector.issuesLoad) → PLAN B174, not per-test limits first.

- P-0118-27 (13:45Z, human quiz): B26 ships as built (visible Land_Status column; runbook rule: run -Stage T7bStatus after creating any Land document) + follow-up B175: per-fixed-row default cell values in templates, to remove the manual step.

- P-0118-28 (14:45Z, retro 455): before any question to the human of the form «where does data X come from», first scan the target table/template for existing rows/columns with the same meaning and list them as quiz options (B26 was built twice because row S3_9 already held the volume).

- P-0118-29 (16:58Z): W15x GO sent to AU on 2dee84da2. WINDOW ACTIVE; quiet CPU+disk until DONE.

- P-0118-30 (17:47Z): W15x DONE on live 2dee84da2 (LAND_UI 1.0.19 Id 22, doc 96 moved to the yearly template, parity 510/510, no RESTORE, builder account disabled, CommandTimeout removed). QUIET SLOT ENDED. Next: AU F13 batch-1118-6; AN B80 N4; PE drop EcrLandPe/:5290 then B175; AA next own PLAN row. K1 re-baseline on live before the next window (PLAN B177).

- P-0118-31 (~18:20Z, human quiz after my live test): dependencies are AUTOMATIC. The system itself finds who reads changed data (parsed formulas of templates + methodologies, incl. registries filled from a source document; no text scan, no manual config) and recalculates in the right order (source first, then consumers). Admin gets a READ-ONLY screen of dependencies/order + the any/approved-only switch. PLAN B176 (expanded). Design to the human before code.

- P-0118-32 (18:30Z, human): «давай на повній до 98 потім передача справ» — FULL SPEED until weekly usage 98 %; at 98 %: all sessions push everything (WIP to lanes), write RESUME/handover, stop. Replaces the 96 % threshold for this week.

- P-0118-33 (~19:00Z, human quiz): B179 design APPROVED with defaults (Q1 trigger on every source cell edit at Any = yes; Q2 per-binding kill switch = yes; Q3 refuse cycles; Q4 screen needs Calculation.ManageSourceBinding; Q5 drop consumer table after one release; Q6 FLERT not automatic yet; Q7 no live stop-gap flag; Q8 manual registry edits stay manual). Order L1 graph → L2 fan-out + acceptance (fixes live) → L3 cycles → L4 API → L5 screen → L6 cleanup; L7 later. Design: reports\B179-auto-dependencies-design-1118.md.

- P-0118-34 (19:10Z, retro 457): every «A affects B» feature gets an END-TO-END scenario in F13 and the window post-check (change A through the normal user path → B updates with no manual step) + check that the shipped config defaults match the feature intent. B80 N1 passed F13/W15x yet did nothing on live.

- P-0118-35 (20:05Z): #980 batch-1118-6 merged (p418, main 9063a4a1a): B80 N2a/N2b/N3b, B26, B114, B145. AU F13 PASS; follow-up test gap B181 (PE). Next RC p419. batch-1118-7 to be cut from 9063a4a1a: B178, N6-A(+), N4, B179 L1, B175(-r), B181.

- P-0118-36 (~21:50Z): #981 batch-1118-7 merged (p419, main 3dbc74fe2): B80 N4/N6 A-C, B175 (migration 20261118175617; token with coordinator), B179 L1, B178, B181. AU F13 PASS; B182 (AN tests) open. Next RC p420. Pending: AN B179 L2 (live fix).

- P-0119-1 (~23:00Z, retro 459/460): (a) every end-to-end scenario in a READY/F13 brief gives exact coordinates: document / sheet / table / row / month / old→new / expected consumer value; (b) when a READY deletes or rewrites test classes, add a table «removed test → named replacement»; merge-tree against fresh main before every push.

- P-0119-2 (~23:35Z, human quiz): B184 = YES for SOURCE templates only — a cell edit in a document whose template is a source of an enabled binding auto-runs that document's own methodology recalculation for the month (coalesced), then Land; other templates unchanged (manual Recalculate).

- P-0119-3 (05:10Z, retro 461): any change in the hot job path (FormulaRecalculationJob, RecalculationJob, PATCH cells) → run *FixedCost*/*QueryCount* tests locally before READY; a new query in the hot path only via the revision-keyed cache. Usage 97 % — handover at 98 % (P-0118-32): be ready to push WIP + RESUME.

- P-0119-4 (05:45Z, human): «вже 98 коли буде 99 всі сесії мають зупинитися і підготувати хандовер і запушити все а ти їх зібрати і підготувати один для іншого клоду». Supersedes P-0118-32 threshold: work continues until 99 %; at 99 % every session STOPS, pushes all (WIP to *-wip lanes), writes .sync-local\HANDOVER-<session>-1119.md (lanes+SHAs, READY/WIP, open questions, worktrees, tokens). Coordinator merges them into ONE .sync-local\HANDOVER-ALL-1119.md for another Claude.

- P-0119-5 (~07:40Z): #982 batch-1119-1 merged (p420, main a6f49601c): B179 L2 (+B185), L2b, L3, B182, B183. Live HSE400→Land fix is in main (deploy next window). Next RC p421. No open batch.

- P-0119-6 (08:40Z): weekly usage 99 % → HANDOVER executed (P-0119-4). All sessions STOP. Merged handover: .sync-local\HANDOVER-ALL-1119.md. Resume after 22.11 15:00Z or by another Claude from that file.

- P-0119-7 (~09:50Z): #983 p421 merged (main dc8fb5d69): coordinator handover in git as docs/build/HANDOVER-coordinator-1119.md (sanitized copy). Next RC p422. Flaky B187 (DuplicateRequestHttpTests route replace) added to PLAN.

- P-1122-1 (human 22.11): stand window after b8; the coordinator decides WHEN the stand is needed (depends on batch readiness) — standing word for windows tied to ready batches. RESTORE of live still needs a separate word. Rules (memory human-work-rules-tokens-models-batches-stand-1122): model by complexity, batches, own stand verification with evidence, no side effects/debts.

- P-1122-2 (human quiz 22.11): F5 = REFUSE submit/approve of a consumer month while it is pending in a source-cascade job (clear ru/kz/en message «йде перерахунок після зміни джерела»); Reject/Recall/Reopen not blocked.

- P-1122-3 (human quiz 22.11): F7 own-table script readers = record the script's read columns at methodology publish (methodology model change, additive, minimal; D-568). F6: no gap (formula engine never reads across year boundary).

- P-1122-4 (human 22.11, verbatim «Так, F5-R у b10 - так познач щоб користуач бачив це»): F5-R goes into b10 — for Any bindings, flag a consumer month approved while its source changed (race in D-567(6)), like B80 N4, VISIBLE to the user in the UI (sheet/month + document list) with a hint to review/reopen/recalc with approved (B80 N5). Also cover or rule out the in-memory Quartz queue.

- P-1123-1 (human 23.11, verbatim «зроби повний UI перевірку функціоналу імпорту NAE … кастомні колонки які я додаю, в моєму прикладі Цех, не додаються … як буде вести система коли 2 таблиці параметрів з різними NAE будуть завантажуватися я боюся наявності старих даних або дублікатів … загальне правило одна остання завантажена таблиця актуальна»): permit parameters import (Import parameters…, permit KZ06VCZ14825472 example, test file C:\Users\Babyc\Downloads\401\7) Параметры_2024_final_Test.xlsx). RULE: the LAST uploaded parameters table is the only current one for the permit — re-import replaces the permit's sources/parameters entirely (no stale rows, no duplicates), across different NAE too. Custom columns (e.g. «Цех») from the file must be imported. Full UI check with evidence.

- P-1123-2 (human 23.11, verbatim «Давай оновлення стенду після всіх правок що все протестувати за раз»): ONE live window (W15z) after b9 + b10 + NAE import are all merged; rehearse on the final main SHA; then one full own verification on live (HSE400→Land, F5 refusal, F5-R flag, 7b Volume/Land_Status, NAE import «Цех» + replace + preview, Windows login, 0 Failed).

- P-1123-3 (human 23.11, verbatim «загалом наступне оновлення стенду має включати і оновлений редактор з міграцією існуючих»): W15z also includes the methodology script editor (already deployed, 0 script versions on live) + MIGRATION of the existing live methodologies to scripts, with parity gates (results identical), rollback, and own UI check of the editor. Lane lane/integ-ms-migrate (D-571).

- P-1123-4 (human quiz 23.11, verbatim):
  - 36 versions without live data — «Так за еталоном, в БД ecr вже є еталонні дані ти можеш взяти для кожної методологія тестові дані з неї щоб протестувати». Switch them in W15z; parity on test data taken from the ECR database, read-only (never drop or modify ECR).
  - Units C05_05/C11_07 — «Так, підтверджую»: Density kg/Sm3, FuelLowerHeating MJ/Sm3, Qr MJ/kg, the rest dimensionless. Set them on the columns in Land 1.0.20.0, and remove the script substitution.
  - Water ECW_C09_02_01 — «Вода має прості формули тому ми їх рахуємо в таблиці і все». Water is computed by table formulas; no methodology migration.
  - C11_10 34 combinations + golden references — «Подивися в БД ecr». Take the rules and references from the ECR database.

- P-1123-5 (human 23.11, verbatim «Це наближає нас до межі - це є сигналом поганого дизайну!» + quiz «подумай та надай найкраще та продуктивне рішення»): coordinator decision — rpt views redesigned to MONTH-AS-ROW (X_M01..M12 → one column X + Month), ~55–60 columns instead of ~700; linear set-based generation; all consumers migrated in the same lane; wide layouts only by pivot in the consumer's own query. D-573, lane/integ-rpt-views. Into W15z. ✎ AMENDED by a human quiz 23.11, «Додати вузькі поряд». D-53 forbids breaking rpt.* (the customer's SSRS reads the wide views), so it is ADDITIVE:
- the wide views stay byte-identical, with linear generation: 1036 columns went from 16.6 s to 1.5 s;
- a month-as-row `…_m` view is added next to each one;
- the quadratic cfg.ColumnDef trigger is fixed in the same lane.

- P-1123-6 (human quiz 23.11, verbatim):
  - The 8 methodology versions without data in ECR or on live — «створи еталони і мігруй». Build synthetic reference inputs that cover every branch; the formula engine gives the expected results; Script must equal Formulas; golden sets go into new versions; switch them in W15z.
  - C11_10 V4 with the 28 rules from ECR — «Так, як у старій системі». Publish it in W15z; after the nightly recalculation the coordinator compares the new rows against ECR.

- **P-1123-7** (людина 23.11 ~16:25 за Києвом), дослівно: «добре, прискорся і онови стенд як все буде готово». Це постійне слово на вікно W15z одразу, як усі lane готові: репетиція → GO → моя перевірка, з точним кейсом NAE «Цех». RESTORE — як і раніше, лише окремим словом людини.

- **P-1123-8** (людина 23.11 ~16:30), дослівно: «нічне вікно 01:00–05:00 не оновлюю - оновлюй я дозволяю». Нічна заборона знята. Рішення координатора: технічно тримаю вільним лише проміжок нічних задач live — 02:15 (перевірки) і 03:30 (нічний перерахунок), доки перерахунок не завершиться. Причина: вікно має строгу звірку даних до/після (datasnap) і фазу перемикання методологій, а нічний перерахунок змінює дані calc посеред вікна. Отже вікно має закінчитись до ~02:10 або стартувати після завершення нічного перерахунку (~04:00), а не з 05:00.

- **P-1123-9** (людина 23.11 ~18:00, квіз «Land 12.2026»): обрано «Не рахувати (Рекомендовано)». Місяці, які ще не відкрито (Scheduled), не рахуються: комірки порожні й без помилок, доки місяць не відкриють. Після відкриття діє D-506. Номер рішення — D-575.

- **P-1123-10** (людина 23.11 ~19:00, квіз «Розрахунки»): обрано «Лишити посилання «All months»». Панель «Результати розрахунку» для документів із річною розкладкою не повертаємо; результати за рік відкриваються за посиланням «All months» (LAND-YEAR L7a без змін).

- **P-1123-11** (людина 23.11 ~19:20, квіз «B74»): обрано «Окремо, наступним вікном (Рекомендовано)». B74 P3 (шляхи запису) + P4 (UI: вкладка «Шаблони» проєкту, крок «Шаблон» у New document, код документа з кодом шаблону) іде окремою lane вже зараз; W15z — за планом; B74 — наступним вікном. Перед цим людина: «Так, перевір на TEST_COORD_2026 і я не побачив в UI цього». Мій промах: раніше сказав, що B74 на стенді, а там лише P1/P2.

- **P-1123-12** (людина 23.11 ~20:20, лист «скіл повного циклу»), дослівно: «мені потрібний повноцінний скіл яки закриє всі розробку». Скіл `ecr-subtask-gates` v3 (`.sync-local\gates\`) встановлено; працюю за картою етапів E0–E10.
- **P-1123-13**, дослівно: «так» — переробити скіл зі скриптами.
- **P-1123-14**, дослівно: «це залежить від навантаження гітхаба або локального пк». Де ганяти важкі прогони, вирішує `route.py`.
- **P-1123-15**, дослівно: «Приймі рішення на основі діяльності координатора». Контур CI гілок: R3 → `lane/integ-*`, R1/R2 → `lane/<напрям>/<задача>`, але лише після передумови з ref\02: швидкий контур рахує базу від origin/main. Доти всі гілки йдуть як `lane/integ-*`.

- **P-1123-16** (людина 23.11 ~22:40, на питання «навіщо репетиція, чи не простіше бекап + відновлення»), дослівно: «продовжуй як раніше». Вікно W15z: репетиція на копії паралельно з CI пачки → бекап → GO → перевірка. Слова на RESTORE при збої НЕ дано — відновлення live лише окремим словом людини.
- **P-1123-17** (людина 23.11 ~22:55), дослівно: «коли будеш тестувати пройди всі можливі варіанти з надсиланням апрувом і перерахунком або зміною 400 щоб не було жодних багів». Матриця M1–M13 у `reports\W15z-window-plan.md`: обов'язкова на репетиції і на live; FAIL блокує GO.
- **P-1123-18** (людина 24.11 ~01:20, на питання про старі результати методологій у Scheduled-місяцях, видимі в комірках), дослівно: «прибрати їх одразу». Реалізація — у продукті (щогодинне прибирання PeriodStateJob поряд із FormulaError, D-575) у пачці b11, щоб на live прибралось одразу після старту, з тестом; доказ на live після вікна.
- **P-1123-19** (людина 24.11 ~12:00), дослівно: «чому Наступним оновленням піде B74 якщо в тебе був час на нього стенд онови разом з ним». Замінює P-1123-11: B74 P3+P4 іде в W15z разом із b11. AC-0 B74 (другий шаблон у проєкті → New document → крок Template) — на репетиції і на live.
- **P-1123-20** (людина 24.11 ~15:40, квіз), на питання: позначка F5-R не з'являється при змінах джерела без правки комірок (результати методологій, копії аркушів, видалення рядків, поля шапки) — «Зараз, у цьому оновленні». Виправлення — у пачку b11 перед вікном W15z.

- P-1123-21 (24.11, квіз): старі погодження без збереженого стану джерел (до міграції P112320) — «A: позначати всі»: NULL-стан = «джерело змінилось» (F5-R інформаційна, знімається перерахунком і повторним погодженням).
- P-1124-1 (24.11, людина, дослівно): «загалом мене цікавить реальний перехід а не сідити на 2 стульях має бути тільки наш новий редакор» — методології: лише новий редактор скриптів; формульні версії не як робочі; нові версії лише Script; залишки (вода ECW_C09_02_01, Common, test-чернетка) розібрати.

## P-1124-2 (24.11 ~20:25, людина): перенесення на нове представлення БЕЗ ВТРАТ — жодної формульної версії методологій; кожен минулий розрахунок має зв'язок зі скриптовою версією з доведеним паритетом. Факти live: 78 формульних версій (46 методологій; без скрипта — Common і вода ECW_C09_02_01), 296 результатів на 29 формульних версіях, 61 знімок подання. Фаза 1 (дизайн + паритет на копії EcrP11242Scratch) — агент opus; далі код lane/integ-p11242-ms-lossless.
- P-1124-2 + (24.11 людина): вода ECW_C09_02_01 — РОЗВ'ЯЗАТИ, виняток прибрати; одиниці точні, паритет обов'язковий.

## P-1124-3 (24.11, людина, квіз «Прибрати повністю»): «Report snapshots» (зрізи вбудованого конструктора, D-52a) прибрати — меню, сторінки, API, фонова побудова, XLSX; звітність лише SSRS через сирий шар rpt.*. Таблиці rpt.ReportSnapshot/Row/Def/Version і в'юхи v_<Report>_v<N> — не дропати в цій lane (D-53), окремо словом людини. Lane lane/integ-rm-report-snapshots.

## P-1124-4 (24.11 18:20 Київ, людина): аудит — переглянути всі колонки, час у МІСЦЕВОМУ часі глядача (скасовує debt-1110 для журналів аудиту), перевірити що фільтри реально працюють. Lane lane/integ-audit-ux.
- P-1125-SG (людина 25.11, квіз до DESIGN-sg-flaring.md): склад газу → методології факелів: усі три разом (Int SG, Cont SG, Thermal oxidizer); події факелів — з PI AF event frames; TOx EOR — ВИПРАВИТИ (склад EOR, не SOR; відома різниця в паритеті); решта — дефолти: подія на межі версій → версія на початок події; стовпці TOx → ID потоку (417→575); сезон Apr–Sep=Summer; розподіл 19/21/146 — довідник, редагує адмін; старі формули через міст GCV, REGSUM пізніше. Раніше: «Подивися БД ecr та сорс дані і ассемблі …»
- P-1125-SG2 (людина 25.11 «1. виправи 2. ок»): L6 Train 1 → потік за фактичним поїздом (виправити старий дефект, відома різниця паритету); doc 100 лишається на live до виправлення видалення документа із закритим періодом (картка land-doc-delete-closed-period), січень не відкриваю.
- P-1125-SG3 (людина 25.11, квіз до rev 2): Q-A HSE372 Excel як резерв — так; Q-B «для факелів ми беремо дані з аф і для кожного рядку рахуємо» → склад газу за датою КОЖНОГО рядка-події (не обмеження «місячний шаблон»; знадобиться читання реєстру на дату рядка); Q-C «відхилено + причина» — так.
- P-1125-ONEPROJ (людина 25.11): «загалом має бути один проект в якому всі шаблони і 3 документи ланд 400 401 і твій тестовий ленд» → LANDUI2026 = doc 96 Land, 98 HSE400, 93 HSE401, 100 тест Land; решту проєктів (HSE400_2026, HSE401_2026, TEST_COORD_2026, HSE400_GOLD) видалити після перенесення. Перенесення документа між проєктами в продукті немає → картка integ-document-move (R3).

## P-1125-B14 (людина 25.11 «починай b14»)
b14 стартує зараз (до скидання ліміту). Лінії: documents-board-yearly + doc-notices-compact (UI документа, один агент послідовно), number-format-consistent, source-test-flert, document-move (R3: спершу DESIGN + незалежний огляд). База d75032896. На 98 % тижневого — стоп, пуш WIP, HANDOVER.


## P-1126-SRCCRED (людина 26.11)
Скрини New connection PiSqlClient + «PI SQL Client ODBC Login» (WIN-ABAANR1OQJE\Administrator, Test connection succeeded): «мені також потрібно мати підключення по юзеру та паролю чи можу я ввести в строку?» → «б» → «потрібно і вин та по ористувачу вхід». Картка integ-source-credentials (R3, DESIGN до брифу), після 29.11 або після аудиту, якщо ліміт дозволить.

## P-1126-AUDCLR (людина 26.11)
«Clear filters» в аудиті: «має очищати все разом» (Document, дати, Row key, Column, Origin, By user, Late — на всіх вкладках) → AC-9 картки integ-audit-filters-live.

## P-1126-STOP99 (людина 26.11)
«на 99 % зупині агента» — поріг зупинки агентів 99 % (не 98 %) на цей тиждень; на 99 %: стоп, пуш WIP, оновити HANDOVER-ALL-20261125 і гілку handover.

- P-1126-DPKEYS (26.11 09:42) людина: «Захист ключів шифрування - достатньо, що паролі джерел шифруються ключами» → integ-source-credentials без сертифіката DataProtection на live.
- P-1126-AFLOCK (26.11 09:52) людина: поріг блокування AF «десь 20 спроб»; Q-6 «ок» (ключі ODBC — на AC-0).
- P-1126-AUDAPI (26.11 10:28) людина: «три доробки аудиту потребують зміни API - зроби зараз» → integ-audit-filters-contract (AC-7 логін лише при збігу імен, AC-8 колонки з сервера з тими ж правами видимості, AC-13 Row N).