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

## Уточнення 2026-10-28 (lane/integ-q17-rvseek: читання rv за актуальними прогонами)

- **Чому старий план читав усю таблицю.** Похідна rv не обмежувала `ru.PeriodKey` періодом екземпляра `ti.PeriodKey`, тож відсічка прогону (`Status = Current`) стояла НАД seek-ом: оптимізатор шукав `IX_CalculationResult_DocRun` (ключ `PeriodKey, DocumentId, CalculationRunId`) лише по двох ключах (`PeriodKey, DocumentId`) і читав усі прогони документа за період, включно з неактуальними (Land: 4,47 млн рядків; на синтетиці форми Land - 23 040 читань при 11 520 рядках актуальних прогонів).
- **Виправлення.** До наборового rv і до підзапиту `rr` гілки з предикатом MatchJson додано `AND ru.PeriodKey = ti.PeriodKey`; у наборовій гілці зʼєднання `calc.CalculationRun ru -> calc.CalculationResult cr` стало `INNER LOOP JOIN`. Результат записується з `periodKey = run.PeriodKey` (`CalculationResultStore`), тож семантика та сама, зокрема для проєктного прогону без `DocumentId` (запасний варіант діє на рівні періоду, `ru2.PeriodKey = ti.PeriodKey` не змінено). Після виправлення: seek по трьох ключах, 11 520 прочитаних рядків = рядкам актуальних прогонів, з предикатом `_PeriodKey` одна партиція.
- **Чому підказка.** Лише фільтр періоду нестабільний: оптимізатор і далі міг обрати план з відсічкою прогону над seek-ом; `LOOP` змушує йти від актуального прогону до його результатів (`ru` - зовнішній вхід), як і `HASH` для прив'язок. Ціна підказки - порядок зʼєднань у цьому apply зафіксований.
- Тести: `ReportViewSliceEquivalenceTests` (стара форма == нова EXCEPT в обидва боки = 0 і вʼюха == справжній зріз для обох гілок rv і обох значень прапора), `ReportViewLandShapeTests` (seek по трьох ключах, `ActualRowsRead` <= 1,3 x рядків актуальних прогонів).

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

## Addendum 2026-10-28: Deleting results of superseded runs

Status: PROPOSAL. Analysis only: nothing was run, migrated or deleted, and no database was connected. Code and scripts were read at `origin/dev/integration` `daeb9decd`. Basis: human decision 28.10, verbatim in `QUESTIONS-BUSINESS.md` section 0.6 (keep only the latest calculation per period). Row counts below come from section 0 item 3 (Land copy EcrPassC_Q17: 4,466,558 rows, 88 % in Superseded runs); every time, log and size figure for a deletion is an EXTRAPOLATION and is marked as such.

### A. Verdict and reader inventory

**No product reader depends on rows of non-Current runs.** Every reader selects results through `calc.CalculationRun.Status = 'Current'` (`CurrentRunIdsAsync` or a join), so Superseded rows are dead weight. Before deleting, three things need work:

1. Two read paths are safe today only because rows are never deleted (R1, R2 below): both read run ids and rows in separate statements.
2. One guard changes meaning (R12, `strictBackdated`).
3. The retention text lists `calc.CalculationResult` and the recorded trace as never erased (D-25, D-71 in `docs/tz/10-decisions.md`; `docs/tz/04-data.md` section 4.8; ФВ-9.11, ФВ-9.13 in `02-requirements.md`). The 28.10 decision overrides it in substance, but needs a D-record in `docs/tz` (not touched here; `SpecificationBaselineTests` checksum applies). `04-data.md:206` already says reproducibility comes from the submission snapshot, not from stored steps.

Hard constraint: **`calc.CalculationRun` rows stay** (R5, R7, R19).

