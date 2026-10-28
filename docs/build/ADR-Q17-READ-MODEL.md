# ADR Q-17: модель читання значень для звітних в'ю (прапор MonthColumnRouting = ON)

Статус: ПРОПОЗИЦІЯ, чекає погодження координатора. Код не пишеться, не пушиться. Дата 2026-10-28, база доказів EcrPassC_Q17 (лише читання/очікувані плани), збірка p66 = 002c19cbd.

## 0. Проблема і докази

Аудит-2 p66 (Land, ON), теплі медіани проти p64 (1be289bd2): T9_04 102 с (було 3,1), T10_03 36 с (0,77), T11_03 30 с (0,77), T7b_00 4,9 с (0,65); T3_00 холодний 386 с, теплий 150 с, план 245 Nested Loops / 233 звернення до CellValue (було 234 / 231).

Встановлено читанням (без виконання важких запитів):

1. **T3_00 не має відношення до MonthsInColumns.** `cfg.TableDef.LayoutKind = 3` (PerPeriodInstance), 232 колонки, 20 Calculated + 6..10 Formula, 60 активних прив'язок. Гілка `@routed` генератора вмикається лише при `@layout = 0`, тому t3perf (f44b93ca2: `oim`/`ow<m>`, apply без IN-списку) на цю таблицю НЕ діє: текст в'ю в EcrPassC_Q17 містить 234 `LEFT JOIN doc.CellValue c<n>` і жодного `oim`. Тест t3perf будував синтетику з layout 0 (96 місячних колонок) і ніколи не відтворював реальну форму T3_00. Це помилка моделі синтетики, а не плану.
2. ~~**Регрес T9_04/T10_03/T11_03/T7b_00 = cb90925f7 (rv ...).**~~ ⚠ АТРИБУЦІЯ УТОЧНЕНА незалежним розбором (DIAG-Q17-VIEW-PERF-002c19cbd.md): ця група - від oim (GROUP BY PeriodKey/100 по ВСІЙ doc.TableInstance) + 12 ow<m>/owA з f44b93ca2 (оцінка x215: 437 066 проти 2 028; hash-зʼєднання, 33 повних скани CellValue 5,44 млн і 13 сканів TableRow без відсічки партицій), а rv (cb90925f7) домінує в T3_00 (~107 з 130 с). Обидва чинники закриваються тією самою набірною формою. Початкове твердження (нижче) - моя гіпотеза до цього розбору. **Регрес T9_04/T10_03/T11_03/T7b_00 = cb90925f7 (rv, колонка Calculated числом методології).** Усі чотири мають колонки DataType=8 (T9_04: 22, T10_03/T11_03: 20, T7b_00: 1) і активні прив'язки (16-28 / 20 / 1). В p64 rv не було; в'ю p66 для них містять `AS rv`. Очікуваний план `SELECT * FROM v_..T9_04_v1_0_14_0` (2,9 МБ XML, оцінено до виконання): 54 Compute Scalar, 52 Hash Match, 28 Nested Loops, 53 Clustered Index Scan, 14 Index Spool; піддерево rv коштує 17 267 з 22 582 (76 %); Index Seek `IX_CalculationResult_Lookup` оцінений на **19,8 млн перепривʼязок** при 437 066 рядках в'ю (~45 seek-ів на рядок: прив'язка x версія методології x результат). Оцінка кардинальності 1 рядок скрізь, оптимізатор обирає вкладені цикли на рядок.
3. **Індекси не причина.** `calc.CalculationResult` (4 466 558 рядків, партиції ps_ByPeriodKey) уже має покриваючий `IX_CalculationResult_DocRun (PeriodKey, DocumentId, CalculationRunId; INCLUDE MethodologyVersionId, OutputCode, SourceRowKey, Value, UnitId, SubstanceEntryId, TableInstanceId)` (Sql/07-partition-tables.sql) і `IX_CalculationResult_Lookup (PeriodKey, DocumentId, MethodologyVersionId, OutputCode; INCLUDE Value, UnitId, SubstanceEntryId, SourceRowKey)`; `cfg.CalculationBinding` (5 064 рядки, 4 700 активних) - `UQ (ColumnDefId, MethodologyId, OutputCode)`; `calc.CalculationRun` (9 357) - `UX_CalculationRun_Current`. ~~Результатів документа на період мало (doc 10: 138)~~ ⚠ ПОМИЛКА ЦЬОГО ПУНКТУ (виправлено за базовими замірами Аудита-2, PERF-BASELINE-Q17-002c19cbd.md): результатів на (документ, період) - 424 групи, p50 1 440, p95 95 920, max 173 606; 138 була вибірка одного документа. Отже SourceRowKey як residual (не ключ) читає весь набір документа на кожен зонд, а 88 % прочитаного - результати Superseded-прогонів (діагноз DIAG-Q17-VIEW-PERF-002c19cbd.md). Проблема в ФОРМІ запиту (корельований apply на рядок x прив'язку, актуальні прогони не першими), а не в доступі. Окремий індекс "наосліп" нічого не дасть; міграція в цьому напрямі не потрібна.
4. **MatchJson**: усі 4 700 активних прив'язок Land мають `{}`; предикат у запиті - мертвий код для Land, але семантично обов'язковий.
5. `doc.CellValue`: 5 437 778 комірок, PK `(PeriodKey, TableRowId, ColumnDefId)` на ps_ByPeriodKey, `doc.TableRow` 666 842. Читання "всі комірки рядка" = один діапазон PK.

Висновок: шаблон "кореляційний підзапит/apply/JOIN на КОЛОНКУ або на РЯДОК x ПРИВ'ЯЗКУ" нестійкий за побудовою. Кожне виправлення в такій формі (p61 -> viewfix -> t3perf -> recalc) лише міняло, де саме йде кратність.

## 1. Модель зберігання і правило "звідки значення"

- `doc.CellValue` екземпляра-ВЛАСНИКА = єдине джерело правди для введених значень (D-457): місячна колонка M{m} - екземпляр періоду m, введена не-місячна - екземпляр-якір року; власна копія в екземплярі перегляду - лише запасне значення.
- `calc.CalculationResult` - результат методології (колонка Calculated показує його, не комірку).
- Правило значення комірки (одне, нормативне): `власна обчислена зі значенням > власник > власна копія` для введених колонок; `число методології (власний екземпляр > загальний; найбільший Id прив'язки; TryCombine) > matеріалізоване` для Calculated. Це правило сьогодні записано ДВІЧІ: у C# (`MonthRoutedCellStore`, `CalculatedCellOverlay`) і в динамічному SQL генератора. Еквівалентність тримає лише тест `ReportViewSliceEquivalenceTests` постфактум.

## 2. Варіанти

**(а) Набірне читання (рекомендовано).**
- Мала мапа `cfg.ColumnOwnerMap(TemplateVersionId, TableDefId, ColumnDefId, OwnerKind[Own|Month|Anchor], MonthNo)`, що генерується з шаблону разом із в'ю (дані, а не текст SQL; ~10^3 рядків на версію).
- Inline TVF `rpt.fn_DocumentCells(@DocumentId, @PeriodKey)` (одна вибірка комірок документа за потрібні періоди з owner-мапою: `doc.CellValue` x `doc.TableRow(RowKey)` x мапа, елімінація партицій по PeriodKey, один прохід) повертає `(RowKey, ColumnDefId, значення по слотах, IsCalculated)` вже з розв'язаним власником і правилом пріоритету. В'ю = pivot цього набору умовною агрегацією `MAX(CASE ColumnDefId ...)` по `(DocumentId, RowKey)` - ОДНЕ приєднання на в'ю, незалежне від числа колонок і місяців. Ліміт 256 таблиць зникає як клас.
- Результати методологій - один попередньо згрупований набір `rpt.fn_MethodologyResults(@DocumentId, @PeriodKey)` за `(SourceRowKey, ColumnDefId)` (пакет "id прив'язки + значення", логіка own/generic/TryCombine/Current-run - всередині, один раз), під індекс `IX_CalculationResult_DocRun` (покриває). Приєднання до в'ю - hash/merge по `(DocumentId, PeriodKey, SourceRowKey)`.
- Прототип (виконано локально на синтетиці, не запушено; патч F:\ecr-backup\q17-perf\prototype-setbased-rv-pc-20261028.patch): PerPeriodInstance, 230 колонок, 600 рядків, ~20 комірок на рядок: ОДИН apply замість 230 LEFT JOIN - 3 148 читань / 93 мс CPU проти 11 465 / 2 625 мс (в 28 разів менший CPU), вміст ідентичний (відбиток SHA-256). Набірний rv у прототипі показав 125 тис. читань на 600 рядків у конфігурації 150+40 Calculated (не вкладається) - тобто "дві похідні таблиці у динамічному SQL" без контролю плану НЕ достатні; саме тому ADR вимагає TVF з контрольованою формою й вимірами плану на Land ДО коду, а не ще один підкрут.

**(б) Матеріалізована read-модель** (таблиця `rpt.CellOwnerValue` або indexed view "значення власника на адресу"), оновлювана записом/перерахунком. Плюси: читання = seek, найшвидше. Мінуси: ціна запису (кожен PATCH/Reopen/консолідація/перерахунок пише ще й у read-модель; бюджет запису p95 <= 200 мс під ризиком), ризик розбіжності при збої між записом комірки та оновленням (потрібна транзакційність або черга + повторна побудова), indexed view неможливий (OUTER/APPLY/CASE по декількох джерелах, MAX/pivot), обсяг ~ у кількість комірок (5,4 млн). Вибирати лише якщо (а) не вкладається в бюджет за вимірами Land.

**Рекомендація: (а)**, з (б) як запасним. Причина: правило значення лишається обчисленням над правдою, консистентність "за побудовою", нуль додаткової ціни запису.

## 3. Одне визначення правила для слайсу і в'ю

- Що можна ділити: **SQL-форму** між генератором в'ю та читачем API. TVF `fn_DocumentCells` можна викликати з C# (`FromSqlInterpolated` у `MonthRoutedCellStore.OverlayDocumentAsync` замість двох викликів і накладання в пам'яті): слайс і в'ю тоді читають ОДНУ функцію, еквівалентність за побудовою для "введених + власник + власна обчислена" (D-457).
- Чого чесно НЕ можна: `CalculatedCellOverlay` робить обчислення методології в C# (`MethodologyRuleMatcher`, `TryCombine`, одиниці, маски доступу Q-17 `ForeignColumns/OwnerPeriod`). Права/маски й одиниці лишаються в C#; у SQL TVF результатів ділимо лише "вибір результату" (Current-run, own > generic, найбільший Id, TryCombine) і закріплюємо тестом-парою на реальному SQL (поточний `ReportViewSliceEquivalenceTests` розширюється на Calculated). Предикат MatchJson залежить від комірок рядка: у TVF виражається приєднанням бінду з предикатом до комірок рядка лише для прив'язок з `MatchJson <> '{}'` (на Land їх немає); потребує окремого рішення (див. відкриті питання).
- Відомі розбіжності в'ю != слайс, які ця зміна НЕ вирішує, а має вирішити правилом: AN-147 (рядковий результат), "обчислювані" 5-10 % комірок після recalc (Аудит-2 p64), "власна введена копія без власника" (7 адрес).

