# req-closure-yellow-3: усі 🟨 Додатка G, звірені з кодом (02.10)

ID черги: `req-closure-yellow-3`. Джерело: `REQ-CLOSURE-2026-10-01.md` (Додаток G, 77 рядків зі знаком 🟨 / «реалізація 🟨»), попередня часткова звірка `req-closure-5-c-2026-10-01.md`.
Звірено з `origin/dev/integration` @ `d649921f` (після #471: SMTP і сповіщення, розріз доступу, залежності розкладів, ECR230, AN-15, RT-24).
Метод: читання коду й тестів (`git grep`, `git log --grep`), vitest лише для змінених клієнтських файлів. `dotnet` у хмарній сесії недоступний: .NET-тести не запускались, трейти перевіряє CI (`Ecr.Architecture.Tests`).
Критерій ✅ — як у реєстрі: реалізація є **і** є тест, що впаде при поломці суті; трейт сам по собі закриттям не вважається.

## 1. Підсумок

| | Кількість |
|---|---:|
| 🟨 на вході | 77 |
| **🟨 → ✅** | **19** |
| лишаються 🟨 | 58 |
| — чекає людини (D-запис / рішення) | 24 |
| — чекає замовника / заміру на його обладнанні | 20 (з них НФ-8.3.1 — ➖) |
| — потребує міграції | 4 |
| — велике | 7 |
| — мала серверна доробка без міграції (для сесії з `dotnet`) | 2 |
| — у роботі «Аналізу» (не чіпав) | 1 (ФВ-8.12) |

Перепис трасування після цієї гілки: **259 · покрито 233 · звільнено 26 · непокрито 0** (було 232 · 27): ФВ-16.12 отримала трейти, рядок `trace-exempt` знято. `docs/build/roadmap.md` оновлено (сторож `JournalIntegrityTests`).

## 2. Що змінено в гілці `lane/cloud/req-closure-yellow-3`

Без міграцій, без `Sql/*.sql`, без контракту, без `shared/ui`; бандли DocumentPage/PeriodsPage не зачеплені (зміни лише в тестах і документах).

| Що | Файли |
|---|---|
| Трейт `ФВ-5.20a` на «Reopen з обов'язковою причиною» | `SubmitApproveTests.cs` |
| Трейт `ФВ-6.5` на тест PBKDF2-SHA512 / 200 000 ітерацій (D-218, підтверджено людиною) | `PasswordHasherTests.cs` |
| Трейт `ФВ-3.1` на генерацію `BusinessKey` після гонки | `DocumentDuplicateKeyRaceTests.cs` |
| Трейт `ФВ-3.5` перенесено з мертвої гілки `IsCrossSheet` (`D-227`) на справжнє відкладення — черга зі злиттям | `FormulaRecalculationCoalesceTests.cs` (+2), `RecalculationServiceTests.cs` (−1, коментар) |
| Трейт `ФВ-4.5` на наскрізний фоновий імпорт | `BackgroundImportScenarios.cs` |
| Трейт `ФВ-2.12` на рантайм `Rollup`/`Check` | `RelationRollupRecalculationTests.cs`, `SubmitRelationCheckTests.cs` (×2) |
| Трейт `ФВ-14.7` на причину зміни довідника й методології | `RegistryDefinitionTests.cs`, `SwitchRegistrySourceTests.cs`, `MethodologyVersionTests.cs` |
| Трейт `ФВ-14.10` на сторож «клієнтські переходи = домен» | `PrincipleTests.cs` |
| Трейт `ФВ-16.12` (×4) на «нерозпізнана одиниця — у звіт»; рядок `trace-exempt` знято | `StructureWorkbookReaderTests.cs`, `UnitRecognizerTests.cs`, `contracts/trace-exempt.md` |
| Трейт `ФВ-12.5` на явний перелік адресатів і доставку SMTP | `SmtpAdminSettingsTests.cs`, `NotificationDispatcherTests.cs` |
| `trace-exempt` ФВ-12.7: дописано `ecr.job.run.duration` | `contracts/trace-exempt.md` |
| vitest-назви з ID: `ФВ-3.3`/`ФВ-14.4` (fill handle, undo протягування), `ФВ-14.4` (віртуалізація), `ФВ-3.1` (створення документа), `ФВ-6.16` (×2), `ФВ-10.8` | `DocumentGrid.fillHandle`, `virtualization.guard`, `CreateDocumentModal.structureError`, `EffectiveAccessPanel.template`, `HealthPage.checkNames` |
| **Дефект знайдено й виправлено:** два тестові файли були в подвійному UTF-8 (кракозябри в назвах `it`, коментарях і очікуваному тексті) — перекодовано, поведінка тестів та сама | `EffectiveAccessPanel.test.tsx`, `PeriodsPage.archiveGuard.test.tsx` |
| `D-220` (ФВ-6.16): позначка «виконано» з доказами; сума в `docs/CHECKSUMS.txt` оновлена | `docs/tz/10-decisions.md`, `docs/CHECKSUMS.txt` |

Перевірка: vitest змінених файлів — 7 файлів, 22/22 зелені. Мутація (лише локально, не в коміті): `isRangeEdit` → `return false` у `rangeEdit.ts` — 4/4 тести `DocumentGrid.fillHandle` червоні; відкат — зелені.

## 3. 🟨 → ✅ (19)

| Вимога | Доказ (реалізація · тест) | Застереження |
|---|---|---|
| ФВ-2.6 | Перестановка колонок і рядків (`TemplateVersionPage.tsx:530`, `reorder.ts`; AN-15 `dfc1946`), перегляд таблиці (`TablePreview.tsx`), редактори формул/правил · `PresentationRowOrderApiTests` (5, трейт), `PatchPresentationTests:378,405,429`, `TemplateVersionPage.reorder.test.tsx` | — |
| ФВ-2.7 | Ширини `ColumnDef.WidthPx` (міграція `D234ColumnWidthPx`), стилі `StyleMapper.cs:67-92`, умовне форматування · `ColumnWidthTests`, `StyleDefTests`, `ConditionalFormatsApiTests/HttpTests` (трейт) | Excel бере ширину за вмістом, не `WidthPx` (`ExcelExporter.cs:269-291`); Excel вручну не відкривали |
| ФВ-3.1 | `CreateDocumentHandler.cs:162-166` (ключ генерує система; ТЗ ключа зі значень колонок не вимагає) · `DocumentDuplicateKeyRaceTests` (трейт додано), `CreateDocumentHandlerTests:86`, vitest `ФВ-3.1:` | — |
| ФВ-3.3 | Діапазон, вставка, копіювання, undo/redo, fill handle (`rangeEdit.ts`) · vitest `ФВ-3.3:` у `keyboardRedo`, `pasteAnchor`, `rangeSelection`, `clipboard`, `fillHandle` (мутація 4/4) | Навігація клавіатурою — вбудована в RevoGrid; юніт-тести сітки на заглушці RevoGrid |
| ФВ-3.5 | Відкладення робить черга зі злиттям `EnqueueCoalescedAsync<IFormulaRecalculationJob>` (`PatchCellsHandler.cs:2449`) · `FormulaRecalculationCoalesceTests:43,63` (трейт додано) | Прапорець `IsCrossSheet` мертвий — прибрати разом з міграцією (`D-227`, борг) |
| ФВ-5.19 | `SubmitSheetHandler.cs:461-542` · Error: `Блокувальна_помилка_валідації_відхиляє_подання` (`:664`), Info: `Інформативне_повідомлення_…_не_блокує` (`:752`), Warning `:766…:851` — усі з трейтом | Пункти реєстру «без трейта» й «Info без тесту» застаріли |
| ФВ-5.20a | `Reopen_документа_при_закритому_періоді_…` (`:1268`), `Старий_поданий_зріз_лишається_…` (`:1322`), `Reopen_документа_повертає_аркуш_у_Draft_із_обовязковою_причиною` (трейт додано), `ReopenRaceTests`, `PatchCellsLateEditAfterReopenTests` | — |
| ФВ-6.5 | `PasswordHasher.cs:20,36` (PBKDF2-HMAC-SHA512, 200 000) · `PasswordHasherTests.D218_…` (трейт додано) | Відхід від тексту ТЗ підтверджено людиною (`D-218`, `D-249`) |
| ФВ-6.16 | `EffectiveAccess.cs:115,256-264` (аркуш/таблиця/колонка, `inheritedFrom`, `caveat`) · `EffectiveAccessInheritanceHttpTests` (4, трейт), `EffectiveAccessHttpTests`, vitest `ФВ-6.16:` | Стан документа й звуження періодом не враховуються (`caveat`); `D-220` оновлено |
| ФВ-10.8 | Операційний дашборд `HealthPage.tsx` · `HealthTests.Health_ready_зелений_і_перелік_перевірок_повний:252` (трейт), vitest `ФВ-10.8:` | Окремої заміни `DataVolume_Trend` не знайдено |
| ФВ-11.9 | `LayerRulesTests.Правило_3_…:49`, `Правило_3а_…:66` (трейт) | — |
| ФВ-12.5 | Алерти в зведенні (`NotificationJob.cs:228`), SMTP і адресати налаштовуються в системі (`D-263`) · `NotificationJobJobFailureDigestTests`, `…CoverageDigestTests`, `MaintenanceRunFailureDigestTests`, `SmtpAdminSettingsTests:182` і `NotificationDispatcherTests:172` (трейт додано) | — |
| ФВ-13.15 | `CollectionSchedule.cs:64,87-99`, `CollectionJob.cs:49` (міграція `FV1315ScheduleDependency` уже в гілці) · `CollectionScheduleDependencyGateTests` (4), `CollectionScheduleDependencyTests`, `CollectionScheduleHandlersTests:425-528`, vitest `DataSourceScheduleTab:196` | — |
| ФВ-14.4 | undo ≥ 50 (`undo.test.ts`), вставка 500×60 (`clipboard.test.ts`), fill handle, віртуалізація (`virtualization.guard.test.ts` у CI, `e2e/virtualGrid.spec.ts`) — vitest `ФВ-14.4:` | Справжній RevoGrid 500×60 — лише e2e (поза обов'язковими гейтами) |
| ФВ-14.7 | Шаблон `PublishReasonRequiredTests`, звіт `ReportDefHandlersTests:124`, методологія `MethodologyPublishTests:162-174`, довідник `RegistryDefinitionTests`/`SwitchRegistrySourceTests` (трейт додано) | Скрипт (рівень 2) поза релізом (`D-105`); пункт реєстру «трейт на тесті про дату» застарів |
| ФВ-14.10 | `PrincipleTests:159` (рольові рішення) і `:189` (переходи = домен, трейт додано) | Негативну вимогу повністю тестом не довести; решта — рев'ю |
| ФВ-16.12 | `tools/Ecr.Bootstrap.Excel` (`UnitRecognizer`, `ImportReport.ManualReview`) · `StructureWorkbookReaderTests:169`, `UnitRecognizerTests` ×3 (трейт додано) | На реальних файлах замовника не перевірено |
| НФ-8.7.2 | `06-rcsi.sql`; `D-102` («`Critical` при старті») збігається з кодом · `StartupSchemaValidatorTests:96`, `DatabaseHealthRcsiOffTests:76`, `DbJobQueueRcsiTests:33` | ТЗ вимагає «RCSI увімкнено», зупинки старту — ні; «довгих транзакцій немає» тестом не перевіряється |
| НФ-8.9.8 | `SubmitApproveTests.Старий_поданий_зріз_лишається_після_повторного_подання:1322` (2 зрізи, старий payload, різні хеші); `SubmissionSnapshot` без мутаторів | Новий зріз створює повторний Submit (ФВ-9.17); тригера незмінності в БД нема (опційно, Sql) |

## 4. Лишаються 🟨 (58) — чому

### 4.1 Чекає людини: D-запис або рішення (24)

| Вимога | Що потрібно | Стан коду |
|---|---|---|
| ФВ-1.4 | Правка тексту ТЗ (`Closed` проєкту прибрано, `D-123`/`D-221`) | код і тест правильні |
| ФВ-1.10 | `D-222`: стан `Archived` періоду або правка ТЗ | Reopen доведено, гілка «проєкт заархівований» тепер з тестом (`ReopenArchivedProjectTests`) |
| ФВ-2.3 | `D-238` після виміру BR-07 | трейти на справжніх тестах `TableDefTests` |
| ФВ-2.9 | D-запис «песимістичний блок замість `RowVersion`» або `AR-01` | поведінка доведена `PublishTemplateVersionConcurrencyTests` |
| ФВ-2.12 | `D-250`: узгодити види `Mirror/Reference/Cascade/Copy` з ТЗ | **рантайм `Rollup`/`Check` є** (`RelationRecalculator.cs:98`, `RelationCheckRunner.cs:41`), трейт додано; ланцюжок `Rollup→Rollup` за один прогін не працює |
| ФВ-3.4 | `D-231`: формули області `Cell` | `SaveFormulaDefHandler` приймає лише Column/Row |
| ФВ-4.5 | D-запис: поріг у кількості змін (2 000), не в МБ | реалізація й тести з трейтом є; посилання реєстру на `D-134` хибне (`D-134` — про інше) |
| ФВ-5.9 | `D-270` (константи в правилах — до відповіді замовника) | REGFIELD працює (RT-24), посилання на іншу таблицю/`CST` — явна відмова |
| ФВ-5.12 | Рішення по REQ-CLOSURE №5 (сирі в'юхи без фільтра статусу) | фільтр лише у в'юсі зрізів |
| ФВ-6.4 | `D-104`/`P-1` — політика ІБ | мін. довжина, блок-лист, блокування — з тестами |
| ФВ-7.6 | `D-237` | фізичне видалення неприв'язаних сутностей |
| ФВ-7.9 | D-запис «RCSI — `Critical` + `Unhealthy`, без зупинки» | `DatabaseHealthCheck.cs:84-94` |
| ФВ-8.9 | `D-233` (період подвійної звірки) | заборона перемикання з тестом є |
| ФВ-9.10 | D-запис «методології пакета паралельно, ребра — для інвалідації» або граф (велике) | `CalculationOrchestrator.cs:140-166` |
| ФВ-10.4 | Рішення по №5 (генератор звітних в'юх за `ColumnsJson`) | сирі в'юхи для SSRS є |
| ФВ-10.5 | `D-65` / D-запис про автопобудову зрізу | `isStale` замість автопобудови |
| ФВ-12.1 | `D-232` | FIFO, один лейн |
| ФВ-12.4a | Текст ТЗ «три спроби» vs 3 ретраї = 4 спроби (`JobRetryPolicy.cs:31`) | алерт тепер доходить (SMTP у системі) |
| ФВ-13.5 | D-запис: критерій вибору рівня драбини | «симуляція без запису» доведено |
| ФВ-13.9 | Рішення: чи блокує розрив покриття публікацію | перетин при публікації є; покриття — окрема матриця `rule-coverage` |
| ФВ-14.3 | `D-235` (операції join/group/compute/script) | екран `/admin/pipeline` є |
| ФВ-14.5 | Підключити клієнтську підказку або прибрати мертвий `shared/formula` і записати D | модуль не використовується в продукті |
| ФВ-14.27 | D-запис: перехід між сторінками є (`AppLayout.tsx:43,427`), ТЗ каже «немає» | ≤ 150 мс доведено |
| НФ-8.8 | D-запис `InvariantGlobalization=false` (обґрунтування в `Q-040`); `08-nfr.md:115` досі `true` | EN/RU/KZ є |

### 4.2 Чекає замовника / заміру на його обладнанні (20)

ПРД-13, НФ-8.1, НФ-8.2.1–8.2.7, НФ-8.3.1 (➖), НФ-8.3.2, НФ-8.3.3, НФ-8.3.4, НФ-8.3.5, НФ-8.5.4, НФ-8.6.5, НФ-8.9.1, НФ-8.9.2, НФ-8.10, ФВ-11.8 (FLERT не налаштований, «datetime без поясу = UTC» не підтверджено). Без змін щодо `req-closure-5-c`; НФ-8.3.5: архівація/відновлення року з тестами є, вікно не заміряне. Довідково: НФ-8.4b реалізовано (`usp_ArchiveAudit`, `AuditArchiveSwitchTests`).

### 4.3 Потребує міграції (4)

ФВ-1.7 (`D-228`, рівні період/роль), ФВ-2.17 (`D-229`), ФВ-16.6 (одиниці аргументів `@x` — у моделі нема `UnitId` аргументу), НФ-8.9.7 (`D-248`: лишити до прода, потім поле «останній редактор»).

### 4.4 Велике (7)

| Вимога | Що лишилось |
|---|---|
| ФВ-6.8 | Храповик `DirectProfileHasRatchetTests` (Debt = 1 файл) не ловить `profile.HasInAnyProject(` — прямі виклики в 6 файлах (`GetTableSliceHandler.cs:75`, `IntegrationHandlers.cs:293`, `ReopenPeriodHandler.cs:59`, `ProjectQueryHandlers.cs:51`, `SearchHandler.cs:50`, `GetCellChangesHandler.cs:86`); 41 виклик `PermissionCheck.IsGranted` повертає `bool`, а не причину. Твердження `c9568c2` «виконано» завищене |
| ФВ-9.16 | Звірка 2 роки × 21 таблиця на даних замовника (R-4) |
| ФВ-12.7 | Обсяги й тривалість по модулях (профіль є лише в `ModulesProfileJson`) |
| ФВ-14.21 | Глобального сторожа «чотири стани для кожного подання» нема |
| ФВ-14.26 | Храповик: ~70 місць `loading={…isPending}` у 51 файлі; частина — бюджетні сторінки й `shared/ui` |
| ФВ-14.29 | Сортування не в адресі — `DataTable.tsx:421` (`shared/ui`) |
| НФ-8.9.6 | Після `usp_ArchiveAudit` (24 міс.) історія в `arc.*`, читач її не бачить |

### 4.5 Мала серверна доробка без міграції — для сесії з `dotnet` (2)

| Вимога | Доробка |
|---|---|
| НФ-8.6.3 | (1) збій архівації року: `NotificationJob` не читає `itg.ArchiveRun` (Failed) — додати `DigestItem("archive-year")` поряд з `MaintenanceRun` (`NotificationJob.cs:195-202`); (2) перевищення бюджету річного перерахунку (`jobs.recalcOverBudget` у `JobProgress`) — у зведення, як у `JobsHealthCheck.cs:~180-200`. Збій збору, `PeriodStateJob` і архівації аудиту вже доходять |
| НФ-8.6.2 | `ObservableGauge` `ecr.job.queue.depth` (кешований `COUNT` очікуючих), гістограма `ecr.calc.module.duration` з `profile.Stats` у `RecalculationJob.cs:393`, `ecr.collect.duration` у `CollectionJob` |

### 4.6 Не чіпав — у роботі «Аналізу»

ФВ-8.12 (вкладка «Зв'язки» конструктора — `1f4d4bb`, `ccb4e67`).

## 5. Застарілі місця в `REQ-CLOSURE-2026-10-01.md` (для «Аудиту»)

- ФВ-5.19, ФВ-5.20a: «тести без трейта», «Info без тесту» — неправда на `d649921f`.
- ФВ-2.6/2.7 (№25, `D-234`): «порядок рядків вимкнений», «ширин нема» — зроблено (AN-15, `D234ColumnWidthPx`).
- ФВ-2.12 (№24): «рантайм-споживача немає» — є `Rollup`/`Check`.
- ФВ-4.5 (розд. 4): посилання на `D-134` хибне.
- ФВ-14.7 (№42): «трейт методології на тесті про дату» — трейт на тесті причини `MethodologyPublishTests:162`.
- ФВ-6.5 (№29): `D-218` підтверджено людиною.
- ФВ-12.5 (розд. 4): «SMTP не налаштований, `P-13`» — замінено `D-263`.