| # | Reader | File:line | Uses non-Current rows? | What changes if they are gone | Verdict |
|---|---|---|---|---|---|
| R1 | `ReadCurrentCoreAsync` (slice overlay, results panel, Excel/export via `CalculatedCellOverlay`) | `CalculationResultStore.cs:701-771`, ids `:810-835` | No. But run ids and rows are two statements | A switch + delete committed between them gives an empty or partial read (today: the full previous result, see the remark at `:652-659`) | **needs change**: re-read run ids after the rows, retry up to 2x if they differ |
| R2 | `CarryOverResultsAsync` | `CalculationResultStore.cs:515-626` (source `:556-561`) | No (Current runs only), but separate statements, default RCSI | A concurrent switch that deletes the source run between the count and the load copies a partial set into the new Current run: silent loss | **needs change**: UPDLOCK the source run rows in the same transaction before reading, keep until commit |
| R3 | `SwitchCurrentRunAsync` | `CalculationResultStore.cs:411-512` | Writes status only | The feature itself (design B) | **needs change** |
| R4 | `ReadCurrentRunProfileAsync` | `:774-793` | Runs only | none | safe |
| R5 | `InvalidateReportSnapshotsAsync` | `:358-400` | Runs only | none, if runs stay | safe |
| R6 | `ReportSnapshotBuilder.AggregateAsync` | `ReportSnapshotBuilder.cs:1226-1266` | No (one statement, Current filter) | none; cheaper | safe |
| R7 | `ReportSnapshotStaleness` | `ReportSnapshotStaleness.cs:102-107` | Reads `CalculationRun` rows incl. `Superseded` with `ErrorMessage IS NULL` | Deleting run rows would break "snapshot is stale" | safe **only while run rows stay** |
| R8 | generated `rpt.v_*` views | `Sql/05-rpt-views.sql:494-506`, `:532-539` | No (`ru.Status = 'Current'`) | none; cheaper (the 88 % noise is gone) | safe |
| R9 | `StaleResultsQuery` | `StaleResultsQuery.cs:76-82, 92-106, 131-137` | No (join to Current runs; `used` subquery `:100-105` scans all runs' rows first) | none; cheaper | safe |
| R10 | `GetCalculationFreshnessAsync` | `MethodologyStore.cs:546-553`, registry part `:583-594` | No (Current run; second statement keyed by that run id) | Same two-statement gap as R1, effect is a transient missed "registry changed" flag | safe (accepted) |
| R11 | `RegistryImpactStore` | `RegistryImpactStore.cs:92-95` | No (`Status == Current`); `RegistryImpactStoreTests.cs:76` uses a Superseded run as negative control | none | safe |
| R12 | `AnyCalculationResultsAsync` -> `RejectBackdatedStrictAsync` | `MethodologyStore.cs:685-707`, `PublishMethodologyHandler.cs:565-603` | **Yes, by design: "any status"** (`IMethodologyStore.cs:265`, `docs/release-notes/rc-2026-12-01.md:40`) | A version whose only rows were superseded no longer blocks publishing Strict backdated. Current and `arc` rows still block | **decision** (see E), then update doc/test (`MethodologyResultsUsageStoreTests.cs:47` comment, expectations) |
| R13 | `MethodologyVersionDeletionStore.LockAsync` / `EnsureDeletable` | `MethodologyVersionDeletionStore.cs:31-41`, `MethodologyVersion.cs:226-252` | Yes (any row) | Only Draft versions are deletable (`:228`), and runs skip unpublished versions (G14 note in `RecalculationJob.cs`, `skippedUnpublished`), so a Draft has no rows: no practical change | safe |
| R14 | unit "in use" check | `UnitStore.cs:258` | Yes (any row) | A unit used only by superseded rows becomes deletable; `FK_CRes_Unit` still protects the Current rows | safe (accepted) |
| R15 | `DocumentDeletionStore` | `DocumentDeletionStore.cs:204-256` | Deletes by document and by run | Fewer rows to delete; relies on run rows (`:187`) | safe |
| R16 | FKs | `CalculationsConfiguration.cs:600-606, 625, 665`; migration `20260904232328_Stage4Calculations.cs:78,105,176` | `FK_CRes_Run`, `FK_CIn_Run`, `FK_CStep_Run` point TO the run; `FK_CRes_MV`, `FK_CRes_Unit` point to version and unit; **no FK points to `CalculationResult`** (grep `principalTable` finds none); `CalculationStep.ResultId` (`CalculationTrace.cs:120`) is a plain column | Deleting results violates nothing; step rows of a deleted result keep a dangling `ResultId` | safe |
| R17 | archive | `Sql/12-archive-tables.sql:121`; `ArchiveJob.cs`, `arc.usp_ArchiveYear` (`Sql/03-archive-proc.sql`) | Only `doc.*` is moved; no writer of `arc.CalculationResult` found in the repo (UNKNOWN whether DBA tooling fills it) | none | safe |
| R18 | submission / approval snapshots (D-67, D-69) | `DocumentVersionStore.cs:37-80`, `SubmitSheetHandler.cs:343-346` | **Confirmed: payload holds cells only**, no result rows. A submitted sheet shows its bound number by live link to the Current run | none | safe |
| R19 | `ReportSnapshot.CalculationRunId` | `ReportDefinitions.cs:140` | Provenance column, no FK in the model | none, if run rows stay | safe |
| R20 | seed `COLL:period0-supersede` and its test | `Sql/09-seed.sql:24519-24527`, `Period0SupersedeSeedTests.cs:81-84` ("history stays", HSE301) | Seed only flips run status; test asserts the rows stay | Unaffected as long as the cleanup does NOT ship in the seed (it runs at every start). Add a comment that the HSE301 rule is replaced by this addendum | safe |
| R21 | `AbandonedWorkSweeper.CloseCalculationRunsAsync` | `AbandonedWorkSweeper.cs:295-320` | `Running` runs only | Failed runs keep partial rows: add to the purge scope | safe |
| R22 | ops scripts | `tools/land/postcheck/PostCheck.sql:182-206, 347-353, 357`; `WindowExtra.sql:202-213` | Count results/steps of ALL runs since a date (not product readers) | Counts and the `BRANCH_BOTH` trace probe lose the earlier runs. `Compare-LandParity.ps1:171-172`, `PostCheck.sql:62-74, 259-269` use Current only | safe; run PostCheck BEFORE the live cleanup and keep its output |
| R23 | client | `Ecr.Web` calls only `/calculation-results` and the run-status endpoint (`DocumentsController.cs:544, 557`) | No run-history screen | none | safe |
| R24 | tests that seed Superseded rows directly | `CalculationResultReadCurrentHistoryTests`, `Q17SliceReadModelTests.cs:147`, `ReportViewSliceEquivalenceTests.cs:325`, `ReportViewMonthOwnershipTests.cs:641` | They simulate a database that is not cleaned yet | Stay valid as pre-cleanup simulations. No test found that switches runs and then asserts old-run rows (`CalculationResultCarryOverTests.cs:62` asserts 0 rows of a refused target) | safe |

Not product readers but in scope of the same rule: `calc.CalculationStep` and `calc.CalculationInput` of superseded runs have **no reader at all** (grep: only the writer, `DocumentDeletionStore`, and the ops probes above). Their volume on Land is UNKNOWN (depends on `TraceLevel` per version). Recommendation: same rule, same job, separate flag, decided after the counts are known (E).

### B. Design: when and how rows are deleted

1. **Scope rule (single invariant):** results may exist only for runs with status `Current` or `Running`. Superseded, Failed and any other terminal status are garbage. `calc.CalculationRun` is never deleted (who/when, `ErrorMessage`, staleness).
2. **Not inside the switch transaction (deviation from the first idea, with reasons).** The switch transaction holds the job-row fence lock and is retried on `CompletionBusy`. Deleting there adds a whole previous run: up to 50,000 rows per document-period (the read ceiling `MaxResults`, `:640`), so up to about 250k index-row deletes for one document run over 5 indexes, and more for a project-wide run that holds every document of the period (sizes per run UNKNOWN), with lock escalation (`calc.CalculationResult` has no `LOCK_ESCALATION` setting, so escalation goes to the TABLE and blocks inserts of any concurrent run; readers are not blocked under RCSI). A failed delete would also roll back the switch. Instead:
   - `SwitchCurrentRunAsync` keeps doing only the status flip, and returns the ids of runs it superseded (including its own id in the `newer` branch, `:481-494`). Port change: `Task<IReadOnlyList<long>>`.
   - **Fast path, after commit** (`RunCalculationHandler.CompleteAsync`, after `ExecuteInTransactionAsync` returns): `PurgeRunResultsAsync(periodKey, runIds, documentId?)`, bounded batches, each its own short transaction. If it fails or is cut off, the switch is already correct and the **sweep** (3) finishes the job.
   - Atomicity is not needed: after the status flip the rows are invisible to every reader (A).
   - If the human prefers literally the same transaction: possible, but only with a cap (for example 5,000 rows) and `LOCK_ESCALATION = AUTO` (partition-level); the remainder still needs the sweep, so the invariant is eventual either way.
3. **Batch form:** `DELETE TOP (@batch)` per `PeriodKey` (one run belongs to one partition, `RecalculationJob.cs:212-228`), key `(PeriodKey, CalculationRunId IN (...))`; for a document run add `DocumentId` so the plan is a seek on `IX_CalculationResult_DocRun (PeriodKey, DocumentId, CalculationRunId)`; for a project run it is a scan of one partition (about 21k rows after cleanup, about 179k before; EXTRAPOLATED average). Batch default **500 rows** (about 5 locks per row over 5 indexes stays under the 5,000-lock escalation threshold; the measured precedent in `DocumentVersionMigrationStore.cs:44-49` shows escalation is unavoidable once a statement passes about 5,000 locks). `SET DEADLOCK_PRIORITY LOW`, lock timeout 5 s, retry on 1205 (3 times), sleep 20-50 ms between batches.
4. **Sweep job** (backstop and the engine of the one-time cleanup): modelled on `SubmissionSnapshotRetentionJob` (`MaintenanceRun`, public `RunAsync(options)`, dry-run by default, `MaxBatchesPerRun`, recovery-model warning). Per partition, keyset over the clustered key `(PeriodKey, Id)`:
   `DELETE TOP (@batch) cr FROM calc.CalculationResult cr JOIN calc.CalculationRun ru ON ru.Id = cr.CalculationRunId WHERE cr.PeriodKey = @p AND cr.Id > @last AND cr.Id <= @hi AND ru.Status NOT IN (N'Current', N'Running');` then advance `@last`. A plain `DELETE TOP` without the key range would re-scan the kept 12 % at the start of the partition on every batch.
   Covers: Failed runs (after `AbandonedWorkSweeper` closes them, 2 h), runs whose fast path was cut off, period 0 (legacy year-run rows, see R20), and the first big cleanup.
5. **Config:** `Calculation:PurgeNonCurrentResults:Enabled` (fast path and sweep) and `...:DeleteEnabled` (sweep only; false = count and log what would be deleted). Default **false** in product config, true in test hosts. Rationale: an upgrade must not start erasing Land history on the first recalculation without the human's window and a backup (same rule the snapshot retention job already follows).
6. **Rows left on purpose:** rows of a project-wide Current run for documents that also have their own Current document run are shadowed (never read) but belong to a Current run, so they stay until the next project-wide run supersedes that run. Size UNKNOWN, bounded by one run.
7. **Monitoring:** `ConsistencyCheckJob` rule `CALC_RESULT_NON_CURRENT` (count of rows whose run is not Current/Running, expected 0 after the sweep interval) writes `aud.ConsistencyIssue`.

### C. One-time cleanup of existing databases

- **Where it ships:** the sweep job above, not `09-seed.sql` (the seed runs at every start; the precedent `COLL:period0-supersede` flips a handful of statuses, it does not move millions of rows) and not an EF migration (no schema change, no migration token). No contract change. The runbook (`docs/admin/operations-runbook.md`) gets one step after the DB upgrade: dry-run, then enabled run in a window.
- **Pre-checks printed by the job (and abort conditions):** status histogram of `calc.CalculationRun` (abort on a status outside `Current, Running, Superseded, Failed` unless confirmed: `Succeeded`/`Cancelled` appear in tests and ops scripts, not in product code; presence on Land UNKNOWN), recovery model, free log and data space, count of victim rows per partition.
- **Procedure per partition, lowest `PeriodKey` first, partition 0 included:** keyset loop (B4), batch 500, each batch its own transaction. Resumable and idempotent by construction: progress is the data itself (a rerun finds fewer victims); `MaintenanceRun.DetailsJson` records partition, last Id, rows deleted for the report. `MaxBatchesPerRun` bounds one tick; the next tick or a manual rerun continues.
- **Log control:** SIMPLE recovery: `CHECKPOINT` every N batches. FULL recovery: the job reads `sys.dm_db_log_space_usage` and pauses above a threshold (default 60 % used) until log backups catch up; the DBA's log-backup cadence is part of the window. RCSI keeps row versions in tempdb for the duration of each batch only, because batches are short.
- **Expected volume on Land (EXTRAPOLATED from 4,466,558 rows, 88 % superseded):** about 3.93 M rows deleted, about 0.54 M kept (matches the "about 0.55 M" figure). At 500 rows per batch that is about 7.9k batches. Time: UNMEASURED; at an assumed 5k-20k deleted rows per second with 5 indexes, about 3-13 min of pure delete plus pauses. Log: about 2.6-3 GB (index data 2.24 GB x 88 % = 1.97 GB of row images x 1.3-1.5 overhead; UNMEASURED). Both must be measured on a scratch copy before the live window.
- **After the delete:** space is not returned by itself. Run `ALTER INDEX ... REBUILD PARTITION = n` for the five indexes (about 21k rows per partition afterwards, so cheap), then `UPDATE STATISTICS`. Expected used size 2.24 GB -> about 0.27 GB (EXTRAPOLATED, only after the rebuild). Do not shrink files.
- **Index design after cleanup:** unchanged, no migration. `IX_CalculationResult_DocRun` stays (the Current seek and the shadowed project-run rows still need `(PeriodKey, DocumentId, CalculationRunId)`). Re-measure the other four indexes later; dropping any is out of scope here. **Interaction with the PAGE-compression addendum above:** the cleanup shrinks the target from about 1.8 GB saved to a few hundred MB, so do the cleanup first, then re-evaluate compression; if compression still goes ahead, do it in the same per-partition rebuild.
- **Order with other steps:** PostCheck output saved (R22) -> full backup -> dry-run counts -> enabled run -> rebuild -> statistics -> smoke / e2e -> turn the fast path on.

### D. Test plan

1. **Invariant test (Infrastructure, real SQL Server):** after a scripted sequence of switches (document run, project run, out-of-order "newer" branch, sheet-scope carry-over chain A -> B -> C, a Failed run), `SELECT COUNT(*) FROM calc.CalculationResult cr JOIN calc.CalculationRun ru ON ru.Id = cr.CalculationRunId WHERE ru.Status NOT IN ('Current','Running')` is 0, and `ReadCurrentAsync`, the slice overlay, `ReportSnapshotBuilder` and the `rpt.v_*` view return the same values as before the purge (golden compare). Mutation proof: remove the purge call -> red.
2. **Run rows stay:** `ReportSnapshotStalenessTests` and `CalculationRunSweepTests` stay green; add an assertion that run count is unchanged by the purge.
3. **Concurrency:** R1 (switch + purge injected between the run-id read and the row read, via a command interceptor) returns the full NEW result, not empty/partial; R2 (concurrent switch while `CarryOverResultsAsync` loads) never copies a partial set (second session blocked by the UPDLOCK on the source run). Repeat 20x (CLAUDE.md rule for timing/concurrency tests; "N/20" in the commit).
4. **Purge unit/integration:** batch size 1, 2 and larger than the data; mixed statuses; `Running` untouched; period 0; second run deletes 0 (idempotent); cancel mid-run then rerun completes (resumable); `DeleteEnabled=false` counts only and deletes nothing; unknown status aborts; log-threshold pause (fake `sys.dm_db_log_space_usage` reader).
5. **Behaviour change tests:** `strictBackdated` allowed when only superseded rows existed (now purged), blocked when a Current or `arc` row exists (`MethodologyResultsUsageStoreTests`, `MethodologyStrictBackdatedApiTests`); unit and methodology-version "in use" checks.
6. **Existing tests to adjust:** `Period0SupersedeSeedTests.cs:81-84` comment; `MethodologyResultsUsageStoreTests.cs:47` comment; `CalculationResultReadCurrentHistoryTests` stays as the "not yet cleaned" scenario, plus one steady-state variant.
7. **Upgrade check (main -> SHA twice, smoke, idempotent):** add a case where the `main` database already holds Superseded rows; after the upgrade and a purge run the invariant holds, smoke is green, a second purge run changes 0 rows.
8. **Process:** destructive data change, so mutation proof (CLAUDE.md "mutation proofs only for security, destructive, data...") and the full CI on a `lane/integ-*` branch, not the fast lane CI.

### E. Open risks and what needs a human word

- **Irreversible.** After the purge the old numbers cannot be shown again; what remains is the submission snapshots (cells), `aud.CellChange`, run metadata and the Current results. Recomputing an old state works only while the inputs and the methodology version are unchanged.
- **Policy text.** D-25/D-71/ФВ-9.11/ФВ-9.13 contradict the decision until a D-record amends them (owner of `docs/tz`; the checksum test needs the new sum in the same commit).
- **R12 behaviour change** (`strictBackdated` no longer sees superseded history): a human word is needed, one word: accept (default proposed) or keep a stricter guard (then a separate marker would be required, which is a migration).
- **Steps and inputs of superseded runs:** same rule or leave? Needs the Land counts first (UNKNOWN).
- **Live cleanup = separate window and a full backup**, with the DBA log-backup cadence, services up or down at the human's choice (the design is online, but recalculations of the affected periods should be paused). No default is assumed. Recovery model, edition, free space and the log size on Land are UNKNOWN.
- **Default of the flag** in product config (proposed: false until the window).
- **Shadowed project-run rows** (B6) stay; size UNKNOWN.
- **Unknown statuses** on Land (`Succeeded`, `Cancelled`): checked by the job before it deletes anything.
- **No measurement was done** for time, log, tempdb, lock behaviour or plans; all of it is to be measured on a scratch copy first (not on EcrPassC_Q17 or any stand without a new word).

Size of the change: one lane, no migration, no contract change; about 500-700 lines of diff with tests at 40 % or more (store method and port change, two race fixes, purge job, config, `ConsistencyCheckJob` rule, runbook step).