## 4. Індекси, статистика, партиції

- Нових індексів на `doc.CellValue`/`calc.CalculationResult` НЕ пропонується: PK і `IX_CalculationResult_DocRun` покривають шляхи (діапазон за `(PeriodKey, TableRowId)`; `(PeriodKey, DocumentId, CalculationRunId)` з INCLUDE TableInstanceId/SourceRowKey/Value/UnitId). Вплив на запис нульовий. Можливий єдиний кандидат після вимірів: `cfg.CalculationBinding (MethodologyId, OutputCode) INCLUDE (ColumnDefId, TableDefId, IsActive, MatchJson)` - для входу з боку результатів; ввести лише якщо план TVF покаже скан бінд-таблиці (5 тис. рядків - малоймовірно).
- Елімінація партицій: усі звернення з явним `PeriodKey = @p` або `IN (...)` (параметризація TVF по періоду); у "усіх документів x усіх періодів" SELECT * по в'ю сканує всі партиції - це допустимо лише для вибірок усього в'ю, бюджети нижче міряються з предикатом `_DocumentId/_PeriodKey`.
- Статистика: у TVF не покладатися на оцінку "1 рядок" (джерело вкладених циклів 245 NL); обрати форму, де join-ключі збігаються з ключами індексів, і перевірити фактичний план на Land; `UPDATE STATISTICS` на `calc.CalculationResult`/`doc.CellValue` для партицій останнього року - частина upgrade-нотатки.
- Upgrade: перегенерація `EXEC rpt.usp_GenerateTemplateViews @MonthRouting = 1`; нова TVF і мапа - у `Sql/*.sql` (ідемпотентно), без міграції EF, якщо мапа створюється сирим SQL разом з в'ю; міграція лише якщо мапа стає EF-таблицею (тоді токен міграцій).

