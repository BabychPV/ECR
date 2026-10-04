# Журнал перевірки знахідок (координатор)
## L4 (registry sync)
- L4-01 high ✔ перевірено: LinksAsync за (DataSourceId, RegistryDefId), без SourceEntityId; унікальності (DataSourceId,RegistryDefId) немає (ExternalConfiguration:69 лише FK).
- L4-02 medium ✔: complete рахується в SnapshotAsync:859 до ForeignAsync (:210).
- L4-03 medium ✔ код (SQL-відтворення — тестом).
- L4-04 medium ✔ механізм (decimal(34,16), Equals).
- L4-05 medium ~ PLAUSIBLE (потрібен реальний DateTime-атрибут AF).
- L4-06 medium→high ✔: RegistryEntryWriter:863-868 — existing.ContainsKey відкидає required=null; Set(null) уже застосовано вище (:847). Writer спільний — стосується й ручного запису.
- L4-07 medium ✔: EntityFieldMap* лише Integration.Manage, RegistryAccess немає.
- L4-08 medium ✔: у RegistrySyncJob немає applock; ручна постановка з GUID-ключем; TryMapDuplicateKey без RegistryExternalKey.
- L4-09 medium ✔ структура (TemplateAsync на кожен атрибут).
- L4-10 ~ PLAUSIBLE.
- L4-11 low ✔ (індекс фільтрований Status IS NULL).
- L4-12 low ✔ (LinksAsync без IsDeleted — бачив).
- L4-13 low ~ PLAUSIBLE + питання людині.
## L5 (registries core)
- L5-01 high ✔: FindEntriesByCodesAsync без IsDeleted (RegistryStore:737), WriteTargetsAsync:527 бере target.Existing без перевірки IsDeleted; ResolveAsync виключає видалені з byCode.
- L5-02 medium ✔: SaveRegistryDefinitionDraftRequest без версії опису; Replace(definition.DefinitionVersion) — перебазування на поточну.
- L5-03 = дубль L4-06 (+ пробільний рядок і PublishKeys без перевірки) — ОБ'ЄДНАТИ.
- L5-04 medium ✔: CsvHandlers:407 outcome за values.Count, не за changes.
- L5-05 medium ✔ код (BumpDataRevision в пам'яті, не concurrency token) — довести тестом гонки.
- L5-06 medium ✔ (агент; логіка 327-333 бачив як цитату) — прийняти.
- L5-07 low ✔ (HashSet Ordinal vs OrdinalIgnoreCase).
- L5-08 medium ✔: CheckRegistryKeyHandler без EnsureNotDenied.
- L5-09 medium ✔ (агент) — PrepareAsync без профілю.
- L5-10 = дубль L4-03 — ОБ'ЄДНАТИ (одна правка RegistryValue.MaxStringLength).
- L5-11 medium ✔: CsvReader: '"' посеред поля вмикає quoted; незакрита лапка не помилка; Encoding.UTF8 без throwOnInvalid.
- L5-12 ~ PLAUSIBLE; L5-13 ~ PLAUSIBLE; L5-14 low ✔ коментар.
## L2 (jobs/worker)
- L2-01 medium ✔: RequeueCoreAsync ставить Queued без огляду на CancelRequestedAt; ClaimQueuedSql стирає CancelRequestedAt; IsCancelRequestedAsync без викликачів. (в) OCE vs SqlException — PLAUSIBLE.
- L2-02 medium ✔: у Database IBackgroundJobScheduler=DbBackgroundJobScheduler, QuartzJobScheduler зареєстрований конкретним типом → InterruptRunningJobsAsync повертає 0.
- L2-03 ~ PLAUSIBLE (жорсткої зупинки немає — факт; реальне зависання не доведено).
- L2-04 medium ✔: FailStale/Summarize фільтрують Lane == null (JobProgressStore:465,563,588,642).
- L2-05 (а) — частково свідомо (коментар Q-240: збій продовження меж = Failed), але ховає основну перевірку запасу → потрібне рішення; (б) таймаут — PLAUSIBLE.
- L2-06..L2-12 low — прийняти як CONFIRMED-шлях (агент), низький пріоритет.
- L2-13 low ✔ частково: MaintenanceRunFailure.Details кладе ex.Message у DetailsJson; доставку в лист/Teams — за агентом.
- F-13 (59f83e50) НЕ ввімкнено — ✔ підтверджує агент.
## L6 (documents/workflow/persistence)
- L6-01 high ✔: ImportDiffBuilder:102 break лише внутрішнього циклу; MaxChanges ніде більше; без позначки обрізання.
- L6-02 high ✔ код (перенос версії без applock; відтворення — тестом 2 з'єднань).
- L6-03 medium ✔: EnsureConfirmed пропускає Origin != UserEdit; ImportDiffBuilder:252 перевіряє лише IsAllowed.
- L6-04 medium ✔: GetTableSliceHandler:159-169 — ReadSlice перед GetRowVersions.
- L6-05 medium ✔: PeriodKey.Parse лише формат; Ensure до перевірки належності періоду.
- L6-06 medium ✔ (агент) — потребує тесту.
- L6-07 ✔ ФАКТ: F-25 реалізовано (ApproveSheetHandler, коментар «пряме рішення людини»), але людина мені не відповідала і D-запису немає → ПИТАННЯ ЛЮДИНІ.
- L6-08 ~ PLAUSIBLE; L6-09..L6-14 low ✔ (агент); L6-15 ~ PLAUSIBLE.
- R4 SwitchCurrentRunAsync — ВИПРАВЛЕНО (міграція CalculationRunDocumentScope) — за агентом.
## L8 (client grid)
- L8-01 high ✔: flushUnsaved викликають лише AppLayout/staleVersion/UnsavedGuard/autosave — SheetActions/DocumentPage/Export/Import ні.
- L8-02 high ✔: SheetActions.refresh інвалідує лише ['document',...]; invalidateSlices лише в перерахунку (:450).
- L8-03 high ✔: onKeyDown на Stack без перевірки, чи подія з поля редактора.
- L8-04 medium ✔ (setRangeText на date кидає за специфікацією) — тест.
- L8-05 medium ✔: event.key замість event.code.
- L8-06 medium ✔ (clipboard без лапок).
- L8-07 medium ✔ (DateCellEditor без getValue).
- L8-08, L8-09, L8-10, L8-11 medium ✔ (агент).
- L8-12..L8-21 low ✔/~ ; L8-18 ✔: keyCommitGate.ts — 35 рядків mojibake.
## L3 (sources/PI)
- L3-01 high ✔: CollectionStore:417 SqlQuery<CoverageIsland> (ad-hoc тип без UtcDateTimeColumns), PiWebApiDataSource.Iso → ToUniversalTime() для Unspecified.
- L3-02 medium ✔ (агент; лише EcrException ловиться).
- L3-03 medium ✔: IntegrationCellPatcher:207 ChangeTracker.Clear у спільному scoped-контексті.
- L3-04 medium ✔ (агент) — гонка видалення без applock.
- L3-05 medium ✔: ValidateAsync має гілки лише PiWebApi(:463)/PiSqlClient(:482), Sql — без політики. Рішення ІБ.
- L3-06 medium ✔ (агент); L3-07 ~ PLAUSIBLE; L3-08 low ✔ (розбіжність з D-241); L3-09 ~; L3-10/L3-11 low ✔; L3-12 ~; L3-13 low ✔.
## L1 (security)
- L1-01 high ✔: SecurityStampMiddleware:51-87 перевидає cookie на будь-який не-GET, щойно штамп у БД інший; кеш "stamp:{id}" без інвалідації; ReadStampAsync лише IsActive (LockedUntil не враховано).
- L1-02 high ✔: LanguageCodes.FromTag повертає сирий субтег; ключ кешу ui:{lang}; AddMemoryCache без SizeLimit — анонімний DoS.
- L1-03 medium ✔ (агент) гонка блокування входу.
- L1-04 medium ✔ відсутність CSRF-захисту окрім SameSite=Strict (експлуатація PLAUSIBLE).
- L1-05 medium ✔ (агент перечитав) каскад видалення.
- L1-06 = L5-08 (дубль). L1-07 = L3-11 (дубль). L1-15 = L4-07 (дубль).
- L1-08 medium ✔ (агент) перенос версії з грантом Read — рівень гранта → питання людині.
- L1-09 medium ✔ (агент) source-events ігнорує заборону таблиці.
- L1-10..L1-21 low ✔ (агент), L1-19 PLAUSIBLE.
## L9 (client shell/admin)
- L9-22 high ✔: ConditionalFormatHandlers відхиляє код видаленої колонки; ColumnDefHandlers не чіпає правил; ConditionalFormatPanel.seedOf кладе сироти в others і шле назад.
- L9-01 medium ✔ (13 мутацій без meta.handled — клас).
- L9-02..L9-05 medium ✔ (агент; L9-04 звірено із серверним CultureNumberReader).
- L9-15 = L8-10 (дубль).
- L9-06..L9-26 low ✔ (агент).
- ПРОГАЛИНА: notifications/integration/pipeline/mapping/audit/methodologies/jobs/reports/units + частина registries → запущено L9b.
- L9 доповнено (сповіщення/інтеграція, помічник): L9-27 medium (шухляда з'єднання без key → шлях каталогу A з dataSourceId B; сервер шлях не звіряє), L9-28 medium (= клас L9-03 у RulesMatrix/Templates/Smtp), L9-29 medium (вимкнений канал як адресат), L9-30..34 low. Статус — за помічником (не перечитував сам) → у плані позначити «перевірити при виконанні».
- L9b (методології/довідники/jobs/reports/units) — ще йде; покриває й сповіщення повторно (дубль прийнятний).
- L9 методології/довідники (помічник, нумерую L9-35..L9-44): 35 medium impact selection не скидається; 36 medium If-Match з запиту чернетки (клієнтська частина L5-02); 37 low guard подвійної відправки (3 місця); 38 low гонка імпорту пакета; 39 low правки під час save чернетки; 40 low ColumnDefPicker порожній; 41 low нескінченне опитування; 42 low asOf на рендер; 43 low мертвий код публікації; 44 low RegistryImportPanel без key.
- L9b зупинено (дубль).
## L7 (calc/expr/domain)
- L7-01 CRITICAL ✔ код: validate з Calculation.View, тіло до 64 КіБ, ParseAdditive циклом → лівоглибоке дерево ~32k; PredicateValidator.FindPredicates рекурсивний ітератор без межі; ніде немає EnsureSufficientExecutionStack/перевірки глибини. Падіння процесу не відтворено — перший тест у плані (окремий процес-зонд).
- L7-02 high ✔ (агент; EvaluationOrder=0 під час золотого набору) — тест.
- L7-03..L7-08 medium ✔ (агент); L7-04 частково PLAUSIBLE (NCalc &); L7-08 атомарність ✔, гонка PLAUSIBLE.
- L7-09..L7-13 low; L7-12/13 PLAUSIBLE, L7-13 → питання людині (сума округлених).
## L10 (DB/deploy/CI)
- L10-01 high ✔: CalculationStep.Expression HasMaxLength(2000), MethodologyFormula до 4000, Describe не обрізає.
- L10-06 medium ✔: DocumentDeletionStore не чіпає SourceEventMap (FK_SEM_Document Restrict) → 500.
- L10-02 ~ PLAUSIBLE (icacls на сервері); L10-03 ✔ (агент); L10-04 ✔ код; L10-05 ✔ (перевірено агентом у PS 5.1); L10-07 ~; L10-08 ✔ (KNOWN, але runbook суперечить); L10-09 ~; L10-10 ~; L10-11..L10-15 ✔ low; L10-16 ~.
