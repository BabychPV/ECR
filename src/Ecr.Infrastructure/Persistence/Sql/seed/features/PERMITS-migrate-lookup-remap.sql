-- PERMITS: migrate-lookup-remap ── ru/kz: мапа записів Lookup шапки при переносі версії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateLookupRemapInvalid', N'ru', N'Карта записей Lookup составлена неверно: поддерживаются только поля шапки (kind "header"), код поля должен быть непустым, идентификаторы записей положительными, исходная запись указывается один раз и должна принадлежать справочнику версии, с которой переносят. Допускается не более {maxTargets} полей и не более {maxPairs} пар в каждом.'),
    (N'err.ECR-SCHM-0422.migrateLookupRemapInvalid', N'kz', N'Lookup жазбаларының картасы дұрыс құрастырылмаған: тек тақырып өрістері қолдау табады (kind "header"), өріс коды бос болмауы керек, жазба идентификаторлары оң болуы керек, бастапқы жазба бір рет көрсетіледі және көшіріліп жатқан нұсқаның анықтамалығына тиесілі болуы керек. Ең көбі {maxTargets} өріс және әрқайсысында ең көбі {maxPairs} жұп рұқсат етіледі.'),
    (N'err.ECR-SCHM-0422.migrateLookupNotMapped', N'ru', N'Ничего не перенесено: часть значений шапки ссылается на записи, для которых в карте Lookup нет пригодного соответствия (записи нет в карте либо её цель отсутствует в новом справочнике, неактивна, удалена или не действует в окне проекта). Отчёт называет идентификаторы записей; дополните карту и повторите.'),
    (N'err.ECR-SCHM-0422.migrateLookupNotMapped', N'kz', N'Ештеңе көшірілмеді: тақырып мәндерінің бір бөлігі Lookup картасында жарамды сәйкестігі жоқ жазбаларға сілтейді (жазба картада жоқ немесе оның мақсаты жаңа анықтамалықта жоқ, белсенді емес, жойылған не жоба терезесінде жарамсыз). Есеп жазба идентификаторларын атайды; картаны толықтырып, қайталаңыз.'),
    (N'err.ECR-SCHM-0422.migrateLookupLockedDocuments', N'ru', N'Ничего не перенесено: карта Lookup переписала бы значения шапки в документах с поданными или утверждёнными листами ({lockedDocumentCount}). Их количество показывает пробный прогон; подтвердите параметром allowLockedDocuments = true и повторите.'),
    (N'err.ECR-SCHM-0422.migrateLookupLockedDocuments', N'kz', N'Ештеңе көшірілмеді: Lookup картасы жіберілген немесе бекітілген парақтары бар құжаттардағы тақырып мәндерін қайта жазар еді ({lockedDocumentCount}). Олардың санын сынақ жүгіру көрсетеді; allowLockedDocuments = true арқылы растап, қайталаңыз.'),
    -- MIGRATE-FAST: ключі повідомлень переносу версії
    (N'err.ECR-SCHM-0422.migrateResumeMismatch', N'ru', N'Предыдущий перенос версии этого проекта был прерван. Продолжить его можно только с той же картой (колонок, строк, Lookup шапки): карта отличается от сохранённой. Ничего не изменено.'),
    (N'err.ECR-SCHM-0422.migrateResumeMismatch', N'kz', N'Бұл жобаның нұсқасын көшіру үзілді. Оны тек сол картамен (бағандар, жолдар, тақырып Lookup) жалғастыруға болады: карта сақталғаннан өзгеше. Ештеңе өзгертілген жоқ.'),
    (N'err.ECR-SCHM-0422.migrateBackupRequired', N'ru', N'Ничего не перенесено: перенос затрагивает {transferredValues} значений (лимит без подтверждения резервной копии — {threshold}). Сделайте резервную копию базы и повторите запрос с backupConfirmation (путь и/или время копии).'),
    (N'err.ECR-SCHM-0422.migrateBackupRequired', N'kz', N'Ештеңе көшірілмеді: көшіру {transferredValues} мәнді қамтиды (сақтық көшірмені растамай рұқсат етілетін шек — {threshold}). Дерекқордың сақтық көшірмесін жасап, backupConfirmation (көшірменің жолы және/немесе уақыты) арқылы өтінімді қайталаңыз.'),
    (N'documents.migrateBackupLabel', N'ru', N'Подтверждение резервной копии'),
    (N'documents.migrateBackupLabel', N'kz', N'Сақтық көшірмені растау'),
    (N'documents.migrateBackupHint', N'ru', N'Перенос затрагивает {count} значений (лимит без подтверждения резервной копии — {threshold}). Сначала сделайте резервную копию базы и укажите её путь и/или время.'),
    (N'documents.migrateBackupHint', N'kz', N'Көшіру {count} мәнді қамтиды (сақтық көшірмені растамай рұқсат етілетін шек — {threshold}). Алдымен дерекқордың сақтық көшірмесін жасап, оның жолын және/немесе уақытын көрсетіңіз.'),
    (N'documents.migrateBackupMissing', N'ru', N'Укажите путь или время резервной копии, чтобы начать перенос.'),
    (N'documents.migrateBackupMissing', N'kz', N'Көшіруді бастау үшін сақтық көшірменің жолын немесе уақытын көрсетіңіз.'),
    -- RPT-STABLE: Part B ── перенос версії «з місяця» (ru/kz) ──
    (N'err.ECR-SCHM-0422.migrateFromMonthRequired', N'ru', N'Ничего не перенесено: перенос «С месяца» требует fromPeriodKey (период проекта, ГГГГММ).'),
    (N'err.ECR-SCHM-0422.migrateFromMonthRequired', N'kz', N'Ештеңе көшірілмеді: «Айдан бастап» көшіру үшін fromPeriodKey (жоба кезеңі, ЖЖЖЖАА) қажет.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthInvalid', N'ru', N'Ничего не перенесено: период {periodKey} не является периодом этого проекта.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthInvalid', N'kz', N'Ештеңе көшірілмеді: {periodKey} кезеңі осы жобаның кезеңі емес.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthBeforeSegment', N'ru', N'Ничего не перенесено: период {periodKey} раньше {segmentFrom}, с которого начинается текущая версия шаблона проекта. Выберите период не раньше {segmentFrom}.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthBeforeSegment', N'kz', N'Ештеңе көшірілмеді: {periodKey} кезеңі жобаның ағымдағы шаблон нұсқасы басталатын {segmentFrom} кезеңінен ерте. {segmentFrom} немесе одан кейінгі кезеңді таңдаңыз.'),
    (N'err.ECR-SCHM-0422.migrateWholeProjectMultiVersion', N'ru', N'Ничего не перенесено: проект уже использует несколько версий шаблона по периодам, поэтому перенос всего проекта недоступен. Используйте «С месяца» с периодом не раньше начала текущей версии.'),
    (N'err.ECR-SCHM-0422.migrateWholeProjectMultiVersion', N'kz', N'Ештеңе көшірілмеді: жоба кезеңдер бойынша бірнеше шаблон нұсқасын пайдаланады, сондықтан бүкіл жобаны көшіру қолжетімсіз. Ағымдағы нұсқаның басынан ерте емес кезеңмен «Айдан бастап» қолданыңыз.'),
    (N'err.ECR-SCHM-0422.migrateTargetNotPublished', N'ru', N'Ничего не перенесено: целевая версия шаблона не опубликована.'),
    (N'err.ECR-SCHM-0422.migrateTargetNotPublished', N'kz', N'Ештеңе көшірілмеді: мақсатты шаблон нұсқасы жарияланбаған.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthDisabled', N'ru', N'Ничего не перенесено: перенос «С месяца» пока не включён. Доступен только перенос всего проекта.'),
    (N'err.ECR-SCHM-0422.migrateFromMonthDisabled', N'kz', N'Ештеңе көшірілмеді: «Айдан бастап» көшіру әлі қосылмаған. Тек бүкіл жобаны көшіру қолжетімді.'),
    (N'documents.migrateRangeLabel', N'ru', N'Охват'),
    (N'documents.migrateRangeLabel', N'kz', N'Қамту'),
    (N'documents.migrateRangeWhole', N'ru', N'Весь проект, в любое время'),
    (N'documents.migrateRangeWhole', N'kz', N'Бүкіл жоба, кез келген уақытта'),
    (N'documents.migrateRangeFromMonth', N'ru', N'С месяца'),
    (N'documents.migrateRangeFromMonth', N'kz', N'Айдан бастап'),
    (N'documents.migrateFromMonthLabel', N'ru', N'Первый месяц на новой версии'),
    (N'documents.migrateFromMonthLabel', N'kz', N'Жаңа нұсқадағы бірінші ай'),
    (N'documents.migrateWarnEffectiveFromJanuary', N'ru', N'В проекте есть таблица с месяцами в колонках, поэтому весь год переходит на новую версию: перенос начнётся с января ({effective}) вместо {requested}.'),
    (N'documents.migrateWarnEffectiveFromJanuary', N'kz', N'Жобада бағандарда айлары бар кесте бар, сондықтан бүкіл жыл жаңа нұсқаға көшеді: көшіру {requested} орнына қаңтардан ({effective}) басталады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: migrate-lookup-remap ── кінець секції ──
-- PERMITS: migrate-lookup-remap ── перенос версії: мапа записів Lookup шапки (ECR-SCHM-0422 migrateLookup*); ru/kz — порцією PERMITS: migrate-lookup-remap нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateLookupRemapInvalid', N'en', N'The Lookup remap map is malformed: only header fields are supported (kind "header"), the field code must be non-empty, entry ids must be positive, a source entry may be listed once, and every source entry must belong to the registry of the version being moved from. At most {maxTargets} fields with at most {maxPairs} pairs each are allowed.', 1),
    (N'err.ECR-SCHM-0422.migrateLookupNotMapped', N'en', N'Nothing was moved: some header values point to entries that the Lookup remap map does not cover with a usable counterpart (the entry is not in the map, or its target is missing from the new registry, inactive, deleted or not valid in the project window). The report lists the entry ids; extend the map and retry.', 1),
    (N'err.ECR-SCHM-0422.migrateLookupLockedDocuments', N'en', N'Nothing was moved: the Lookup remap would rewrite header values in documents that have submitted or approved sheets ({lockedDocumentCount}). The dry run reports their number; confirm with allowLockedDocuments = true and retry.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: migrate-lookup-remap ── кінець секції ──
