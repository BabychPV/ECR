-- CONS:finding-texts ── тексти знахідок нічної перевірки узгодженості й момент останньої перевірки (аудит live 12.11) ──
-- ⛔ ConsistencyCheckJob пише текст знахідки КОНВЕРТОМ {"k":"consistency.finding.<RULE>","p":{…}}, а не готовим
-- українським реченням: мови читача задача не знає. Ключі — за кодом правила; підстановки — людські значення.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'consistency.finding.ORPHANED_CELL', N'ru', N'Ячейка строки {rowId} (период {period}) ссылается на запись справочника {entryId}, которой не существует.'),
    (N'consistency.finding.BROKEN_FK', N'ru', N'Строка {rowId} (период {period}) ссылается на экземпляр таблицы {instanceId}, которого не существует.'),
    (N'consistency.finding.ARCHIVE_CHECKSUM', N'ru', N'Контрольные суммы прогона архивации {runId} не совпали: источник {source}, архив {target}.'),
    (N'consistency.finding.UNBOUND_CALCULATED_COLUMN', N'ru', N'Колонку {table}.{column} (версия шаблона {version}) заполняет методология, но к ней не привязан ни один действующий выход методологии и её не заполняет формула шаблона: оператор видит пустую ячейку, которую не может заполнить. Привяжите выход методологии к этой колонке или включите существующую привязку.'),
    (N'consistency.finding.UNSOURCED_FORMULA_COLUMN', N'ru', N'Колонка {table}.{column} (версия шаблона {version}) — формульная, но в опубликованной версии на ней нет ни формулы шаблона, ни действующей привязки методологии: оператор видит пустую ячейку, которую не может заполнить. Привяжите выход методологии к колонке или исправьте структуру новой версией шаблона.'),
    (N'consistency.finding.NON_CURRENT_RUN_ROWS', N'ru', N'В периоде {period} остались строки неактуальных прогонов расчёта: результатов {results}, шагов {steps}, входов {inputs}. Хранится только последний расчёт; ночная очистка должна была их удалить. Если замечание повторяется, проверьте, что удаление при очистке включено.'),
    (N'consistency.finding.ORPHANED_CELL', N'kz', N'{rowId} жолының ұяшығы ({period} кезеңі) жоқ {entryId} анықтамалық жазбасына сілтейді.'),
    (N'consistency.finding.BROKEN_FK', N'kz', N'{rowId} жолы ({period} кезеңі) жоқ {instanceId} кесте данасына сілтейді.'),
    (N'consistency.finding.ARCHIVE_CHECKSUM', N'kz', N'{runId} мұрағаттау іске қосылымының бақылау сомалары сәйкес келмеді: дереккөз {source}, мұрағат {target}.'),
    (N'consistency.finding.UNBOUND_CALCULATED_COLUMN', N'kz', N'{table}.{column} бағанын ({version} үлгі нұсқасы) әдістеме толтырады, бірақ оған әдістеменің бірде-бір белсенді шығысы байланыстырылмаған және оны үлгі формуласы толтырмайды: оператор толтыра алмайтын бос ұяшықты көреді. Әдістеме шығысын осы бағанға байланыстырыңыз немесе бар байланысты қосыңыз.'),
    (N'consistency.finding.UNSOURCED_FORMULA_COLUMN', N'kz', N'{table}.{column} бағаны ({version} үлгі нұсқасы) — формулалық, бірақ жарияланған нұсқада онда үлгі формуласы да, әдістеменің белсенді байланысы да жоқ: оператор толтыра алмайтын бос ұяшықты көреді. Әдістеме шығысын бағанға байланыстырыңыз немесе құрылымды үлгінің жаңа нұсқасымен түзетіңіз.'),
    (N'consistency.lastCheck', N'ru', N'Последняя проверка:'),
    (N'consistency.lastCheck', N'kz', N'Соңғы тексеру:'),
    (N'consistency.finding.NON_CURRENT_RUN_ROWS', N'kz', N'{period} кезеңінде есептеудің өзекті емес іске қосылымдарының жолдары қалды: нәтижелер {results}, қадамдар {steps}, кірістер {inputs}. Тек соңғы есептеу сақталады; түнгі тазалау оларды жоюы тиіс еді. Ескерту қайталанса, тазалаудағы жою қосулы екенін тексеріңіз.'),
    -- B70: пряма правка архіву в SQL (ARCHIVE_CHECKSUM, сутність arc.Period).
    (N'consistency.finding.ARCHIVE_TAMPER', N'ru', N'Архивные данные периода {period} изменились после архивации (эталон — прогон архивации {runId}): ячеек было {cellsThen}, сейчас {cellsNow}; строк было {rowsThen}, сейчас {rowsNow}; экземпляров таблиц было {instancesThen}, сейчас {instancesNow}; контрольная сумма содержимого не совпадает. Приложение архив не изменяет — это прямое изменение в базе данных. Выясните, кто его внёс, и восстановите архив периода из резервной копии.'),
    (N'consistency.finding.ARCHIVE_TAMPER', N'kz', N'{period} кезеңінің мұрағат деректері мұрағаттаудан кейін өзгерді (эталон — {runId} мұрағаттау іске қосылымы): ұяшықтар {cellsThen} болды, қазір {cellsNow}; жолдар {rowsThen} болды, қазір {rowsNow}; кесте даналары {instancesThen} болды, қазір {instancesNow}; мазмұнның бақылау сомасы сәйкес келмейді. Қолданба мұрағатты өзгертпейді — бұл дерекқордағы тікелей өзгеріс. Оны кім енгізгенін анықтап, кезең мұрағатын сақтық көшірмеден қалпына келтіріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CONS:finding-texts ── кінець секції ──
-- CONS:finding-texts ── en (область 1 — адмінський екран /admin/consistency-issues); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'consistency.lastCheck', N'en', N'Last check:', 1),
    (N'consistency.finding.ORPHANED_CELL', N'en', N'A cell in row {rowId} (period {period}) refers to registry entry {entryId}, which does not exist.', 1),
    (N'consistency.finding.BROKEN_FK', N'en', N'Row {rowId} (period {period}) refers to table instance {instanceId}, which does not exist.', 1),
    (N'consistency.finding.ARCHIVE_CHECKSUM', N'en', N'Checksums of archive run {runId} do not match: source {source}, archive {target}.', 1),
    (N'consistency.finding.UNBOUND_CALCULATED_COLUMN', N'en', N'Column {table}.{column} (template version {version}) is filled by a methodology, but no active methodology output is bound to it and no template formula fills it, so operators see an empty cell they cannot fill. Bind a methodology output to this column or enable the existing binding.', 1),
    (N'consistency.finding.UNSOURCED_FORMULA_COLUMN', N'en', N'Column {table}.{column} (template version {version}) is a formula column, but the published version has neither a template formula on it nor an active methodology binding, so operators see an empty cell they cannot fill. Bind a methodology output to the column or fix the structure in a new template version.', 1),
    (N'consistency.finding.NON_CURRENT_RUN_ROWS', N'en', N'Period {period} still holds rows of outdated calculation runs: {results} results, {steps} steps, {inputs} inputs. Only the latest calculation is kept, and the nightly cleanup should have removed them. If this finding persists, check that cleanup deletion is enabled.', 1),
    (N'consistency.finding.ARCHIVE_TAMPER', N'en', N'Archived data of period {period} changed after archiving (reference: archive run {runId}): cells {cellsThen} then, {cellsNow} now; rows {rowsThen} then, {rowsNow} now; table instances {instancesThen} then, {instancesNow} now; the content checksum does not match. The application never changes the archive, so this is a direct database change. Find out who made it and restore the period''s archive from a backup.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CONS:finding-texts ── кінець секції ──