## 5. Бюджети і пакет вимірів для Аудита-2 (Land-копія, ON)

Бюджети (рішення людини 28.10): в'ю кожної таблиці - теплий <= 2 с і <= 1,2x від OFF; T3_00 теплий <= 5 с, холодний <= 15 с; GET слайсу/tables/status <= 1 с теплий; запис p95 <= 200 мс.

Пакет вимірів ДО коду (читання/очікувані плани, без важких виконань на спільній базі - окремий прогін у вікні Аудита-2):
1. Фактичний план + `SET STATISTICS IO, TIME` для `SELECT * FROM v_T3_00` з предикатом `_DocumentId = <найбільший>, _PeriodKey = 202610` (OFF і ON) - заміряє реальні рядки/читання, а не оцінку.
2. Те саме для T9_04, T10_03, T11_03, T7b_00 (OFF/ON) і для 8 колонок Calculated.
3. Розподіл: кількість комірок на рядок і заповнених колонок на документ для T3_00/T9_04 (визначає вартість pivot: O(колонок x комірок рядка)); кількість результатів на (документ, період).
4. Частка в'ю, де ON != OFF за часом >1,2x.
5. Після реалізації: ті самі виміри + `ReportViewSliceEquivalenceTests` (в'ю == слайс), golden OFF, відбиток вмісту, запис p95 (PATCH комірки) до/після.

Відкриті питання:
- Поводження з бінд-предикатом `MatchJson <> '{}'`: підтримувати в TVF (повільніша гілка лише для таких бінд) чи заборонити в моделі? Дефолт до відповіді: підтримувати, ізольовано.
- Чи вважається вхідним критерієм "<= 2 с" весь `SELECT *` по в'ю без предиката (437 тис. рядків T9_04; навіть p64 = 3,1 с) чи вибірка документа/періоду? Дефолт: з предикатом `_DocumentId/_PeriodKey` (так користується застосунок/SSRS).
- Чи можемо перенести читання API-слайсу на TVF в тій же зміні (вигода: еквівалентність за побудовою) чи окремим кроком.

## Уточнення 2026-10-28 (реалізація, коміт [CODE] Q-17.perf)

- Реалізовано в `Sql/05-rpt-views.sql` без міграції і без нових індексів (`IX_CalculationResult_DocRun` покриває; експеримент з індексом по SourceRowKey не дав виграшу - вузьке місце було в формі, не в доступі).
- Не TVF, а **ведучий набір ti -> CROSS APPLY** усередині самої в'ю (в'ю лишається звичайною, SSRS без параметрів): oim - apply на екземпляр (рівності PeriodKey по UQ_TableInstance), rv - CROSS APPLY на екземпляр (актуальні прогони першими -> seek DocRun -> прив'язки таблиці хеш-зʼєднанням -> групування за SourceRowKey -> хеш-зʼєднання з рядками екземпляра), pc - один apply по ключу рядка для нероутованих колонок (прапор ON). Параметри екземпляра (ti.PeriodKey, d.Id) у apply - константи, тож предикат `_DocumentId/_PeriodKey` проштовхується природно; перевіряється тестом форми плану.
- Спільна TVF для слайсу API лишається окремим кроком: правило «звідки значення» поки записане в C# і в генераторі; еквівалентність тримає `ReportViewSliceEquivalenceTests` (33 стани), не побудова.
- MatchJson <> '{}': ізольована гілка (apply на рядок як раніше) для таблиць, де на момент генерації є така прив'язка. Прив'язку з предикатом, додану ПІСЛЯ генерації, наборова форма ігнорує до перегенерації (старт застосунку/публікація шаблону перегенерують в'ю).
- Урок синтетики (t3perf): T3_00 - PerPeriodInstance (LayoutKind 3), синтетика мусить мати ту саму розкладку, >=200 колонок, розріджені комірки, результати актуального й НЕактуальних прогонів. Нові тести так і побудовані.
- Хеш-зʼєднання (`LEFT/INNER HASH JOIN`) у в'ю - свідомий підказувач: без нього оптимізатор на оцінці «1 рядок» обирав вкладений цикл із Table Spool (600 x 600 = 360 тис. рядків спула, 122 тис. читань на 600 рядків). Ціна: підказка фіксує порядок зʼєднань усередині apply.

## Уточнення 2026-10-28 (lane/integ-q17-slice: наборове читання API-зрізу і статусу)

- **Замір на синтетиці форми Land** (Q17SliceReadModelTests; layout 0 і 3, 100 рядків, розріджені комірки, 40 тис. чужих чисел на прогін, актуальний + неактуальний прогони): зріз з колонками Calculated вичитував ВСІ числа документа-періоду (44 737 рядків з бази при 4 500 комірках), сортував і переносив у памʼять; понад 50 000 - ECR-CALC-0422. Це й домінувало (запитів на зріз і так фіксована кількість, не по комірці/рядку).
- Зроблено без міграції й без нового індексу: CalculationResultStore.ReadCurrentMatchingAsync (порт ICurrentResultFilterReader) - той самий запит по IX_CalculationResult_DocRun, плюс предикати код виходу / версія методології / екземпляр (або NULL); накладення бере його, якщо порт уміє. Проєкт документа й актуальні прогони - один запит. Рядків з бази 44 737 -> 4 735.
- 	ables/status: чужі власники рахуються ОДНИМ запитом по діапазону ключа партиції року (BETWEEN Рік*100+1 AND Рік*100+12, без періоду перегляду) замість запиту на період-власник: 15 -> 5 звернень, doc.TableInstance 13 -> 3. Логічні читання майже не змінилися: вартість - пошук комірок власників по КОЖНОМУ рядку власника (рядки x періоди), а не кількість запитів. Знизити її далі можна лише індексом (PeriodKey, ColumnDefId, ...) - це міграція, не робилась (токен міграцій).
- Правило «звідки значення» лишається записаним двічі (C# і генератор вʼюх): текст вʼюх не змінено, golden OFF зелений, еквівалентність тримає ReportViewSliceEquivalenceTests.

## Addendum 2026-10-28: PAGE compression of calc.CalculationResult indexes

Status: PROPOSAL. Nothing in this section has been run, migrated or applied anywhere. No change to any live database (Land or otherwise) happens without a new explicit human word.

### A. Measured basis (read-only estimate on the Land copy EcrPassC_Q17)

Sources: `.sync-local/reports/Q17-T3_00-compression-estimate.sql`, its `-RESULT.txt`, `NOTE-Q17-T3_00-CELLVALUE-NEXT.md`.

- Current state (query 0 of the script): all five `calc.CalculationResult` indexes are `NONE`, 25 partitions, 4,466,232 rows, 2.24 GB used in total (DocRun 0.60, Lookup 0.60, PK 0.63, Version 0.23, UnitId 0.18 GB).
- `sp_estimate_data_compression_savings ... 'PAGE'`, partition 7 (period 202606) only: 265,080 KB -> 55,088 KB, about -79% for the table. Per index: PK -86%, DocRun -81%, Lookup -85%, Version -64%, UnitId -44%. The extrapolation of -79% to all 25 partitions is an assumption (one partition sampled; 202604 was not estimated for this table). The mapping of estimate `index_id` 2..5 to index names was inferred from index size and the two measured names in the caller's note, not read from the output; it is not stated in the RESULT file.
- Not a lever: `doc.TableRow` is about 0.15 GB in total (PAGE about -59%). `doc.CellValue` PK is already `PAGE` (query 0). ROW compression of the CellValue PK was estimated larger than current (76,272 -> 129,672 KB); irrelevant, do not use ROW there.

### B. Rationale

The remaining cold cost of the T3_00 star is physical IO (cold 42.3 s vs warm 5.1 s; CPU is about 12 s of 61 s in the pre-fix run, see the NOTE). The working hypothesis is PAGEIOLATCH wait, and it is still unconfirmed (the NOTE section 4 asks for the wait stats). `calc.CalculationResult` is read through `IX_CalculationResult_DocRun` and `IX_CalculationResult_Lookup`, which are uncompressed. About one fifth of the pages means about one fifth of the cold page reads and read-ahead for those indexes, and a smaller buffer-pool footprint. Expected gain is proportional to CalculationResult's share of cold IO; that share in variant B is measured only as 562 physical and 11,171 read-ahead pages (NOTE section 1), so the cold-time gain is unknown until measured. This is secondary to the CellValue set-read option A in the NOTE and does not replace it.

### C. Plan (not executed)

1. New databases: in `src/Ecr.Infrastructure/Persistence/Sql/07-partition-tables.sql` the `@map` row for `calc.CalculationResult` has `CompressPk = 0`, and the move cursor compresses only clustered indexes (`CASE WHEN m.CompressPk = 1 AND i.type = 1 THEN PAGE ELSE NONE`). Two non-clustered indexes are created later in the same file without `DATA_COMPRESSION` (`IX_CalculationResult_Version`, `IX_CalculationResult_DocRun`), and the D-401 `DROP_EXISTING` step also omits it, so it would reset an already compressed index to NONE. The change: PAGE for all CalculationResult indexes in all three places (a per-table all-index flag, or `DATA_COMPRESSION = PAGE` added to the three explicit statements). The cursor skips indexes already on the scheme, so this alone does nothing for existing databases.
2. Existing databases (Land and any upgraded copy): one migration/upgrade step, idempotent, per partition n = 1..25:
   `ALTER INDEX <ix> ON calc.CalculationResult REBUILD PARTITION = n WITH (DATA_COMPRESSION = PAGE)`
   for each of the five indexes, skipping partitions whose `sys.partitions.data_compression_desc` is already `PAGE` and empty partitions. Add `ONLINE = ON` only if the edition supports it (see risks). The step belongs in the upgrade path next to the other `-Upgrade` maintenance-window steps in `07-partition-tables.sql`, or as an EF migration; that choice and the migration itself belong to the migration-token holder. Not created in this change.
3. The upgrade check (main -> SHA, twice, second run 0 changes) must be extended to assert `data_compression_desc = 'PAGE'` on all five indexes after the first run.

### D. Risks

- Edition / ONLINE: the repo comments say Standard edition has no ONLINE builds (`07-partition-tables.sql`, lines about "Standard без ONLINE"). The Land edition is UNKNOWN (not confirmed in the files read). Without ONLINE, each partition rebuild blocks writes to that partition. Verify the edition and version with `SELECT SERVERPROPERTY('Edition'), SERVERPROPERTY('ProductVersion')` before planning.
- Free space: a rebuild needs room for the new copy per partition plus sort space (tempdb if `SORT_IN_TEMPDB`). Largest partition size on Land is not recorded; rough size of the 202606 partition is 265 MB uncompressed (estimate output). Disk rules in CLAUDE.md apply (H: or F:).
- Write cost: PAGE compression adds CPU on insert/update/recalc into CalculationResult, which is a write-heavy table during recalculation. Magnitude UNKNOWN; must be measured with the patch/recalc benchmark (I could not locate a `patchbench` script in the repo files searched, so the exact tool is to be named by its owner).
- Log growth: rebuild is fully logged under FULL recovery; size UNKNOWN, measure on the scratch copy.
- Interaction with the planned CellValue set-read change (option A): independent; do not combine in one measurement, or the effects cannot be separated.
- Later rebuilds that omit `DATA_COMPRESSION` silently revert an index to NONE; see plan item 1.

### E. Verification gates (all on the scratch copy first, never on live)

1. Before: cold T3_00 star (OFFLINE/ONLINE the copy as in the NOTE), 3 runs, with STATISTICS IO per table and PAGEIOLATCH_SH wait time; record index sizes (`sys.dm_db_partition_stats`).
2. Rebuild one partition (202606), timed; record log used, tempdb and free-space delta; then all partitions.
3. After: same cold/warm T3_00 runs; result sets must be unchanged (EXCEPT both ways vs the Before output, as in Audit-2). Index sizes must be within the estimate (-79% +/- 10 points, otherwise stop and report).
4. Write benchmark before/after (recalculation of a loaded period and the patch benchmark): accept only if the regression is within an agreed bound (the bound is a decision for the coordinator; not set here).
5. Upgrade check (main -> SHA twice + smoke + idempotent) green with the new step.
6. Go on only if the cold gain is material against the 15 s budget; otherwise leave as is (the saving is about 1.8 GB on Land, which alone does not justify the write-CPU risk).

### F. Rollback

Per partition: `ALTER INDEX <ix> ON calc.CalculationResult REBUILD PARTITION = n WITH (DATA_COMPRESSION = NONE)`, same space and log needs as the forward step. Revert the `07-partition-tables.sql` change in git for new databases. No data is changed by compression, only the physical page format.

### G. Execution authority

Nothing runs on live (Land or any production database) from this addendum. Each application step (scratch-copy rebuild, any live rebuild, migration creation) needs a new explicit human word; this document records the plan and the evidence only.
