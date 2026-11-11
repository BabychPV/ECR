# Аудит 07 — архівація, звітний шар для SSRS, життєвий цикл

- **Область:** шар `rpt.*` для SSRS (`05-rpt-views.sql`: `rpt.v_DocumentCells`, `rpt.v_DocumentCellsByPermit`, дозвільні вʼюхи, генератори `rpt.usp_GenerateTemplateViews` / `rpt.usp_GenerateStableViews`, ролі `rpt_reader` / `ecr_viewer`), `ReportViewGenerator`, `ReportViewsHealthCheck`, зрізи `rpt.ReportSnapshot` (лише в частині «що бачить SSRS»), архівація `arc.usp_ArchiveYear` / `arc.usp_RestoreYear` (`03-archive-proc.sql`, `12-archive-tables.sql`), `ArchiveJob`, `ArchiveAwareCellReader`, перенос версії (`MigrateDocumentVersionHandler`, `DocumentVersionMigrationStore`) у частині, що зачіпає звіти й архів.
- **SHA `main`:** `39aea41d9` (робоча копія `F:/ecr-wt/audit-dec`). **Дата:** 2026-11-11.
- **✎ 2026-11-11 (після рішень PO):** R-1 — варіант А (архів у стабільних вʼюхах, `rpt.v_DocumentCells`, `rpt.v_DocumentCellsByPermit`; `_vN` — ні); R-3 — варіант 3 (заборона публікації зміни родини типу; наявні конфлікти — попередження в health). Обидві переписано до впровадження; у R-6 виправлено вибір тестового періоду; R-7 узгоджено.
- **Охоплено повністю:** `05-rpt-views.sql` (усі 1656 рядків), `03-archive-proc.sql` (усі 1181 рядок), `ArchiveJob.cs`, `ReportViewGenerator.cs`, `ReportViewsHealthCheck.cs`, `B16-ecr-reporting.md`, рішення `D-14/16/23/24/53/65/66/71/117/123/380/396`.
- **Поверхово:** `ArchiveAwareCellReader`, `RowStore` (лише гілка F-13), `ReportSnapshotBuilder` (лише джерело агрегації), `RecalculationService.TranslateForeignInstancesAsync`, `VersionMigrationPlanner` (лише мапа перейменувань), `usp_ArchiveAudit`.
- **Не охоплено:** продуктивність вʼюх на реальних обсягах (плани, sniffing параметрів SSRS), тривалість кроку 2 архівації під Sch-M (потребує стенду; `D-117`, `TODO-REMAINING` B3.3), самі RDL.
- **Уже відомо (не дублюю):** `D-12` звіту 02 (`usp_RestoreYear` не звіряє й не прибирає частини місяця). Твердження брифу «позначки `CellFormulaOverride` лишаються на місці під час архівації» застаріле: коміт `65112112e` (10.11, борг «ar») переносить їх у `arc.CellFormulaOverride` зі звіркою (`03-archive-proc.sql:456-460, 587-597, 624`). Попередня умова з `docs/delivery/handover-2026-11-01-pi.md:146` («не обрізати без arc-дзеркала») виконана.

## Відповіді на питання PO

- **Чи правильно працює архівація?** Так, у частині збереження даних. Процедура спершу копіює, потім звіряє, і лише після збігу звільняє партиції однією транзакцією. Повтор після збою, перекриття діапазонів і спільні партиції сусідніх проєктів закриті й покриті тестами (див. «Перевірено»). Є три застереження. (1) Запускає лише DBA вручну: кнопки, задачі чи API немає (`RecurringScheduleService.cs:352-354`). (2) Час блокування всієї бази на кроці 2 не виміряно (`D-117`). (3) Звірка порівнює лише кількість і суму чисел, тож зміна тексту чи прапора рядка між копією й видаленням загубиться ([R-6](#r-6)).
- **Чи бачить SSRS заархівований рік?** Ні. Після `usp_ArchiveYear` рік зникає з усіх «сирих» вʼюх (`rpt.v_DocumentCells`, `rpt.v_<…>_vN`, стабільних `rpt.v_<…>`, `rpt.v_DocumentCellsByPermit`). Залишаються лише зрізи `rpt.ReportSnapshot`. **✎ Рішення PO 11.11 — варіант А:** стабільні вʼюхи, `rpt.v_DocumentCells` і `rpt.v_DocumentCellsByPermit` показуватимуть архівний рік прозоро з колонкою `_IsArchived`/`IsArchived`; `_vN` — лише живі дані ([R-1](#r-1)).
- **Чи може автор SSRS один раз узяти вʼюху й не перейматися новою назвою?** Так, якщо брати **стабільну** вʼюху `rpt.v_<Шаблон>_<Аркуш>_<Таблиця>` (без `_vN`). Вона переживає нову версію шаблону, додані колонки (жодна не зникає), нові періоди, копії аркушів і перейменування колонки, коли перенос виконано з мапою. Вʼюхи `_vN` для цього не годяться: після переносу проєкту його дані переходять у вʼюху нової версії. Стабільна вʼюха ламається в трьох випадках: перейменування таблиці ([R-2](#r-2)), зміна типу колонки між версіями ([R-3](#r-3); ✎ PO 11.11: зміну родини типу заборонено при публікації) і архівація ([R-1](#r-1); ✎ PO 11.11: архів показується прозоро).
- **Чи не злітають права SSRS при перегенерації?** Ні. Право `SELECT` видано на всю схему `rpt` (`05-rpt-views.sql:1633`). Вʼюхи змінюються через `CREATE OR ALTER` і ніколи не видаляються. Обліковий запис SSRS (`rpt_reader`) не має доступу до `doc.*` і `sec.*` напряму, це перевірено тестом.
- **Чи отримає звіт назви з довідників і підписи ru/kz?** Ні. SSRS бачить лише коди колонок і числові Id записів довідників. Назва аркуша в `_SheetDisplayName` лише англійською ([R-4](#r-4)).
- **Повний життєвий цикл:** перенос версії заархівованого проєкту заборонено. Видалити можна лише чернетку проєкту. Повернення року з архіву робить тільки DBA. Документа «контракт для автора звітів» немає, а частина довідкових текстів застаріла ([R-7](#r-7)). Права служби дозволяють писати й видаляти в `arc.*` напряму ([R-5](#r-5)).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| R-1 | High | correctness / reporting | Заархівований рік зникає з усіх сирих вʼюх SSRS | L | високий | ні (✎ ухвалено PO 11.11: варіант А) |
| R-2 | Medium | correctness / reporting | Перейменування таблиці при переносі розщеплює стабільну вʼюху: старе ім'я перестає показувати дані проєкту | M | середній | ні |
| R-3 | Medium | correctness / reporting | Стабільна вʼюха бере тип колонки з ОСТАННЬОЇ версії: зникають `<Код>_Text`/`<Код>_Id`, старі рядки `Calculated` → NULL, конфлікт типу → `nvarchar` | M | середній | ні (✎ ухвалено PO 11.11: варіант 3 — заборона публікації) |
| R-4 | Medium | i18n / reporting | Немає шару довідників і підписів для SSRS: Lookup — лише Id, назви ru/kz недоступні | S | низький | ні |
| R-5 | Low | security | Обліковий запис служби (`db_datawriter`) може писати й видаляти в `arc.*` напряму | S | низький | ні |
| R-6 | Low | data-integrity | Звірка архівації — лише COUNT + SUM чисел (D-23 вимагає три суми): нечислова правка між копією й TRUNCATE губиться | S | низький | ні |
| R-7 | Low | docs | Немає контракту SSRS для автора звітів; застарілі тексти (Calculated = NULL, D-396, «запустіть лише usp_GenerateTemplateViews») | S | низький | ні |

---

<a id="r-1"></a>
### R-1 — High — Заархівований рік зникає з усіх сирих вʼюх SSRS
**✎ рішення PO 11.11:** варіант **(А)** — сирі вʼюхи SSRS показують заархівовані роки прозоро з `arc.*`, з позначкою-колонкою. Обсяг: стабільні вʼюхи `rpt.v_<Шаблон>_<Аркуш>_<Таблиця>` (генератор `rpt.usp_GenerateStableViews`) — колонка `_IsArchived bit`; довга `rpt.v_DocumentCells` і `rpt.v_DocumentCellsByPermit` — колонка `IsArchived bit` (у цих вʼюх службові колонки без `_`: там немає колонок-кодів шаблону, з якими треба розводити імена, `05-rpt-views.sql:55-75`, `:1522-1536`). Кожна — `UNION ALL` гарячої гілки (`doc.*`, текст як зараз + `0`) і архівної (`arc.*`, той самий текст із заміною трьох-чотирьох таблиць + `1`), з однаковими списками й типами колонок. ⚠ Вʼюхи `_vN` (`rpt.usp_GenerateTemplateViews`) у варіанті А, який затвердив PO, не змінюються: за вимкненого прапора Q-17 вони читають кожну колонку окремим `LEFT JOIN doc.CellValue` (`:401-406`), і друга гілка подвоїла б кількість таблиць у запиті, на межу якої вже розраховано лічильник `@n + @extra > 250` (`:687-693`; коментар `:409-410`: «ліміт SQL Server 256 таблиць у запиті»). Таблиця Land на 232 колонки (`:372`) не вмістилась би. Стабільний контракт для авторів звітів — стабільні вʼюхи (`D-53`); `_vN` лишаються «лише живі дані», це документується (крок 7). Якщо PO захоче архів і в `_vN` — окрема знахідка (архівна гілка з одним `OUTER APPLY` замість `c<n>`), не в цьому PR.

**Категорія:** correctness / reporting (контракт `rpt.*`, `D-53`, `ФВ-10.12`).
**Де:**
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:51-52` — коментар «Лише ЖИВІ дані `doc.*`…»; `:53-92` — `rpt.v_DocumentCells` (лише `doc.CellValue` / `doc.TableRow` / `doc.TableInstance`, `:76-78`);
- `:1290-1297` (`@fromSql`), `:1312-1327` (текст стабільної вʼюхи) — ті самі `doc.*`; `:950` — оголошення змінних;
- `:1519` — коментар «Лише живі дані `doc.*`» над `rpt.v_DocumentCellsByPermit` (`:1520-1621`; гарячі таблиці на `:1538-1540`, `:1580-1583`, `:1601`, `:1615`);
- `:714-721` — `FROM` вʼюх `_vN` (не змінюються, див. ✎);
- `:1624-1633` — права `rpt_reader` (лише `SELECT ON SCHEMA::rpt`), коментар про ланцюг власності;
- `src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql:614-625` — `TRUNCATE … WITH (PARTITIONS)` шести гарячих таблиць; `12-archive-tables.sql:40-85, 89-105, 263-272` — `arc.TableInstance/TableRow/CellValue/CellPermitPart`;
- `docs/build/TESTER-GUIDE.md:1489-1490` — обмеження записане як відоме.

**Проблема:** архівація переносить рядки з `doc.*` в `arc.*` і звільняє гарячі партиції. Жодна вʼюха `rpt.*` не читає `arc.*`:
```sql
FROM doc.CellValue          AS cv
JOIN doc.TableRow           AS r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0
JOIN doc.TableInstance      AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
```
Застосунок читає архів прозоро (`ArchiveAwareCellReader.cs:8-22, 33-52`, `RowStore.cs:957-970`), а SSRS — ні. Рішення `D-nnn` не було; обмеження суперечить `B16-ecr-reporting.md:134-137` (A-9 «архів — прозорим») і `B02-persistence.md:404-415` (§6.4 «Прозоре читання»).

**Сценарій відтворення:**
1. Проєкт 2025 позначено `Archived`. DBA: `EXEC arc.usp_ArchiveYear @ProjectId, 202501, 202512`.
2. Звіт SSRS: `SELECT … FROM rpt.v_<Т>_<А>_<Т> WHERE _PeriodKey BETWEEN 202501 AND 202512`.
3. Фактично: 0 рядків, без помилки. Очікувано (рішення PO 11.11): ті самі рядки, що до архівації, з `_IsArchived = 1`.
4. Після `arc.usp_RestoreYear` рядки повертаються (`_IsArchived = 0`), повторна архівація знову ховає — після фіксу не ховає.

**Вплив:** будь-який звіт SSRS за рік, старший за поріг архівації: річні порівняння, звірка з уже поданими формами (`ФВ-10.9`). Зрізи `rpt.ReportSnapshot`/`rpt.ReportRow` не зачеплені: не партиційовані й архівацією не чіпаються, а `ReportSnapshotBuilder` агрегує з `calc.CalculationResult` (`ReportSnapshotBuilder.cs:1222-1235`), який лишається гарячим.

**Чому рядок не дублюється (доведено з процедур архіву).** Екземпляр таблиці лежить або в `doc.*`, або в `arc.*` у сталому стані, але проміжні й аварійні стани існують:
- **Успішна архівація:** копія (крок 1, поза транзакцією, `03-archive-proc.sql:342-490`), далі ОДНА транзакція «звірка під `TABLOCKX` → `TRUNCATE` гарячих партицій» (`:511-635`) — після `COMMIT` діапазон лише в `arc.*`.
- **Успішне повернення:** копія в `doc.*` тими самими `Id` (`:808-815`: `INSERT … SELECT a.PeriodKey, a.Id, …`, `NOT EXISTS` по ключу), далі ОДНА транзакція «звірка → `DELETE FROM arc.*` діапазону» (`:943-1008`) — після `COMMIT` діапазон лише в `doc.*`.
- **Проміжні стани (обидві схеми одночасно):** весь крок 1 архівації (до `COMMIT` на `:635`); збій 50010 (`:468-481`) або 50014 (`:611`, відкат лише транзакції кроку 2) лишає копію в `arc.*`, а оригінал у `doc.*` — до повтору (`:321-340` прибирає таку копію); увесь крок 1 повернення (пакети комітяться, `:747-922`); збій повернення — Z6-01 прибирає з `doc.*` лише тотожні екземпляри, екземпляри з розбіжністю лишаються в обох схемах (`:1046-1092`).
Тому архівна гілка бере лише екземпляри, яких НЕМАЄ в `doc.*`: `AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id)`. Ключ надійний: повернення зберігає `Id` екземпляра (`:808-815`), Id — із послідовності (`EcrDbContext.cs:361-362`). Це те саме правило F-13, що в застосунку: гаряча копія має перевагу (`03-archive-proc.sql:760-769`). Рядки й комірки належать екземпляру, тож вибір на рівні екземпляра виключає дублі й на рівні рядка. Нових гарячих екземплярів заархівованого року з іншим `Id` бути не може: правки архівного проєкту заблоковано (`EditRules.cs:200-202`), `RowStore` не створює екземплярів в архівному році (`RowStore.cs:957-970`).

**Права (`rpt_reader` і далі не бачить `arc.*`).** `rpt_reader` має лише `SELECT ON SCHEMA::rpt` (`05-rpt-views.sql:1631-1633`). Вʼюхи створює процедура `WITH EXECUTE AS OWNER` (`:853`), статичні вʼюхи — скрипт; власник вʼюхи — власник схеми `rpt`. Читання `arc.*` крізь вʼюху проходить ланцюгом власності, якщо власник схеми `arc` той самий. Схеми створюються без явного власника тим самим обліковим записом розгортання в одному прогоні: `rpt` — міграція EF (`20260905000751_Stage5Integration.cs:20-21`, `EnsureSchema`), `doc` — `20260904182730_InitialCreate.cs:20-21`, `arc` — `12-archive-tables.sql:37` (`CREATE SCHEMA arc` без `AUTHORIZATION`); порядок — `12-archive-tables.sql` ДО `05-rpt-views.sql` у кожному шляху (`tools/deploy-ecr.ps1:2095-2099`, `tests/Ecr.TestKit/SqlServerFixture.cs:539`, `:557`). Тож власник однаковий (той самий механізм уже тримає `doc`/`cfg`/`wf`, коментар `:1624-1628`). Прямий `SELECT FROM arc.*` під `rpt_reader` і далі дає 229 — доводить тест кроку 9. Сторож власників схем — у тому ж тесті (якщо колись `arc` створять іншим обліковим записом, ланцюг порветься і SSRS отримає 229 на архівній гілці).

**Відсікання партицій / сегментів (обидві гілки).** Предикат на колонку вʼюхи (`_PeriodKey` / `PeriodKey`) SQL Server проштовхує в кожну гілку `UNION ALL`; обидві гілки віддають її простим посиланням на колонку (`r.PeriodKey` / `ti.PeriodKey` / `cv.PeriodKey`), без виразу. Гаряча гілка — як зараз: відсікання партицій `ps_ByPeriodKey`. Архівна: `arc.*` не партиційована (`03-archive-proc.sql:305-308`), це clustered columnstore (`12-archive-tables.sql:49, 64, 83`); `PeriodKey` відсікає групи рядків за метаданими min/max сегментів (копія лягає пакетами по періоду, `03-archive-proc.sql:344-391`). `NOT EXISTS` — seek по ключу `doc.TableInstance (PeriodKey, Id)` (PK, `EcrDbContextModelSnapshot.cs:4218`), виконується лише для архівних екземплярів; на порожньому `arc.*` архівна гілка коштує сканування порожніх колонсторів. Реальну ціну на обсягах Land треба заміряти на стенді (крок 10).

**Зміна — покроково:**
1. `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql`, коментар `:51-52`:
   ```diff
   --- ⚠ Лише ЖИВІ дані `doc.*`: роки, винесені в архів (`arc.usp_ArchiveYear`),
   --- сюди не потрапляють.
   +-- ✎ R-1 (рішення PO 11.11, D-R1): гаряча гілка (`doc.*`, IsArchived = 0) UNION ALL архівна (`arc.*`, IsArchived = 1) -
   +-- лише екземпляри, яких немає в `doc.*` (проміжні стани архівації/повернення: гаряча копія має перевагу, як F-13).
   +-- Архівна гілка - ДОСЛІВНО гаряча із заміною `doc.CellValue`/`doc.TableRow`/`doc.TableInstance` на `arc.*` (сторож
   +-- ReportArchiveBranchTests). Колонка IsArchived - останньою.
   ```
2. Той самий файл, `rpt.v_DocumentCells` (`:53-91`) — новий текст (рядки `:55-75` і `:79-90` переносяться ДОСЛІВНО, нижче скорочено як «…без змін…» лише там, де текст тотожний):
   ```sql
   CREATE OR ALTER VIEW rpt.v_DocumentCells
   AS
   SELECT <рядки :55-74 без змін>
          (SELECT CONCAT(… рядки :71-75 без змін …) AS SheetDisplayName,
          CAST(0 AS bit) AS IsArchived
   FROM doc.CellValue          AS cv
   JOIN doc.TableRow           AS r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0
   JOIN doc.TableInstance      AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
   <рядки :79-90 без змін>
   WHERE cv.IsEmpty = 0
   UNION ALL
   SELECT <рядки :55-74 без змін>
          (SELECT CONCAT(… рядки :71-75 без змін …) AS SheetDisplayName,
          CAST(1 AS bit) AS IsArchived
   FROM arc.CellValue          AS cv
   JOIN arc.TableRow           AS r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0
   JOIN arc.TableInstance      AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
   <рядки :79-90 без змін>
   WHERE cv.IsEmpty = 0
     AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id);
   GO
   ```
   Тобто: до кінця списку колонок (після `AS SheetDisplayName`, `:75`) дописати `,\n       CAST(0 AS bit) AS IsArchived`; `WHERE cv.IsEmpty = 0;` (`:91`) → без `;`; далі `UNION ALL`, копія всього `SELECT … WHERE cv.IsEmpty = 0` із заміною рівно трьох підрядків `doc.CellValue ` → `arc.CellValue `, `doc.TableRow ` → `arc.TableRow `, `doc.TableInstance ` → `arc.TableInstance ` (з пробілом після — тоді `doc.DocumentSheet`, `doc.Document`, `doc.Project` не зачіпаються), `CAST(0 AS bit)` → `CAST(1 AS bit)` і рядок `NOT EXISTS` у кінці. Типи колонок двох гілок ідентичні: колонки `arc.*` збігаються з `doc.*` за типом (`12-archive-tables.sql:43-48, 56-63, 71-82, 265, 272` vs `EcrDbContextModelSnapshot.cs:3184-3247, 4190-4289`: `ValueString nvarchar(1000)`, `ValueNumeric decimal(34,16)`, `ValueDate datetime2(3)`, `ValueRegistryEntryId int`, `RowKey nvarchar(100)`, `Ordinal int`, `SheetCopyId bigint`), решта колонок — з тих самих `cfg.*`/`doc.Document`/`doc.Project`/`wf.*`/`uom.*` в обох гілках.
3. Той самий файл, `rpt.v_DocumentCellsByPermit` (`:1520-1621`), за тим самим правилом:
   - коментар `:1519` «⚠ Лише живі дані `doc.*`; …» → «✎ R-1 (D-R1): гаряча гілка UNION ALL архівна (`arc.*`), колонка `IsArchived` останньою; архівна гілка — дослівно гаряча із заміною `doc.CellValue`/`doc.TableRow`/`doc.TableInstance`/`doc.CellPermitPart` на `arc.*`. Status — колонка, як у rpt.v_DocumentCells. PermitSource: 'row' | 'header'.»;
   - після `CAST(COALESCE(a.Status, 0) AS tinyint) AS Status` (`:1536`) дописати `,\n       CAST(0 AS bit) AS IsArchived`;
   - `;` у кінці `:1621` прибрати, далі `UNION ALL` і копія всього `SELECT … AND (rp.ColumnDefId IS NOT NULL OR hp.HeaderFieldDefId IS NOT NULL)` (`:1522-1621`, з коментарями всередині) із замінами: `doc.CellValue ` → `arc.CellValue ` (`:1538`, `:1583`), `doc.TableRow ` → `arc.TableRow ` (`:1539`, `:1581`), `doc.TableInstance ` → `arc.TableInstance ` (`:1540`, `:1580`), `doc.CellPermitPart ` → `arc.CellPermitPart ` (`:1601`, `:1615`), `CAST(0 AS bit) AS IsArchived` → `CAST(1 AS bit) AS IsArchived`; наприкінці — рядок `  AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id);`.
   - НЕ замінюються (не архівуються, `03-archive-proc.sql:614-625`): `doc.Document`, `doc.Project`, `doc.DocumentSheet`, `doc.Period`, `doc.PermitSegment`, `doc.DocumentHeaderValue`, `dic.RegistryEntry`, `cfg.*`, `wf.ApprovalState`. `arc.CellPermitPart` має ті самі колонки, що читає вʼюха (`12-archive-tables.sql:89-105`).
4. Той самий файл, `usp_GenerateStableViews`: оголошення після `:950`:
   ```diff
       DECLARE @select nvarchar(max), @selectText nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
   +    -- R-1 (D-R1): спільна голова SELECT і хвіст FROM..WHERE; архівний хвіст - той самий із заміною трьох таблиць на arc.*.
   +    DECLARE @selHead nvarchar(max), @tail nvarchar(max), @tailArc nvarchar(max);
   ```
   і текст вʼюхи `:1312-1327`:
   ```diff
   -        SET @sql = CONCAT(
   -N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
   -AS
   -', @marker, N': ', @header, N'. Не редагувати руками.
   -SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
   +        SET @selHead = CONCAT(
   +N'SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
           d.Id AS _DocumentId, d.BusinessKey AS _DocumentKey,
           ', @pkExpr, N' AS _PeriodKey,
           CAST(COALESCE(a.Status, 0) AS tinyint) AS _Status,
           r.Id AS _RowId, r.RowKey AS _RowKey, r.Ordinal AS _RowOrdinal,
   -       ', @verCase, N' AS _TemplateVersion', @select, @copyCols, N'
   -', @fromSql, N'
   +       ', @verCase, N' AS _TemplateVersion', @select, @copyCols, N',
   +       CAST(');
   +        SET @tail = CONCAT(N' AS bit) AS _IsArchived
   +', @fromSql, N'
    LEFT JOIN wf.ApprovalState AS a ON a.DocumentId = d.Id AND a.SheetDefId = ', @sheetCase,
    N' AND a.PeriodKey = ti.PeriodKey
                                   AND (a.SheetCopyId = ti.SheetCopyId OR (a.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
    LEFT JOIN doc.DocumentSheet AS dsh ON dsh.Id = ti.SheetCopyId', @joins, N'
   -WHERE ti.TableDefId IN (', @tids, N');');
   +WHERE ti.TableDefId IN (', @tids, N')');
   +        -- R-1: заміна лише в хвості (FROM/JOIN/APPLY/WHERE): у ньому немає літералів із назвами (імена вʼюх у @vnLit
   +        -- санітизовано до [A-Za-z0-9_], :903-907), тож REPLACE зачіпає тільки імена таблиць. doc.Document, doc.Project,
   +        -- doc.Period, doc.DocumentSheet, wf.*, calc.*, cfg.*, rpt.ViewSlot не архівуються (03-archive-proc.sql:614-625).
   +        SET @tailArc = REPLACE(REPLACE(REPLACE(@tail,
   +                           N'doc.TableInstance', N'arc.TableInstance'),
   +                           N'doc.TableRow', N'arc.TableRow'),
   +                           N'doc.CellValue', N'arc.CellValue')
   +                     + N'
   +  AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id)';
   +        SET @sql = CONCAT(
   +N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
   +AS
   +', @marker, N': ', @header, N'. Не редагувати руками.
   +', @selHead, N'0', @tail, N'
   +UNION ALL
   +', @selHead, N'1', @tailArc, N';');
   ```
   Що міняє заміна в хвості: `@fromSql` (`doc.TableInstance AS ti`, `doc.TableRow AS r` або `CROSS APPLY … FROM doc.TableRow AS r0`, `:1291-1297`, `:1211-1212`), `pc` (`FROM doc.CellValue AS x`, `:1172-1174`), власники `fam` (`doc.TableInstance AS oi`, `doc.TableRow AS ow`, `doc.CellValue AS x`, `:1186-1192`), `sc` (`:1201`), предикат прив'язки `rv` (`doc.CellValue AS m`, `:1278`). У `@selHead` (`@copyCols` — підзапит `doc.DocumentSheet`, `:1305-1310`) заміни немає, і не треба. Колонка `_IsArchived` — останньою (після `_SheetCopyOrdinal`/`_SheetDisplayName`), позиції наявних колонок не зсуваються. Ліміт 1000 колонок (`:1161`) не зачеплено (кількість колонок та сама); таблиць у запиті вдвічі більше (~15-30 на гілку: один `apply pc` на всі колонки, `:1170-1174`), до межі SQL Server далеко. Текст процедури змінюється → змінюється відбиток (`:131-133`) → на першому старті стабільні вʼюхи перегенеруються один раз.
   Оновити шапку-коментар RPT-STABLE (`:832-846`): додати пункт «`* archive : R-1 (D-R1) - UNION ALL over arc.* for instances absent from doc.* (_IsArchived = 1); hot copy wins`».
5. `src/Ecr.Infrastructure/Persistence/Sql/12-archive-tables.sql` — **лише за результатом заміру на стенді (крок 10).** Якщо архівна гілка повільна (`apply pc` на рядок по колонстору без B-tree), наприкінці файла додати:
   ```sql
   -- R-1: seek архівної гілки rpt.v_* по (період, рядок) і (період, екземпляр); на порожньому arc.* - миттєво.
   IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'arc.CellValue') AND name = N'IX_arc_CellValue_Row')
       CREATE NONCLUSTERED INDEX IX_arc_CellValue_Row ON arc.CellValue (PeriodKey, TableRowId, ColumnDefId) ON [DATA_ARCHIVE];
   IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'arc.TableRow') AND name = N'IX_arc_TableRow_Instance')
       CREATE NONCLUSTERED INDEX IX_arc_TableRow_Instance ON arc.TableRow (PeriodKey, TableInstanceId) ON [DATA_ARCHIVE];
   GO
   ```
   На базі, де `arc.*` уже заповнено, будувати з `ONLINE = ON` на Enterprise (`SERVERPROPERTY('EngineEdition') IN (3, 5, 8)`, як `12-archive-tables.sql:351-352`) і перевіряти `INDEXPROPERTY`, як `AN34CollectionCoverageDedupIndex`. Індекс сповільнює `INSERT … WITH (TABLOCK)` у колонстор на кроці 1 архівації (`03-archive-proc.sql:378, 405`) — заміряти разом.
6. **Health.** Нова залежність вʼюх від таблиць `arc.*`: на базі без `12-archive-tables.sql` (ручне накочення лише `05`) створення вʼюхи дає системну помилку 208, яку картка `reportviews` показує як «SQL Server error 208; the details are in the server log» без підказки (`ReportViewGenerator.cs:208-212`). `src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs`:
   ```diff
                 : $"SQL Server error {ex.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}; the details are in the server log (correlation {correlationId})."
   -              + (ex.Number == MissingProcedureError ? MissingProcedureHint : string.Empty);
   +              + (ex.Number == MissingProcedureError ? MissingProcedureHint
   +                 : ex.Number == MissingObjectError ? MissingObjectHint : string.Empty);
   ```
   і поруч із `MissingProcedureError` (`:215`):
   ```csharp
       /// <summary>✎ R-1: SQL Server 208 «Invalid object name» — вʼюхи читають таблицю, якої в базі немає (архів arc.*).</summary>
       private const int MissingObjectError = 208;

       /// <summary>✎ R-1: без імен обʼєктів (SEC TIER2) — що виконати оператору.</summary>
       private const string MissingObjectHint =
           " A table the report views read is missing from this database (archive tables from 12-archive-tables.sql): "
           + "the Sql scripts of this release were not applied in order. Run deploy-ecr.ps1 with the schema step "
           + "(without -SkipSchema, runbook section 8) and restart the application.";
   ```
   Текст картки `health.reportviews.ok` не змінюється. (Тексти `failed` з порадою лише про `usp_GenerateTemplateViews` — R-7, крок 2.)
7. Документація.
   - `docs/build/TESTER-GUIDE.md:1489-1490`: «лише над **живими** даними `doc.*` (роки в архіві — `arc.*` — не потрапляють);» → «стабільні `rpt.v_<Шаблон>_<Аркуш>_<Таблиця>`, `rpt.v_DocumentCells` і `rpt.v_DocumentCellsByPermit` показують і заархівовані роки (з `arc.*`): колонка `_IsArchived` / `IsArchived` = 1; під час архівації чи повернення рядок не дублюється — показується гаряча копія (D-R1); вʼюхи `_vN` — лише живі дані `doc.*`;».
   - `docs/admin/admin-guide.md`: у розділі для авторів звітів (R-7 крок 1 створює `## 13. Звітність SSRS: контракт rpt.*`; якщо R-7 ще не злито — створити розділ із цим підрозділом, R-7 доповнить) — підрозділ «Архівні роки»: (а) стабільні вʼюхи, `rpt.v_DocumentCells`, `rpt.v_DocumentCellsByPermit` віддають рік і після `arc.usp_ArchiveYear`; `_IsArchived`/`IsArchived = 1` — рядок з архіву; (б) фільтр `_PeriodKey` завжди (відсікання партицій гарячої гілки й сегментів архівної); (в) вʼюхи `_vN` архіву не показують; (г) архівний рік не показує стилів комірок і позначок ручних значень (вони теж в `arc.*`, вʼюхи їх не читають); (д) під час архівації/повернення рік видно без дублів (гаряча копія має перевагу).
   - `docs/admin/operations-runbook.md` §7 — замість речення R-7 «до рішення R-1 рік зникає з сирих вʼюх» (R-7 крок 5) писати «рік лишається видимим у стабільних вʼюхах і `rpt.v_DocumentCells*` з `_IsArchived = 1` (D-R1); `_vN` — лише живі дані».
   - `docs/tz/10-decisions.md` — новий рядок після `D-474` (`:911`), номер — наступний вільний (нижче `D-R1`), + рядок журналу змін (за зразком `:1139`), + `docs/CHECKSUMS.txt:11` (`git add docs/tz/10-decisions.md && git show :docs/tz/10-decisions.md | sha256sum`):
     ```
     | **D-R1** | **✎ 2026-11-11. Сирі вʼюхи SSRS показують заархівовані роки прозоро (A-9, §6.4).** Стабільні `rpt.v_<Шаблон>_<Аркуш>_<Таблиця>` (колонка `_IsArchived`), `rpt.v_DocumentCells` і `rpt.v_DocumentCellsByPermit` (колонка `IsArchived`) — `UNION ALL` гарячої гілки (`doc.*`, 0) і архівної (`arc.*`, 1) з тими самими колонками й типами; архівна бере лише екземпляри, яких немає в `doc.*` (проміжні стани архівації/повернення: гаряча копія має перевагу, як F-13). Вʼюхи `_vN` — лише живі дані (ліміт таблиць у запиті). `rpt_reader` і далі має лише `SELECT` на схему `rpt`; `arc.*` читається ланцюгом власності. | рішення PO 11.11 (аудит 2026-11, `R-1`, варіант А, через координатора) | `R-1` · `D-53` · `D-66` · `ФВ-10.12` |
     ```
8. Наявні тести, що фіксують список чи кількість колонок стабільної вʼюхи або кількість `COALESCE(a.Status, 0)` у файлі:
   - `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.cs:62-65` — до списку колонок дописати `"_IsArchived"` останньою; `:102-104` — `ab.Length + 13` → `ab.Length + 14`; `:472` — дописати `"_IsArchived"` останньою; `:475` — `13` → `14`.
   - `tests/Ecr.Architecture.Tests/StatusNumberingTests.cs:72` — `Assert.Equal(4, … COALESCE\(a\.Status, 0\) …)` → `6` (статичні `v_DocumentCells` і `v_DocumentCellsByPermit` тепер мають по дві гілки в тексті файла; генератори `_vN` і стабільної — по одній, бо архівний текст стабільної збирається з тієї самої голови); коментар `:70-71` доповнити «(довга й за дозволом — двома гілками, R-1)».
   - Golden `_vN` (`ReportViewMonthOwnershipTests.cs:249-291`) і `ReportViewGeneratorTests.cs:46-55` (колонки `_vN`) не змінюються: `_vN` не зачеплено.
9. Нові тести:
   - `tests/Ecr.Architecture.Tests/ReportArchiveBranchTests.cs` (сторож дослівності статичних гілок; читання файла — як `StatusNumberingTests.Sql()`, `:22-23`): `[Theory] [InlineData("rpt.v_DocumentCells", new[] {"doc.CellValue ", "doc.TableRow ", "doc.TableInstance "})] [InlineData("rpt.v_DocumentCellsByPermit", new[] {"doc.CellValue ", "doc.TableRow ", "doc.TableInstance ", "doc.CellPermitPart "})]`: нормалізувати `\r\n` → `\n`; узяти тіло від `CREATE OR ALTER VIEW {view}\nAS\n` до наступного `\nGO`; розбити по `\nUNION ALL\n` — рівно 2 частини; очікувана архівна = гаряча з кожною заміною `doc.X ` → `arc.X `, `CAST(0 AS bit) AS IsArchived` → `CAST(1 AS bit) AS IsArchived`, плюс `\n  AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id)`; порівняти з другою частиною без кінцевого `;`. Червоніє на будь-якій розбіжності копії.
   - `tests/Ecr.Infrastructure.Tests/Jobs/RestoreYearTests.cs` (наявні помічники `DocumentAsync`, `CellAsync`, `ArchiveAsync`, `RestoreAsync`, `:355-404`) — новий `[Fact] Архівний_рік_видно_у_вʼюхах_SSRS_з_позначкою_і_без_дублів_після_повернення`, період `202706` (не вживається іншими тестами архіву/повернення: `git grep -n "const int period" -- tests/Ecr.Infrastructure.Tests/Jobs` — 202705, 202709-202712, 202805; `ArchiveAsync` позначає архівними всі проєкти періоду, `:382-394`):
     1. `doc = DocumentAsync(202706)`; `CellAsync(doc, 0, 1, 5.25m)`; опублікувати версію (`version.Publish(1, Now)` + `SaveChangesAsync`, як `ReportViewGeneratorTests.cs:239-245`); `new ReportViewGenerator(db).GenerateAsync(doc.TemplateVersionId, ct)`; ім'я стабільної вʼюхи — `v_{template.Code}_{doc.SheetCode}_{table.Code}` після заміни `[^A-Za-z0-9_]` → `_` (як `ReportViewStableTests.cs:782-786`).
     2. До архівації: `SELECT COUNT(*), MAX(CAST(_IsArchived AS int)) FROM rpt.[<stable>] WHERE _DocumentId = {doc.DocumentId}` → `(1, 0)`; `rpt.v_DocumentCells WHERE DocumentId = … AND ValueNumeric IS NOT NULL` → 1 рядок, `IsArchived = 0`.
     3. `ArchiveAsync(doc.ProjectId, 202706, 202706)` → у `doc.CellValue` цього рядка 0 записів; стабільна → `(1, 1)`, значення колонки = 5.25; `v_DocumentCells` → 1 рядок, `IsArchived = 1`.
     4. Проміжний стан «обидві схеми»: від власника скопіювати екземпляр, рядки й комірки з `arc.*` назад у `doc.*` тими самими `Id` (SQL — як `INSERT` кроку 1 `usp_RestoreYear`, `03-archive-proc.sql:808-882`, з фільтром `PeriodKey = 202706 AND Id/TableInstanceId = doc.TableInstanceId`) → стабільна `(1, 0)` — рівно один рядок, гарячий.
     5. `RestoreAsync(doc.ProjectId, 202706, 202706)` (повтор після часткової копії пропускає наявне, `NOT EXISTS`) → `arc.CellValue` за 202706 порожній; стабільна `(1, 0)`.
     6. Права: `IF USER_ID(N'rpt_probe') IS NULL CREATE USER rpt_probe WITHOUT LOGIN; ALTER ROLE rpt_reader ADD MEMBER rpt_probe;` (як `ReportViewGeneratorTests.cs:149-158`), знову заархівувати (`ArchiveAsync`), `EXECUTE AS USER = N'rpt_probe'`: стабільна → 1 рядок з `_IsArchived = 1`; `SELECT COUNT(*) FROM arc.CellValue` → `SqlException.Number == 229`; `REVERT`. І від власника: `SELECT COUNT(DISTINCT principal_id) FROM sys.schemas WHERE name IN (N'rpt', N'doc', N'arc')` = 1.
     Без кроків 2-4 кроку «Зміна»: на кроці 3 тесту — 0 рядків (і помилка імені `_IsArchived`); без `NOT EXISTS` — на кроці 4 два рядки.
   - `tests/Ecr.Infrastructure.Tests/Reporting/DocumentCellsByPermitViewTests.cs` — новий `[Fact] Заархівований_екземпляр_видно_з_IsArchived_і_без_дублів`: на `BuildAsync(tag)` (`:183`) і даних першого кейсу — імітація архівації ОДНОГО екземпляра без процедури (період `202601` спільний з іншими тестами, `TRUNCATE` його не можна): від власника `INSERT INTO arc.TableInstance/TableRow/CellValue/CellPermitPart SELECT … FROM doc.* WHERE` екземпляр кіта; перевірити, що вʼюха віддає ті самі рядки рівно раз із `IsArchived = 0`; потім `DELETE` з `doc.CellPermitPart` (за документом кіта) → `doc.CellValue` → `doc.TableRow` → `doc.TableInstance` (FK лише два, `07` «Перевірено»: `FK_TableRow_Instance`, `FK_CellValue_Row`) → ті самі рядки (ті самі `PermitId`, `ValueNumeric`, частини) з `IsArchived = 1`. Наприкінці прибрати рядки кіта з `arc.*` (`DELETE … WHERE DocumentId/TableInstanceId = …`), щоб не зачепити тести архіву.
   - `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorFailureTextTests.cs` — кейс `SafeMessage` для 208 (як наявний для 2812, `:30-48`): текст містить «12-archive-tables.sql» і не містить імені обʼєкта.
10. **Замір на стенді (обовʼязковий, до злиття):** копія бази Land (або `tools/br07-load-test.ps1`-набір), рік архівований.
    1. `SET STATISTICS IO, TIME ON;` до й після розгортання R-1, на НЕзаархівованому році: `SELECT COUNT(*), SUM(<числова колонка>) FROM rpt.[v_<Land-таблиця T3_00>] WHERE _PeriodKey BETWEEN <рік>01 AND <рік>12;` — час і логічні читання; критерій: не гірше ніж на 10 % (архівна гілка для живого року відсікається сегментами; у плані «Actual Number of Rows» архівної гілки = 0).
    2. Той самий запит на ЗААРХІВОВАНОМУ році: результат `COUNT`/`SUM` = значенням до архівації (записати їх перед `usp_ArchiveYear`); `MIN(_IsArchived) = 1`. Записати час.
    3. `SELECT *` по одному документу й періоду архівного року (типовий звіт SSRS) — час; критерій прийнятності — у межах бюджету звіту SSRS, погодженого для живих даних (Q-17.perf: секунди, не десятки секунд).
    4. Якщо пункт 2/3 > 5× часу того самого запиту до архівації або план показує `Table Scan`/`Columnstore Index Scan` `arc.CellValue` на кожен рядок (Nested Loops з великим числом виконань) — додати індекси кроку 5, повторити 1-3 і заміряти тривалість кроку 1 архівації (`usp_ArchiveYear`) з індексами.
    5. Те саме для `rpt.v_DocumentCellsByPermit WHERE PeriodKey BETWEEN …` на архівному році.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ReportViewStableTests|FullyQualifiedName~ReportViewGeneratorTests|FullyQualifiedName~ReportViewSlotMapTests|FullyQualifiedName~ReportViewStartupSkipTests|FullyQualifiedName~ReportViewMonthOwnershipTests|FullyQualifiedName~DocumentCellsByPermitViewTests|FullyQualifiedName~RestoreYearTests|FullyQualifiedName~ArchiveJobTests|FullyQualifiedName~DeployScriptsRerunTests|FullyQualifiedName~ReportViewGeneratorFailureTextTests"`; `dotnet test tests/Ecr.Architecture.Tests --filter "FullyQualifiedName~StatusNumberingTests|FullyQualifiedName~ReportDefinitionTests|FullyQualifiedName~ReportArchiveBranchTests|FullyQualifiedName~SpecificationBaselineTests"`. Бюджетні тести `…не_читає_більше_ніж_vN_плюс_20_відсотків` (`ReportViewStableTests.cs:309, 373`) мусять лишитися зеленими: стабільна отримує архівну гілку, а `_vN` — ні; на спільній тестовій базі в `arc.*` лежать рядки тестів архіву (сотні), тож гілка додає кілька сторінок колонстору — менше за запас 20 %. Якщо тест почервоніє — це сигнал кроку 10.4, а не привід піднімати стелю. Стенд — крок 10.

**Ризики і побічні ефекти:**
- Текст процедури й статичних вʼюх змінюється → перегенерація стабільних вʼюх на першому старті (`ALTER VIEW` під Sch-M, ~секунди на вʼюху).
- Кількість таблиць у запиті стабільних вʼюх подвоюється (≈15 → 30, з маршрутизацією ≈25 → 50) — до межі далеко; `_vN` не зачеплено якраз через цю межу.
- Під час кроку 2 архівації (Sch-M на `doc.CellValue`/`doc.TableRow`, `03-archive-proc.sql:507-509`) читання вʼюх чекає, як і зараз (`D-117`).
- Частково повернений екземпляр (збій `usp_RestoreYear`) показується гарячою частиною, як у застосунку (F-13), доки Z6-01 його не прибере (`03-archive-proc.sql:1046-1142`).
- Власник схеми `arc`, відмінний від `rpt`, зламав би ланцюг власності (229 на архівній гілці для `rpt_reader`) — сторож у тесті кроку 9.
- Архівна гілка `fam` (маршрутизація Q-17) шукає власників року в `arc.*`; якщо DBA архівував лише частину року, власники з гарячої частини того самого року для архівних рядків не знайдуться (значення — з власної копії, як за вимкненого прапора). Архівація за роками (`ArchiveJob`, `03-archive-proc.sql` приклади) робить цей випадок нештатним; задокументувати в R-7.

**Обґрунтування:** варіант А дає «одну назву назавжди» (A-9, §6.4) і не потребує нових імен вʼюх. Відкинуті: Б — окремі вʼюхи `…_arc` (друга назва, порушує «збудуй раз»); В — union-вʼюхи `doc.*∪arc.*` на рівні таблиць (зачіпає всі генератори, `_vN` і ліміт 250; без B-tree на колонсторі кожен `OUTER APPLY` сканує сегменти); Г — «звіт за архівний рік лише після повернення» (PO відхилив). Дублі прибирає `NOT EXISTS` по екземпляру, а не стан журналу `itg.ArchiveRun`: так само вирішує застосунок (F-13), і так вʼюха правильна навіть після обриву процедури, коли журнал лишається `Running`.

**Рішення PO:** ухвалено 11.11 — варіант А. Відкрите лише одне: якщо PO хоче архів і у `_vN` — окрема знахідка (див. ✎).
**Впевненість:** дефект і безпечність щодо дублів/прав підтверджено читанням; продуктивність архівної гілки — потребує перевірки на стенді (крок 10).
**Залежності:** лінія `05-rpt-views.sql`: R-3 → R-2 → R-4 → **R-1** (усі правлять `usp_GenerateStableViews` або сусідні блоки; R-1 — останньою, бо змінює спосіб складання тексту вʼюхи, а R-2/R-3 — його вміст). R-7 (документація) — після R-1. `10-decisions.md` + `CHECKSUMS.txt` спільні з R-3, звітами 08 (X-2) і 09 (V-7).

---

<a id="r-2"></a>
### R-2 — Medium — Перейменування таблиці при переносі розщеплює стабільну вʼюху
**Категорія:** correctness / reporting.
**Де:**
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:880-887`: група стабільної вʼюхи = (шаблон, **код** аркуша, **код** таблиці);
- `:1010-1015`: псевдоніми колонок беруться лише в межах `@ci` групи (`JOIN @ci AS a … JOIN @ci AS b`);
- `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1351-1357`: `RenamePairs` зберігає лише пари колонок з **різним кодом колонки**;
- `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1767-1772`: перенос перепризначає `doc.TableInstance.TableDefId` на нову таблицю;
- `:1908-1912`: запис `cfg.ColumnRename`;
- `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:290-321`: мапа `TableRenames` переводить таблицю «старий код → новий код»;
- `src/Ecr.Application/Recalculation/RecalculationService.cs:1862-1869`: міжперіодні формули шукають таблицю-відповідник лише за кодом.

**Проблема:** перенос версії офіційно підтримує перейменування таблиці (`VersionMigrationTableRename(FromTableCode, ToTableCode)`). Але таблиця-перейменування ніде не зберігається. `RenamePairs` пропускає колонки з тим самим кодом:
```csharp
&& !string.Equals(f.Code, t.Code, StringComparison.Ordinal))
```
Стабільна вʼюха групує за кодом таблиці, тому стара назва (`rpt.v_T_S_OLD`) веде лише TableDefId старих версій, а нова (`rpt.v_T_S_NEW`) — лише нових. Перенос усього проєкту (`@fromPeriod = 0`) перепризначає всі його екземпляри на нову таблицю. Після цього стара назва для цього проєкту порожня. Навіть коли колонки перейменовано мапою, псевдонім між різними групами відкидається (`:1011-1015`).

**Сценарій відтворення:**
1. Шаблон v1: аркуш `S`, таблиця `OLD` (колонка `X`). Проєкт P з даними 202601..202606. Звіт SSRS побудовано на `rpt.v_T_S_OLD`.
2. v2: таблицю перейменовано на `NEW`. `POST …/migrate-version` з `tableRenames: [{fromTableCode:"OLD", toTableCode:"NEW"}]`, режим «увесь проєкт».
3. Фактично: `SELECT … FROM rpt.v_T_S_OLD WHERE _ProjectId = P` повертає 0 рядків, а дані P є лише в `rpt.v_T_S_NEW`. При переносі «з місяця» (прапор `Documents:VersionMigration:FromMonthEnabled`) дані розщеплюються між двома вʼюхами, і міжперіодні формули поточної версії не бачать місяців старої таблиці (`RecalculationService.cs:1862-1869` відсіює екземпляр без відповідника за кодом).
4. Очікувано: за тим самим принципом, що для колонок (`ColumnRename.cs:4-7`: «колонка видна під обома іменами»), обидві назви показують дані обох версій.

**Вплив:** будь-який звіт SSRS на таблицю, перейменовану переносом. Сценарій штатний (`ФВ-7.5`, тести `VersionMigrationTableRenamesTests`). Дані не губляться, але звіт мовчки показує порожньо.

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1351-1357`: зберігати пари всіх колонок таблиці, чий **код таблиці** змінився. Метод стає `internal` для тесту:
   ```diff
   -    private static IReadOnlyList<(int FromColumnDefId, int ToColumnDefId)> RenamePairs(
   -        VersionMigrationPlan plan, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
   -        => [.. plan.Columns
   -            .Where(c => from.ColumnsById.TryGetValue(c.SourceColumnDefId, out var f)
   -                        && to.ColumnsById.TryGetValue(c.TargetColumnDefId, out var t)
   -                        && !string.Equals(f.Code, t.Code, StringComparison.Ordinal))
   -            .Select(c => (c.SourceColumnDefId, c.TargetColumnDefId))];
   +    /// <summary>
   +    /// RPT-STABLE Part B + R-2: пари «колонка → колонка» для <c>cfg.ColumnRename</c>: змінився код КОЛОНКИ або код ТАБЛИЦІ
   +    /// (таблицю перейменовано мапою) — тоді й колонки з тим самим кодом, щоб стабільна вʼюха й міжперіодні формули знайшли
   +    /// відповідник.
   +    /// </summary>
   +    internal static IReadOnlyList<(int FromColumnDefId, int ToColumnDefId)> RenamePairs(
   +        VersionMigrationPlan plan, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
   +    {
   +        var fromTables = from.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id, t => t.Code);
   +        var toTables = to.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id, t => t.Code);
   +        return [.. plan.Columns
   +            .Where(c => from.ColumnsById.TryGetValue(c.SourceColumnDefId, out var f)
   +                        && to.ColumnsById.TryGetValue(c.TargetColumnDefId, out var t)
   +                        && (!string.Equals(f.Code, t.Code, StringComparison.Ordinal)
   +                            || (fromTables.TryGetValue(f.TableDefId, out var ft) && toTables.TryGetValue(t.TableDefId, out var tt)
   +                                && !string.Equals(ft, tt, StringComparison.Ordinal))))
   +            .Select(c => (c.SourceColumnDefId, c.TargetColumnDefId))];
   +    }
   ```
   `SheetDef.Tables` (`SheetDef.cs:48`), `TableDef.Code` (`TableDef.cs:30`) і `ColumnDef.TableDefId` вже існують.
2. `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql`, процедура `usp_GenerateStableViews`. Групи, повʼязані через `cfg.ColumnRename`, ведуть спільний набір TableDefId: кожне ім'я класу отримує однаковий вміст.
   - після `:933` (`DELETE FROM @groups WHERE ViewName IN (SELECT Name FROM @clashNames);`) додати:
     ```sql
         -- R-2: групи одного шаблону, повʼязані мапою перейменувань (таблицю перейменовано переносом): транзитивне замикання.
         DECLARE @glink TABLE (GroupId int NOT NULL, Other int NOT NULL, PRIMARY KEY (GroupId, Other));
         INSERT INTO @glink (GroupId, Other)
         SELECT DISTINCT ga.GroupId, gb.GroupId
           FROM cfg.ColumnRename AS rr
           JOIN cfg.ColumnDef AS ca ON ca.Id = rr.FromColumnDefId
           JOIN cfg.ColumnDef AS cb ON cb.Id = rr.ToColumnDefId
           JOIN @gt AS ga ON ga.TableDefId = ca.TableDefId
           JOIN @gt AS gb ON gb.TableDefId = cb.TableDefId
          WHERE ga.GroupId <> gb.GroupId AND ga.TemplateId = gb.TemplateId;
         INSERT INTO @glink (GroupId, Other)
         SELECT l.Other, l.GroupId FROM @glink AS l
          WHERE NOT EXISTS (SELECT 1 FROM @glink AS z WHERE z.GroupId = l.Other AND z.Other = l.GroupId);
         WHILE 1 = 1
         BEGIN
             INSERT INTO @glink (GroupId, Other)
             SELECT DISTINCT x.GroupId, y.Other FROM @glink AS x JOIN @glink AS y ON y.GroupId = x.Other
              WHERE x.GroupId <> y.Other AND NOT EXISTS (SELECT 1 FROM @glink AS z WHERE z.GroupId = x.GroupId AND z.Other = y.Other);
             IF @@ROWCOUNT = 0 BREAK;
         END
         DECLARE @gx TABLE (TableDefId int NOT NULL PRIMARY KEY);
     ```
   - у циклі після `:985` (`DELETE FROM @cols;`):
     ```sql
             DELETE FROM @gx;
             INSERT INTO @gx (TableDefId)
             SELECT x.TableDefId FROM @gt AS x
              WHERE x.GroupId = @g OR x.GroupId IN (SELECT l.Other FROM @glink AS l WHERE l.GroupId = @g);
     ```
   - замінити фільтр групи на `@gx` у всіх місцях, де береться «таблиці групи»:
     ```diff
     -        SET @tids = STUFF((SELECT N',' + CAST(TableDefId AS nvarchar(10)) FROM @gt WHERE GroupId = @g ORDER BY TableDefId
     +        SET @tids = STUFF((SELECT N',' + CAST(TableDefId AS nvarchar(10)) FROM @gt WHERE TableDefId IN (SELECT TableDefId FROM @gx) ORDER BY TableDefId
     ```
     Так само в `:990` і `:992` (`FROM @gt WHERE GroupId = @g` → `FROM @gt WHERE TableDefId IN (SELECT TableDefId FROM @gx)`), `:995` (`WHERE GroupId = @g AND LayoutKind <> 0` → `WHERE TableDefId IN (SELECT TableDefId FROM @gx) AND LayoutKind <> 0`), `:997`, `:1006` і `:1304` (`x.GroupId = @g` → `x.TableDefId IN (SELECT TableDefId FROM @gx)`).
   Відбиток уже враховує `cfg.ColumnRename` (`:150-151`), тож нова мапа сама перегенерує вʼюхи на старті.
3. `src/Ecr.Application/Recalculation/RecalculationService.cs:1862-1869`: після пошуку за кодом додати пошук за мапою:
   ```diff
                .FirstOrDefault(t => !t.IsDeleted && string.Equals(t.Code, source.Table.Code, StringComparison.Ordinal));
   +            // R-2: таблицю перейменовано переносом — відповідник за збереженою мапою колонок (будь-яка колонка таблиці).
   +            target ??= source.Table.Columns
   +                .Select(c => forward.TryGetValue(c.Id, out var n) || backward.TryGetValue(c.Id, out n) ? n : (int?)null)
   +                .Where(n => n is not null && current.ColumnsById.ContainsKey(n.Value))
   +                .Select(n => current.ColumnsById[n!.Value].TableDefId)
   +                .Distinct()
   +                .Select(id => current.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == id && !t.IsDeleted))
   +                .FirstOrDefault(t => t is not null);
                if (target is null)
   ```
   Наявне зіставлення колонок за кодом (`:1873-1878`) після цього знаходить колонки з тим самим кодом, а перейменовані — через `forward/backward` (`:1879-1885`).
4. `docs/build/02-contracts.md:968-975`: у коментарі `IColumnRenameMap` дописати «і пари всіх колонок таблиці, чий код змінено мапою (R-2)».

**Тест-доказ:**
- `tests/Ecr.Application.Tests/Documents/VersionMigrationTableRenamesTests.cs`, кейс `RenamePairs_для_перейменованої_таблиці_включає_колонки_з_тим_самим_кодом`. План з мапою `T9a_03 → T9a_17a` (як у `З_мапою_дані_таблиці_переїжджають_під_новий_код`). `ColumnsById` знімків заповнюється з колонок таблиць, бо помічник `Version` (`:258-270`) дає порожній словник. Перевірка: `MigrateDocumentVersionHandler.RenamePairs(plan, from, to)` дорівнює `[(101, 201), (102, 202)]`. Без фіксу — порожньо.
- `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.cs`, кейс `Перейменована_таблиця_за_мапою_видна_під_обома_іменами`. v1 `S/OLD(X)`, v2 `S/NEW(X)`, дані 202609 у v1 і 202610 у v2. Без мапи: `v_T_S_OLD` має лише 202609. Додати `db.ColumnRenames.Add(new ColumnRename(v1.Cols["X"], v2.Cols["X"]))`, виконати `ExecStableAsync(v2.VersionId)`. Тепер обидві вʼюхи віддають 202609 і 202610. Без зміни процедури `v_T_S_OLD` дає лише 202609, і тест червоний.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~VersionMigration"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ReportViewStableTests|FullyQualifiedName~VersionMigration|FullyQualifiedName~Recalculation"`. На стенді: перенос із `tableRenames` → обидві стабільні вʼюхи віддають дані проєкту.

**Ризики і побічні ефекти:**
- Більше рядків у `cfg.ColumnRename`: колонки перейменованих таблиць, обмежено `MaxTableRenames = 100` × колонки.
- `ColumnRenameMap` читає `Take(100_000)` (`ColumnRenameMap.cs:19`), запас достатній.
- Перенос, виконаний **до** фіксу, мапи не має. Для старих переносів потрібен разовий SQL або повторний перенос (див. R-7, документація).
- Кількість запитів переносу не змінюється: той самий `INSERT … OPENJSON` (`DocumentVersionMigrationStore.cs:1908-1912`).

**Обґрунтування:** окрема таблиця `cfg.TableRename` потребувала б міграції EF і нового порту. Пари колонок уже несуть звʼязок «таблиця → таблиця» і читаються обома споживачами (вʼюхи, формули), тож нової схеми не треба.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** після R-3 (та сама процедура), до R-1.

---

<a id="r-3"></a>
### R-3 — Medium — Стабільна вʼюха бере тип колонки з ОСТАННЬОЇ версії
**✎ рішення PO 11.11:** варіант **(3)** — заборонити публікацію версії шаблону, у якій наявний код колонки змінює РОДИНУ типу зберігання (тоді стабільна вʼюха не ламається ніколи); автор створює новий код. Конфлікти, що вже є в базах замовника (опубліковані до цього рішення), лишаються, як є: стабільна вʼюха показує таку колонку `nvarchar`, а попередження тепер видно в `/health/ready` (крок 5). Кроки 1-2 (супутник `<Код>_Text` і накладення методології в межах однієї родини) рішення не потребували й лишаються. Колишній крок «(г) зберегти `<Код>_Id` при конфлікті» вилучено: його замінює заборона.

**Категорія:** correctness / reporting (контракт `D-53`: «стабільні імена **і типи**», `docs/tz/10-decisions.md:90`).
**Де:**
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:1000-1006` — `@ci` колонок групи; **родина типу** — `:1002`: `CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END` (String=0, Bool=3, Date=4, Lookup=5, Unit=7; Int/Decimal/Formula/Calculated — одна числова родина 1; значення enum — `src/Ecr.Domain/Enums/Enums.cs:50-65`);
- `:869-875` — які версії входять у стабільну вʼюху: `tv.Status IN (1, 2)` (Published, Deprecated) того самого шаблону, живі аркуш і таблиця; `:880-887` — група = (шаблон, **код** аркуша, **код** таблиці); `:1006` — живі колонки; `:1007` — колонки групи за кодом;
- `:1026-1029` — тип і місяць із найновішої версії, конфлікт = `COUNT(DISTINCT Fam) > 1`;
- `:1048-1071` — конфлікт → `nvarchar`, `SET @colType = 0;   -- no companion, no methodology overlay`;
- `:1081-1082`, `:1084-1086` — агрегат `s<n>` лише якщо `@colType IN (6, 8)`; `:1119` — накладення методології лише `IF @colType = 8`; `:1132` — `@hasText` лише для `@colType IN (6, 8)`; `:1147` — `@hasId` лише для `@colType = 5`;
- `:1329-1330` — текст попередження; `:935-947` — гілка пропуску (`@OnlyIfChanged`), яка попереджень не повертає;
- `src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs:166-169` — попередження лише в журнал; `:127` — безумовний `status?.Succeeded(templateVersionId)`;
- `src/Ecr.Api/Health/ReportViewsHealthCheck.cs:31-67` — картка `reportviews` знає лише збої генерації;
- публікація: `src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs:117` (знімок версії), `:209` (остання структурна перевірка перед відмовою), `:242-255` (усі зауваження однією відмовою `err.ECR-TMPL-0422.publishRejected`); зразок перевірки з ключем — `PublishChecks.CheckLookupRegistries` (`src/Ecr.Application/Templates/PublishChecks.cs:606-688`).

**Проблема:** набір колонок стабільної вʼюхи «ніколи не зменшується» (`:833-834`), але **супутники** й тип головної колонки залежать від останньої версії:
```sql
            -- Type / month placement come from the LATEST version that has the code.
            SELECT TOP (1) @colType = DataType, @isMonth = IsMonth, @monthNo = MonthNo
              FROM @ci WHERE Code = @colCode ORDER BY Ord DESC;
```
Наслідки:
- (а) `Formula` або `Calculated` у v1 → `Decimal` у v2 (одна родина, без конфлікту): колонка `<Код>_Text` **зникає** з наявної вʼюхи. Закривають кроки 1-2.
- (б) `Calculated` у v1 → не-`Calculated` у v2: накладення результатів методології (`rv`) не генерується, і рядки v1 отримують NULL. `doc.CellValue` значень `Calculated` не містить (`:574-577`; `TESTER-GUIDE.md:1491-1492`). Закривають кроки 1-2.
- (в) `Lookup` у v1 → `String` у v2: колонка стає `nvarchar`, а `<Код>_Id`, на який за `:1143-1146` з'єднується SSRS, **зникає**. Після рішення PO така публікація відхиляється (крок 3).
- (г) будь-який конфлікт родини міняє тип колонки на `nvarchar` (свідома поведінка генератора, тест `ReportViewStableTests.cs:139-163`), і датасет SSRS ламається (`Sum()` → `#Error`), а сигнал є лише в журналі. Після рішення PO нові конфлікти неможливі (крок 3), наявні видно в health (крок 5).

**Сценарій відтворення:**
- (а) v1 `S/T` колонка `F` типу `Formula`, у рядку v1 формула дала текст (`ValueString = 'n/a'`, `ValueNumeric = NULL`), звіт використовує `F_Text`. Публікується v2 з `F` типу `Decimal`. Після `rpt.usp_GenerateStableViews` колонки `F_Text` немає — датасет падає з «Invalid column name 'F_Text'». Очікувано: колонка лишається (`:833-834`) і для рядків v1 віддає `'n/a'`.
- (в/г) v1 опубліковано з `X` типу `String`; у чернетці v2 `X` змінено на `Decimal`; `POST /template-versions/{v2}/publish`. Фактично: 204, стабільна вʼюха робить `X` `nvarchar`. Очікувано (рішення PO 11.11): `422 ECR-TMPL-0422`, `messageKey = err.ECR-TMPL-0422.publishRejected`, у `diagnostics` — `err.ECR-TMPL-0422.columnTypeFamilyChanged` з `columnPath = S.T.X`.

**Вплив:** звіти SSRS на таблиці, де між версіями змінили тип колонки. (а)/(б) — тихе руйнування контракту без жодної дії адміністратора; (в)/(г) — після кроку 3 не виникають у нових версіях.

**Зміна — покроково:**
1. `05-rpt-views.sql:958` (оголошення):
   ```diff
       DECLARE @rvPred bit, @rvRef nvarchar(8), @conflictCodes nvarchar(1500), @hasText bit, @hasId bit, @extra int;
   +    DECLARE @everText bit, @everCalc bit;   -- R-3: тип колонки хоча б в одній версії
   ```
2. `05-rpt-views.sql:1038-1039`: одразу після обчислення `@routed` додати:
   ```diff
                SET @routed = CASE WHEN @routedGroup = 1 AND @conflict = 0 AND @colType NOT IN (6, 8)
                                        AND (@isMonth = 0 OR @monthNo BETWEEN 1 AND 12) THEN 1 ELSE 0 END;
   +            -- R-3: супутник <Код>_Text і накладення методології - якщо ХОЧА Б ОДНА версія мала Formula/Calculated
   +            -- (колонки стабільної вʼюхи не зникають, :833-834). Лише для неконфліктних і немаршрутизованих колонок.
   +            SET @everText = CASE WHEN EXISTS (SELECT 1 FROM @ci WHERE Code = @colCode AND DataType IN (6, 8)) THEN 1 ELSE 0 END;
   +            SET @everCalc = CASE WHEN EXISTS (SELECT 1 FROM @ci WHERE Code = @colCode AND DataType = 8) THEN 1 ELSE 0 END;
   +            IF @conflict = 0 AND @routed = 0 AND @everText = 1 AND @colType NOT IN (6, 8)
   +                SET @colType = CASE WHEN @everCalc = 1 THEN 8 ELSE 6 END;
   ```
   Колонки, що не міняли тип, отримують байт-у-байт той самий текст: умова спрацьовує лише коли в групі є `6/8` і інший тип тієї самої родини. `s<n>` (`:1081-1086`), `rv` (`:1119-1128`) і `_Text` (`:1132-1140`) далі генеруються наявним кодом. Для рядків нової версії результат той самий: `rv` NULL (прив'язки лише в колонки v1), `ValueString` порожній.
3. **Заборона публікації (рішення PO).** Порівнюються коди колонок чернетки з тими самими (код аркуша, код таблиці, код колонки) у **опублікованих і застарілих** версіях того самого шаблону — рівно ті версії й ті живі сутності, які читає стабільна вʼюха (`:869-875`, `:1006`). Чернетки не рахуються (вʼюх не дають). Коди порівнюються без урахування регістру: SQL групує за зіставленням бази (CI), а `UQ_ColumnDef` унікальний без регістру (`:177-178`).
   **Правило:** нехай `F` — множина родин коду в цих версіях. Якщо `F` не порожня і родина колонки чернетки ∉ `F` — зауваження. Так наявний конфлікт (у `F` уже дві родини) не блокує нові версії, що беруть одну з наявних родин, але нову родину додати не можна.
   - 3a. `src/Ecr.Application/Ports/ITemplateVersionStore.cs` — новий метод в інтерфейсі після `ReplaceHeaderGroupsAsync` (`:343-344`, перед `}` на `:345`):
     ```csharp
         /// <summary>
         /// ✎ R-3 (рішення PO 11.11, D-R3): типи живих колонок ОПУБЛІКОВАНИХ і ЗАСТАРІЛИХ версій шаблону — ті самі версії
         /// й сутності, які обʼєднує стабільна вʼюха <c>rpt.usp_GenerateStableViews</c> (живі аркуш, таблиця, колонка).
         /// </summary>
         /// <param name="templateId">Шаблон.</param>
         /// <param name="ct">Токен скасування.</param>
         /// <returns>Рядок на кожну живу колонку кожної такої версії.</returns>
         public Task<IReadOnlyList<PublishedColumnType>> ListPublishedColumnTypesAsync(int templateId, CancellationToken ct);
     ```
     і запис після `PresentationChange` (`:360`):
     ```csharp
     /// <summary>✎ R-3: тип колонки опублікованої/застарілої версії шаблону (вхід перевірки родини типу при публікації).</summary>
     public sealed record PublishedColumnType(
         string SheetCode, string TableCode, string ColumnCode, Domain.Enums.CellDataType DataType, string Version);
     ```
   - 3b. `src/Ecr.Infrastructure/Persistence/TemplateVersionStore.cs` — реалізація після `ListTableCodesAsync` (`:1319-1335`); `using Ecr.Domain.Enums;` уже є (`:6`):
     ```csharp
         /// <inheritdoc />
         public async Task<IReadOnlyList<PublishedColumnType>> ListPublishedColumnTypesAsync(int templateId, CancellationToken ct)
             => await (
                     from c in db.ColumnDefs.AsNoTracking()
                     join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                     join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                     join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                     where v.TemplateId == templateId
                           && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated)
                           && !c.IsDeleted && !t.IsDeleted && !s.IsDeleted
                     select new PublishedColumnType(s.Code, t.Code, c.Code, c.DataType, v.Version))
                 .ToListAsync(ct)
                 .ConfigureAwait(false);
     ```
     Властивості існують: `SheetDef.Code`/`TableDef.Code`/`ColumnDef.Code` — `string` (`SheetDef.cs:23`, `TableDef.cs:30`, `ColumnDef.cs:22`), `TemplateVersion.TemplateId`/`Version`/`Status` (`TemplateVersion.cs:34-36`), набори `db.ColumnDefs/TableDefs/SheetDefs/TemplateVersions` (`EcrDbContext.cs:31-34`). Інтерфейс реалізує лише `TemplateVersionStore` (`git grep -n ": ITemplateVersionStore"` — `TemplateVersionStore.cs:21`); тестові обгортки — `DispatchProxy` (`PublishDraftEditRaceApiTests.cs:192`) і `Substitute.For` (`ExcelExporterPublishedRangeTests.cs:147`), обидві нового методу не потребують.
   - 3c. `src/Ecr.Application/Templates/PublishChecks.cs` — дві нові функції поруч із `CheckLookupRegistries` (після `:688`):
     ```csharp
         /// <summary>
         /// ✎ R-3: родина типу зберігання — рівно як у <c>rpt.usp_GenerateStableViews</c>
         /// (<c>05-rpt-views.sql</c>: <c>CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END</c>).
         /// Змінюєш одне — зміни й друге (сторож <c>StableViewTypeFamilyTests</c>).
         /// </summary>
         public static int StorageFamily(CellDataType type) => type switch
         {
             CellDataType.String => 0,
             CellDataType.Bool => 3,
             CellDataType.Date => 4,
             CellDataType.Lookup => 5,
             CellDataType.Unit => 7,
             _ => 1,   // Int, Decimal, Formula, Calculated — число
         };

         /// <summary>
         /// ✎ R-3 (рішення PO 11.11, D-R3): код колонки, що вже є в опублікованих/застарілих версіях того самого шаблону
         /// (той самий аркуш і таблиця за кодом), не може перейти в ІНШУ родину типу зберігання — інакше стабільна вʼюха
         /// SSRS зробила б колонку <c>nvarchar</c> і прибрала <c>&lt;Код&gt;_Id</c> (<c>ECR-TMPL-0422</c>,
         /// <c>columnTypeFamilyChanged</c>). Наявний конфлікт (у минулих версіях уже кілька родин) нову версію не блокує,
         /// якщо вона бере одну з наявних родин.
         /// </summary>
         /// <param name="version">Чернетка зі структурою.</param>
         /// <param name="published">Типи колонок опублікованих/застарілих версій того самого шаблону.</param>
         /// <returns>Зауваження публікації; порожньо — усе гаразд.</returns>
         public static IReadOnlyList<ExpressionDiagnostic> CheckColumnTypeFamilies(
             TemplateVersion version, IReadOnlyList<PublishedColumnType> published)
         {
             ArgumentNullException.ThrowIfNull(version);
             ArgumentNullException.ThrowIfNull(published);

             var diagnostics = new List<ExpressionDiagnostic>();
             if (published.Count == 0)
             {
                 return diagnostics;
             }

             var byCode = published
                 .GroupBy(p => (Sheet: p.SheetCode.ToUpperInvariant(), Table: p.TableCode.ToUpperInvariant(), Column: p.ColumnCode.ToUpperInvariant()))
                 .ToDictionary(g => g.Key, g => g.ToList());

             foreach (var sheet in version.Sheets.Where(s => !s.IsDeleted))
             {
                 foreach (var table in sheet.Tables.Where(t => !t.IsDeleted))
                 {
                     foreach (var column in table.Columns.Where(c => !c.IsDeleted))
                     {
                         var key = (sheet.Code.ToUpperInvariant(), table.Code.ToUpperInvariant(), column.Code.ToUpperInvariant());
                         if (!byCode.TryGetValue(key, out var before)
                             || before.Any(p => StorageFamily(p.DataType) == StorageFamily(column.DataType)))
                         {
                             continue;
                         }

                         var path = $"{sheet.Code}.{table.Code}.{column.Code}";
                         var was = string.Join(", ", before
                             .GroupBy(p => p.DataType)
                             .OrderBy(g => g.Key)
                             .Select(g => $"{g.Key} ({string.Join(", ", g.Select(p => p.Version).Distinct(StringComparer.Ordinal).Take(3))})"));
                         diagnostics.Add(new ExpressionDiagnostic(
                             "ECR-TMPL-0422",
                             $"Колонка {path}: в опублікованих версіях шаблону — {was}, тут — {column.DataType}. Інша родина типу "
                             + "зламала б стабільну вʼюху SSRS; залиште тип або додайте колонку з новим кодом.",
                             0,
                             1,
                             "err.ECR-TMPL-0422.columnTypeFamilyChanged",
                             new Dictionary<string, string>
                             {
                                 ["columnPath"] = path,
                                 ["publishedTypes"] = was,
                                 ["newType"] = column.DataType.ToString(),
                             }));
                     }
                 }
             }

             return diagnostics;
         }
     ```
     `PublishChecks.cs` уже має `using Ecr.Application.Ports;`, `Ecr.Domain.Entities.Configuration`, `Ecr.Domain.Enums` (`:3-5`); `ExpressionDiagnostic(Code, Message, Position, Length, MessageKey, MessageParams)` — `src/Ecr.Expressions/Parsing/ParseResult.cs:51-57`; `SheetDef.Tables`, `TableDef.Columns`, `IsDeleted` вживаються в `LiveTables`/`LookupRegistryIds` (`:55-62`, `:579-590`).
   - 3d. `src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs`, одразу після `:209` (`diagnostics = [.. diagnostics, .. PublishChecks.CheckLookupRegistries(version, usable)];`):
     ```diff
             diagnostics = [.. diagnostics, .. PublishChecks.CheckLookupRegistries(version, usable)];
     +
     +        // ✎ R-3 (рішення PO 11.11, D-R3): код колонки не міняє родину типу між опублікованими версіями шаблону -
     +        // інакше стабільна вʼюха SSRS (rpt.usp_GenerateStableViews) зробила б її nvarchar. Один запит, у транзакції
     +        // публікації (під блоком версії, :99).
     +        var publishedColumns = await versionStore
     +            .ListPublishedColumnTypesAsync(version.TemplateId, ct)
     +            .ConfigureAwait(false);
     +        diagnostics = [.. diagnostics, .. PublishChecks.CheckColumnTypeFamilies(version, publishedColumns)];
     ```
     Зауваження потрапляє в загальну відмову `:242-255` (усі проблеми одразу); клієнт показує `MessageKey` з `MessageParams` (`src/Ecr.Application/Templates/ExpressionRejection.cs:126`: `new DiagnosticInfo(d.Code, d.Message, d.Position, d.Length, d.MessageKey, d.MessageParams)`).
4. Новий сід `src/Ecr.Infrastructure/Persistence/Sql/seed/features/RPTSTABLE-type-family.sql` (форма — як `COLL-rc16lookup.sql`: порція `#I18N` ru/kz, потім `#UiEn` en зі `Scope = 1`, окремі батчі `GO`, рядки-маркери секції; ім'я за шаблоном `<AREA>-<slug>.sql`, область `RPTSTABLE` уже є — `RPTSTABLE-rpt-stable.sql`):
   ```sql
   -- RPTSTABLE:type-family ── ru/kz: R-3, заборона змінювати родину типу коду колонки між версіями; попередження health; власна порція ──
   INSERT INTO #I18N ([Key], Lang, Val)
   SELECT v.[Key], v.Lang, v.Val
     FROM (VALUES
       (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'ru', N'Столбец «{columnPath}»: в опубликованных версиях шаблона — {publishedTypes}, здесь — {newType}. Другой тип хранения сломал бы стабильное представление отчётов SSRS: оставьте тип или добавьте столбец с новым кодом.'),
       (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'kz', N'«{columnPath}» бағаны: үлгінің жарияланған нұсқаларында — {publishedTypes}, мұнда — {newType}. Сақтаудың басқа түрі SSRS тұрақты есеп көрінісін бұзар еді: түрін сақтаңыз немесе жаңа кодпен баған қосыңыз.'),
       (N'health.reportviews.typeConflict', N'ru', N'Представления отчётов rpt.v_* созданы, но в стабильных представлениях есть столбцы, тип которых различается между версиями шаблона; они отдаются текстом (nvarchar), и суммы в отчётах SSRS по ним не работают: {details}. Новые такие изменения публикация не пропускает; для существующих — используйте в отчёте столбец нужной версии или новый код.'),
       (N'health.reportviews.typeConflict', N'kz', N'rpt.v_* есеп көріністері жасалды, бірақ тұрақты көріністерде түрі үлгі нұсқалары арасында әртүрлі бағандар бар; олар мәтін (nvarchar) ретінде беріледі, және SSRS есептеріндегі қосындылар олар бойынша жұмыс істемейді: {details}. Жариялау мұндай жаңа өзгерістерді өткізбейді; барларына есепте қажетті нұсқаның бағанын немесе жаңа кодты пайдаланыңыз.')
          ) AS v ([Key], Lang, Val)
   OPTION (RECOMPILE);
   GO
   -- RPTSTABLE:type-family ── кінець секції ──
   -- RPTSTABLE:type-family ── R-3: заборона змінювати родину типу коду колонки між версіями; попередження health; ru/kz — порцією вище ──
   INSERT INTO #UiEn ([Key], Lang, Val, Scope)
   SELECT v.[Key], v.Lang, v.Val, v.Scope
     FROM (VALUES
       (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'en', N'Column "{columnPath}": {publishedTypes} in the published versions of this template, {newType} here. A different storage type would break the stable SSRS report view: keep the type or add a column with a new code.', 1),
       (N'health.reportviews.typeConflict', N'en', N'Report views rpt.v_* are generated, but some stable views have columns whose type differs between template versions; they are exposed as text (nvarchar) and SSRS sums over them fail: {details}. Publication no longer accepts such changes; for the existing ones use the column of the right version or a new code in the report.', 1)
          ) AS v ([Key], Lang, Val, Scope)
   OPTION (RECOMPILE);
   GO
   -- RPTSTABLE:type-family ── кінець секції ──
   ```
   Підстановки ru/kz = en (`SeedTranslationTests.Підстановки_перекладу_збігаються_з_оригіналом`); ключі нові (`git grep -n "columnTypeFamilyChanged\|reportviews.typeConflict" -- src` — порожньо).
5. **Наявні конфлікти в базах замовника — видимі в health (рішення PO: попередження лишається).**
   - 5a. `src/Ecr.Application/Ports/IReportViewStatus.cs:8` — константа коду:
     ```diff
     -public sealed record ReportViewFailure(int? TemplateVersionId, int Code, string Message);
     +public sealed record ReportViewFailure(int? TemplateVersionId, int Code, string Message)
     +{
     +    /// <summary>✎ R-3: не збій, а попередження стабільних вʼюх «конфлікт типу між версіями → nvarchar» (номер не вживає жодна процедура).</summary>
     +    public const int StableTypeConflictCode = 50410;
     +}
     ```
     (`git grep -n "50410" -- src tests` — порожньо.)
   - 5b. `src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs`: `GenerateStableAsync` (`:134-181`) повертає ще й попередження; `GenerateAsync` після успіху ставить їх у стан замість `Succeeded`:
     ```diff
     -    private async Task<bool> GenerateStableAsync(int? templateVersionId, bool routed, bool onlyIfChanged, CancellationToken ct)
     +    private async Task<(bool Skipped, List<string> Warnings)> GenerateStableAsync(
     +        int? templateVersionId, bool routed, bool onlyIfChanged, CancellationToken ct)
          {
              var skipped = false;
     +        var warnings = new List<string>();
     …
                      if (!reader.IsDBNull(2) && logger is not null)
                      {
                          LogStableWarning(logger, reader.GetString(0), reader.GetString(2));
                      }
     +
     +                // ✎ R-3: конфлікт типу між версіями (наявні до D-R3) — у /health/ready, не лише в журнал.
     +                if (!reader.IsDBNull(2))
     +                {
     +                    warnings.Add($"rpt.{reader.GetString(0)}: {reader.GetString(2)}");
     +                }
     …
     -        return skipped;
     +        return (skipped, warnings);
          }
     ```
     у `GenerateAsync` (`:57-127`): перед `try` (`:57`) оголосити `List<string> stableWarnings = [];`; рядок `:83` → `var (skipped, warnings) = await GenerateStableAsync(templateVersionId, routed, onlyIfChanged, ct).ConfigureAwait(false); stableWarnings = warnings;`; рядок `:127`:
     ```diff
     -        status?.Succeeded(templateVersionId);
     +        if (stableWarnings.Count == 0)
     +        {
     +            status?.Succeeded(templateVersionId);
     +        }
     +        else
     +        {
     +            // ✎ R-3: вʼюхи створено, але стабільні мають колонки nvarchar через конфлікт типу між версіями.
     +            status?.Failed(new ReportViewFailure(
     +                templateVersionId, ReportViewFailure.StableTypeConflictCode, string.Join(" | ", stableWarnings)));
     +        }
     ```
   - 5c. `05-rpt-views.sql`, гілка пропуску `usp_GenerateStableViews` (`:944-947`): без цього після рестарту з незмінними входами попередження зникали б із health (процедура повертає один рядок `Skipped`). Замінити `SELECT` пропуску:
     ```diff
          BEGIN
     -        SELECT CAST(N'' AS sysname) AS ViewName, CAST(N'Skipped' AS nvarchar(16)) AS Action, CAST(NULL AS nvarchar(2000)) AS Warning;
     +        -- R-3: конфлікти типу між версіями (наявні до D-R3) повертаються й при пропуску - інакше після рестарту health їх не бачить.
     +        SELECT CAST(N'' AS sysname) AS ViewName, CAST(N'Skipped' AS nvarchar(16)) AS Action, CAST(NULL AS nvarchar(2000)) AS Warning
     +        UNION ALL
     +        SELECT CAST(g.ViewName AS sysname), CAST(N'Skipped' AS nvarchar(16)),
     +               CAST(LEFT(CONCAT(N'type conflict across versions, exposed as nvarchar: ',
     +                    STUFF((SELECT N', ' + x.Code
     +                             FROM (SELECT cd.Code
     +                                     FROM cfg.ColumnDef AS cd JOIN @gt AS t ON t.TableDefId = cd.TableDefId
     +                                    WHERE t.GroupId = g.GroupId AND cd.IsDeleted = 0
     +                                    GROUP BY cd.Code
     +                                   HAVING COUNT(DISTINCT CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END) > 1) AS x
     +                            ORDER BY x.Code FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'')), 2000) AS nvarchar(2000))
     +          FROM @groups AS g
     +         WHERE EXISTS (SELECT 1 FROM cfg.ColumnDef AS cd JOIN @gt AS t ON t.TableDefId = cd.TableDefId
     +                        WHERE t.GroupId = g.GroupId AND cd.IsDeleted = 0
     +                        GROUP BY cd.Code
     +                       HAVING COUNT(DISTINCT CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END) > 1);
              RETURN;
          END
     ```
     `@gt.GroupId` заповнено на `:885-887`; `@groups` на цей момент уже без зіткнень і без чужих шаблонів (`:921-933`). Генератор розпізнає пропуск за `Action = 'Skipped'` (`ReportViewGenerator.cs:161-164`), тож додаткові рядки з тим самим `Action` пропуск не ламають, а `Warning` з них іде в журнал і (5b) у стан. ⚠ Цей текст процедури змінює відбиток (`:131-133`): на першому старті стабільні вʼюхи перегенеруються один раз.
   - 5d. `src/Ecr.Api/Health/ReportViewsHealthCheck.cs:56-67`: коли ВСІ записи стану — попередження конфлікту типу, текст інший (вʼюхи створено):
     ```diff
     -        var failed = await Text(
     -            "health.reportviews.failed",
     +        var onlyTypeConflicts = failures.All(f => f.Code == ReportViewFailure.StableTypeConflictCode);
     +        var failed = onlyTypeConflicts
     +            ? await Text(
     +                "health.reportviews.typeConflict",
     +                "Report views rpt.v_* are generated, but some stable views have columns whose type differs between template "
     +                + "versions; they are exposed as text (nvarchar) and SSRS sums over them fail: {details}. Publication no longer "
     +                + "accepts such changes; for the existing ones use the column of the right version or a new code in the report.",
     +                new Dictionary<string, string>(StringComparer.Ordinal) { ["details"] = details },
     +                cancellationToken).ConfigureAwait(false)
     +            : await Text(
     +            "health.reportviews.failed",
     ```
     (решта виклику `failed` без змін, `return HealthCheckResult.Degraded(failed, data: data);`). Статус лишається `Degraded`, не `Unhealthy` (`:13-16`).
6. Контракт `docs/build/02-contracts.md` §7.1 — після рядка `err.ECR-TMPL-0422.ordinalInvalid` (`:4388`):
   ```
   | `err.ECR-TMPL-0422.columnTypeFamilyChanged` | 422 | ✎ R-3 (D-R3): публікація версії, у якій код колонки (той самий аркуш і таблиця за кодом) має іншу родину типу зберігання, ніж в опублікованих/застарілих версіях шаблону (родини: String; Bool; Date; Lookup; Unit; число = Int/Decimal/Formula/Calculated); `columnPath`, `publishedTypes`, `newType`; іде в `diagnostics` загальної відмови `publishRejected` |
   ```
   і в опис картки health (`docs/build/02-contracts.md:3338`, `reportviews`) — ключ `health.reportviews.typeConflict` (Degraded). OpenAPI не змінюється (нових полів немає).
7. `docs/tz/10-decisions.md` — новий рядок після `D-474` (`:911`), номер — наступний вільний (нижче `D-R3`), + рядок журналу змін (за зразком `:1139`), + `docs/CHECKSUMS.txt:11` (`git add docs/tz/10-decisions.md && git show :docs/tz/10-decisions.md | sha256sum`):
   ```
   | **D-R3** | **✎ 2026-11-11. Код колонки не змінює родину типу зберігання між опублікованими версіями шаблону (стабільні вʼюхи SSRS, `D-53`).** Публікація версії відхиляється (`422 ECR-TMPL-0422`, `columnTypeFamilyChanged`), якщо код колонки на тому самому аркуші й у тій самій таблиці (за кодами) в опублікованих чи застарілих версіях шаблону має іншу родину типу (String; Bool; Date; Lookup; Unit; число — Int/Decimal/Formula/Calculated): автор заводить новий код. Наявні конфлікти лишаються: стабільна вʼюха віддає таку колонку `nvarchar`, `/health/ready` показує `reportviews` = Degraded з переліком (`health.reportviews.typeConflict`). Зміна в межах родини (напр. Formula → Decimal) дозволена, супутник `<Код>_Text` і накладення методології зберігаються. | рішення PO 11.11 (аудит 2026-11, `R-3`, варіант 3, через координатора) | `R-3` · `D-53` · `RPT-STABLE` |
   ```
8. `docs/build/TESTER-GUIDE.md` (розділ SSRS, після `:1492`): «Зміна типу колонки між версіями шаблону: у межах родини — дозволено (стабільна вʼюха зберігає колонку й супутники); в іншу родину — публікація відхиляється `columnTypeFamilyChanged` (D-R3)». Детальний розділ для авторів звітів — R-7.

**Тест-доказ:**
- `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.cs`, новий кейс `Колонка_Formula_що_стала_Decimal_зберігає_супутник_Text_з_даними_v1` (кроки 1-2). v1 `S/T`: `F` типу `CellDataType.Formula`; v2 `S/T`: `F` типу `CellDataType.Decimal`. `SeedAsync(p, 202609, v1, ("R1", 1, new() { ["F"] = "n/a" }))` (`InsertCellsAsync` кладе рядок у `ValueString`, `:651-664`), `SeedAsync(p, 202610, v2, ("R1", 1, new() { ["F"] = 3m }))`, `ExecStableAsync(v2.VersionId)`. Перевірки: `UserColumnsAsync(stable)` містить `"F_Text"`; `RowsAsync(stable, p.ProjectId, "_PeriodKey", "F", "F_Text")` = `[(202609, null, "n/a"), (202610, 3m, null)]`. Без кроку 2 колонки `F_Text` немає.
- `tests/Ecr.Application.Tests/Templates/PublishColumnTypeFamilyTests.cs` (новий, за зразком `PublishRuleWiringTests.cs:32-120`: реальний `TemplateVersionStore`, `PublishCodesAsync`), кейси:
  - `Код_колонки_що_змінив_родину_типу_відхиляє_публікацію`: шаблон, v1 `S/T/X` `String` — опублікувати (`version.Publish(1, Now)` + `SaveChangesAsync`, як `ReportViewGeneratorTests.cs:239-245`); v2 (чернетка) `S/T/X` `Decimal` → відмова, серед `diagnostics` є `MessageKey == "err.ECR-TMPL-0422.columnTypeFamilyChanged"` з `MessageParams["columnPath"] == "S….T….X"`. Без кроку 3d — публікація проходить.
  - `Зміна_в_межах_родини_публікується`: v1 `X` `Decimal`, v2 `X` `Int` → такого зауваження немає.
  - `Наявний_конфлікт_не_блокує_версію_з_наявною_родиною`: v1 `X` `String` і v2 `X` `Decimal` опубліковані напряму (обхід перевірки, як дані до D-R3); v3 `X` `Decimal` → без зауваження; v3' `X` `Date` → зауваження.
  - `Чернетки_й_видалені_колонки_не_рахуються`: інша чернетка того самого шаблону з `X` `String` і видалена колонка `X` в опублікованій версії не дають зауваження.
- `tests/Ecr.Application.Tests/Templates/StableViewTypeFamilyTests.cs`: `PublishChecks.StorageFamily` для всіх значень `CellDataType` = `{String:0, Int:1, Decimal:1, Bool:3, Date:4, Lookup:5, Formula:1, Unit:7, Calculated:1}`; і текст `05-rpt-views.sql` (шлях — як `ReportDefinitionTests.cs:63-64`) містить `CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END` (сторож синхронності двох реалізацій родини). Якщо читати файли рішення з `Ecr.Application.Tests` не прийнято — другу частину покласти в `tests/Ecr.Architecture.Tests`.
- `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.cs`, кейс `Конфлікт_типу_колонки_між_версіями_дає_nvarchar_і_попередження` (`:139-163`) лишається зеленим (генератор не змінює поведінки для наявних конфліктів); доповнити: `new ReportViewGenerator(db, status).GenerateAsync(v2.VersionId, …)` з `ReportViewStatus` → `status.Snapshot()` має один запис із `Code == ReportViewFailure.StableTypeConflictCode` і текстом із `X`; другий виклик із `onlyIfChanged: true` (пропуск) — запис лишається (крок 5c). Без 5b — `Snapshot()` порожній; без 5c — порожній після пропуску.
- `tests/Ecr.Api.Tests/Health/ReportViewsHealthTests.cs` (наявний файл тестів картки): стан з одним `StableTypeConflictCode` → `Degraded` з текстом ключа `health.reportviews.typeConflict`; стан із `50422` → старий текст.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ReportViewStableTests|FullyQualifiedName~ReportViewSlotMapTests|FullyQualifiedName~ReportViewStartupSkipTests|FullyQualifiedName~ReportViewGenerator"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Publish"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~Health|FullyQualifiedName~Publish"`; `dotnet test tests/Ecr.Architecture.Tests` (сід, каталог, `CHECKSUMS`). Стенд: на копії бази замовника `GET /health/ready` → якщо `reportviews` став Degraded з `typeConflict` — це наявні конфлікти, перелік передати авторам звітів; спроба опублікувати чернетку зі зміною родини типу → 422 з `columnTypeFamilyChanged`.

**Ризики і побічні ефекти:**
- Текст процедури змінюється (кроки 1-2, 5c) — на першому старті стабільні вʼюхи перегенеруються (`ALTER VIEW` під Sch-M, секунди на вʼюху), як у R-1.
- Публікація: +1 запит (типи колонок шаблону, кілька тисяч рядків на Land) у транзакції публікації; гарячий шлях не зачеплено. Тести фіксованої кількості запитів публікації (якщо є: `git grep -n "QueryCount\|CommandCount" -- tests/*Publish*`) — оновити стелю лише на +1 з поясненням.
- Картка `reportviews` стане Degraded на базах, де конфлікти вже є (саме це й треба було показати). Публікація від цього не блокується.
- Для маршрутизованих колонок (`@routed = 1`) пункт (а) лишається невиправленим (рідкісне поєднання MonthsInColumns + прапор Q-17 + Formula→input); задокументувати в R-7.
- Групи, які R-2 пізніше звʼяже мапою перейменувань (таблицю перейменовано переносом), перевірка публікації не бачить: мапа зʼявляється вже після публікації (`DocumentVersionMigrationStore.cs:1908-1912`). Такий конфлікт генератор і далі зробить `nvarchar` + попередження (крок 5) — прийнятно.

**Обґрунтування:** «колонки не зникають» — вже правило процедури (`:833-834`), тож супутники мають підкорятися йому (кроки 1-2). Для зміни родини PO обрав заборону (варіант 3) замість `nvarchar` (1) чи `nvarchar` + `<Код>_Id` (2): вʼюха не ламається взагалі. Перевірку поставлено в публікацію (одні ворота, всі зауваження одразу, `:242-255`), а не в редагування колонки чернетки: чернетка може тимчасово мати проміжний тип, а публікація — єдиний момент, коли структура замерзає (ФВ-7.1).

**Рішення PO:** ухвалено 11.11 — варіант 3 (заборона публікації; наявні конфлікти — попередження в health). Відкритих питань немає.
**Впевненість:** підтверджено читанням.
**Залежності:** перша в лінії `05-rpt-views.sql`: R-3 → R-2 → R-4 → R-1. Ділить з R-1 також `ReportViewGenerator.cs`/health (R-1 крок 6 — підказка 208, інший метод).

---

<a id="r-4"></a>
### R-4 — Medium — Немає шару довідників і підписів для SSRS
**Категорія:** i18n / reporting.
**Де:**
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:1631-1633`: `rpt_reader` має лише `SELECT ON SCHEMA::rpt`;
- `:1143-1155`: Lookup-колонка віддає лише Id (`<Код>`, `<Код>_Id`);
- `:354-361`: у `_vN` тип Lookup → `ValueRegistryEntryId`, Unit → `ValueUnitId` (лише Id);
- `:71-72, 726-733, 1301-1310`: `_SheetDisplayName` / `SheetDisplayName` — лише `$.en`;
- `:1412-1500`: єдині вʼюхи довідників — дозвільні (`v_Permit*`), і вони дають код, без назв.

**Проблема:** рішення людини 2026-09-30 (`05-rpt-views.sql:36-38`) — SSRS сам формує звіти із сирих даних. Проте `rpt_reader` не читає `dic.*`, `cfg.*` і `uom.*` (`:1624-1628`; перевірено тестом `ReportViewGeneratorTests.cs:140-172`, відмова 229). Тому звіт не може показати назву речовини, джерела чи одиниці, лише число-Id. Підписи колонок шаблону (`cfg.ColumnDef.HeaderL10n`) і назви записів (`dic.RegistryEntry.DisplayL10n`) ru/kz для SSRS недоступні. Назва аркуша є лише англійською.

**Сценарій відтворення:** звіт «викиди за речовинами» на стабільній вʼюсі з Lookup-колонкою `SUBSTANCE`. `SELECT SUBSTANCE, SUBSTANCE_Id …` дає `1234`, `1234`. Отримати назву речовини російською: `SELECT * FROM dic.RegistryEntry` під `rpt_reader` → 229. Очікувано: є вʼюха `rpt.*` для розшифрування Id (межа B16 §1: «резолвінг довідника» робить сервіс або дає контракт).

**Вплив:** усі звіти з довідниковими колонками. Обхідний шлях — видати `rpt_reader` читання `dic`/`cfg`, а це ширше за найменші права.

**Зміна — покроково:**
1. `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql`: перед блоком прав (`:1624`) додати три статичні вʼюхи:
   ```sql
   -- R-4: розшифрування Id для SSRS (rpt_reader бачить лише rpt.*): записи довідників, одиниці, підписи колонок опублікованих версій.
   -- Мови - ключі LocalizedText (en/ru/kz, як у сіді); видалені логічно записи НЕ відсіюються (на них можуть посилатися старі комірки),
   -- а віддаються колонкою IsDeleted.
   CREATE OR ALTER VIEW rpt.v_RegistryEntry
   AS
   SELECT e.Id AS EntryId, d.Code AS RegistryCode, e.Code AS EntryCode,
          JSON_VALUE(e.DisplayL10n, N'$.en') AS Name_en,
          JSON_VALUE(e.DisplayL10n, N'$.ru') AS Name_ru,
          JSON_VALUE(e.DisplayL10n, N'$.kz') AS Name_kz,
          e.ParentEntryId, e.ValidFrom, e.ValidTo, e.IsActive, e.IsDeleted
     FROM dic.RegistryEntry AS e
     JOIN cfg.RegistryDef   AS d ON d.Id = e.RegistryDefId;
   GO
   CREATE OR ALTER VIEW rpt.v_Unit
   AS
   SELECT u.Id AS UnitId, u.Code AS UnitCode,
          JSON_VALUE(u.SymbolL10n, N'$.en') AS Symbol_en, JSON_VALUE(u.SymbolL10n, N'$.ru') AS Symbol_ru,
          JSON_VALUE(u.SymbolL10n, N'$.kz') AS Symbol_kz,
          JSON_VALUE(u.NameL10n, N'$.en') AS Name_en, JSON_VALUE(u.NameL10n, N'$.ru') AS Name_ru,
          JSON_VALUE(u.NameL10n, N'$.kz') AS Name_kz
     FROM uom.Unit AS u;
   GO
   CREATE OR ALTER VIEW rpt.v_TemplateColumn
   AS
   SELECT t.Code AS TemplateCode, tv.[Version] AS TemplateVersion, sd.Code AS SheetCode, td.Code AS TableCode,
          cd.Code AS ColumnCode, cd.DataType, cd.Ordinal, cd.LookupRegistryDefId, cd.UnitId,
          JSON_VALUE(sd.NameL10n, N'$.en') AS SheetName_en, JSON_VALUE(sd.NameL10n, N'$.ru') AS SheetName_ru,
          JSON_VALUE(sd.NameL10n, N'$.kz') AS SheetName_kz,
          JSON_VALUE(cd.HeaderL10n, N'$.en') AS Header_en, JSON_VALUE(cd.HeaderL10n, N'$.ru') AS Header_ru,
          JSON_VALUE(cd.HeaderL10n, N'$.kz') AS Header_kz
     FROM cfg.ColumnDef       AS cd
     JOIN cfg.TableDef        AS td ON td.Id = cd.TableDefId AND td.IsDeleted = 0
     JOIN cfg.SheetDef        AS sd ON sd.Id = td.SheetDefId AND sd.IsDeleted = 0
     JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId AND tv.Status IN (1, 2)
     JOIN cfg.Template        AS t  ON t.Id = tv.TemplateId
    WHERE cd.IsDeleted = 0;
   GO
   ```
   Колонки підтверджено снапшотом моделі: `RegistryEntry` (`Code`, `DisplayL10n`, `IsActive`, `IsDeleted`, `ParentEntryId`, `RegistryDefId`, `ValidFrom`, `ValidTo`), `RegistryDef` (`Code`), `uom.Unit` (`Code`, `NameL10n`, `SymbolL10n`), `ColumnDef` (`HeaderL10n`, `LookupRegistryDefId`, `UnitId`, `Ordinal`, `IsDeleted`). Права: ланцюг власності `dbo` діє, схемний `GRANT` (`:1633`) покриває нові вʼюхи автоматично.
2. `docs/admin/admin-guide.md`: у розділі для авторів звітів (R-7) показати приклад поєднання `… JOIN rpt.v_RegistryEntry AS re ON re.EntryId = v.SUBSTANCE_Id`.

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorTests.cs`, розширити `Роль_rpt_reader_читає_вʼюхи_але_не_таблиці_документів` (`:140-172`) або додати сусідній кейс `Роль_rpt_reader_розшифровує_довідник_через_rpt_v_RegistryEntry`. Від власника вставити запис довідника з `DisplayL10n = N'{"en":"Sulphur","ru":"Сера","kz":"Күкірт"}'` у будь-який `cfg.RegistryDef` сіду. Під `EXECUTE AS USER = N'rpt_probe'`:
- `SELECT Name_ru FROM rpt.v_RegistryEntry WHERE EntryId = @id` → `"Сера"`;
- `SELECT COUNT(*) FROM dic.RegistryEntry` → `SqlException` 229.

Без кроку 1 перший запит падає з 208 («Invalid object name»).

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ReportViewGeneratorTests|FullyQualifiedName~DeployScriptsRerunTests"` (скрипт ідемпотентний: `CREATE OR ALTER`).
**Ризики і побічні ефекти:** контракт `rpt.*` розширюється адитивно. Записи довідників стають видимі акаунту SSRS (бізнес-довідники, без секретів: облікові дані джерел — у `sec.*`). Індексів не треба, довідники малі.
**Обґрунтування:** статичні вʼюхи з константною проєкцією узгоджуються з ER-R-02 (вʼюха лише проєктує). Варіант «видати `rpt_reader` право на `dic`/`cfg`» відкинуто як ширший за потребу.
**Рішення PO:** не потрібне (адитивне розширення контракту в межах рішення 30.09).
**Впевненість:** підтверджено читанням.
**Залежності:** немає (інша ділянка файлу; рекомендовано в тій самій лінії, що R-3/R-2/R-1, щоб не конфліктувати на `05-rpt-views.sql`).

---

<a id="r-5"></a>
### R-5 — Low — Обліковий запис служби може писати й видаляти в `arc.*` напряму
**Категорія:** security (найменші права / цілісність архіву).
**Де:**
- `tests/Ecr.TestKit/ServiceAccountGrants.cs:19`: `Roles = ["db_datareader", "db_datawriter"]`;
- `docs/admin/operations-runbook.md:2396-2412`: згенерований скрипт прав;
- `src/Ecr.Infrastructure/Jobs/ArchiveJob.cs:17-20` і `src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql:13` стверджують протилежне («обліковий запис застосунку не має… права запису в `arc.*`», `D-66`).

**Проблема:** `db_datawriter` дає `INSERT/UPDATE/DELETE` на **всі** таблиці бази, зокрема `arc.CellValue`, `arc.TableRow`, `arc.TableInstance`. На колонсторі `arc.*` тригерів незмінності немає (`ArchiveJobTests.cs:384-385`: «на таблиці з clustered columnstore SQL Server тригерів не дозволяє»). Застосунку запис в `arc.*` не потрібен: усі записи йдуть через процедури `WITH EXECUTE AS OWNER` (`03-archive-proc.sql:35-36, 53-58, 677-682`). `git grep` C#-коду не знаходить жодного `INSERT/UPDATE/DELETE arc.`.

**Сценарій відтворення:** від імені служби `DELETE FROM arc.CellValue WHERE PeriodKey = 202501` → виконується. Архівний рік, оригіналу якого в `doc.*` вже немає, знищено без сліду. Очікувано за `D-66` і `D-71` («`arc.*` не затирається»): 229.
**Вплив:** захист у глибину. Без вразливості в застосунку не експлуатується, але компрометація облікового запису служби дає незворотне знищення архіву.

**Зміна — покроково:**
1. `tests/Ecr.TestKit/ServiceAccountGrants.cs`: після `Functions` (`:36`) додати
   ```csharp
       /// <summary>
       /// R-5: схеми, запис у які службі ЗАБОРОНЕНО поверх <c>db_datawriter</c>: архів пишуть лише процедури
       /// <c>WITH EXECUTE AS OWNER</c> (D-66, D-71); DENY на викликача на них не діє.
       /// </summary>
       public static readonly IReadOnlyList<string> DeniedWriteSchemas = ["arc"];
   ```
   У `Script` (`:42-66`) після циклу процедур, перед `return sql.ToString();`:
   ```csharp
           foreach (var schema in DeniedWriteSchemas)
           {
               sql.Append("DENY INSERT, UPDATE, DELETE ON SCHEMA::").Append(schema).Append(" TO ").Append(who).AppendLine(";");
           }
   ```
2. Згенерувати блоки документації: `powershell -File tools\gen-install-docs.ps1`. Блок оновиться в `docs/admin/operations-runbook.md`, `docs/build/11-install-guide.md` і `docs/delivery/QUICKSTART-FIRST-TEST.md`, і сторож `ServiceAccountGrantsRunbookTests` лишиться зеленим.
3. У прозі runbook (`:2392`) дописати «+ `DENY INSERT, UPDATE, DELETE ON SCHEMA::arc` (архів пишуть лише процедури)».

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Persistence/ServiceAccountLeastPrivilegeTests.cs`, новий кейс `Служба_не_може_змінювати_архів_напряму`: `await using var c = new SqlConnection(database.LowPrivConnectionString)`, `DELETE FROM arc.CellValue WHERE 1 = 0` → `SqlException.Number == 229`. Без кроку 1 команда проходить (0 рядків), тож тест червоний. Наявні кейси (`Нічна_перевірка_партицій…`, `:187-200`, з `usp_ArchiveAudit` → SWITCH від власника) лишаються зеленими.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ServiceAccountLeastPrivilegeTests|FullyQualifiedName~ArchiveLeastPrivilegeTests"`; `dotnet test tests/Ecr.Architecture.Tests --filter "FullyQualifiedName~ServiceAccountGrantsRunbookTests"`.
**Ризики і побічні ефекти:** `AuditReader` лише читає `arc.AuditCellChange` (`AuditReader.cs:277, 307`), DENY на запис його не зачіпає. На вже розгорнутих базах DBA виконує один рядок.
**Обґрунтування:** найдешевше узгодження коду з `D-66`/`D-71`. Тригери на колонсторі неможливі.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

<a id="r-6"></a>
### R-6 — Low — Звірка архівації порівнює лише COUNT і SUM чисел
**Категорія:** data-integrity.
**Де:**
- `src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql:541-552, 558-564`: перевірка в момент знищення (під `TABLOCKX`) — кількість і сума комірок, кількість рядків;
- `:594-597`: порівняння;
- `docs/tz/10-decisions.md:50` (`D-23`: «звірка **трьох** контрольних сум»), `docs/tz/04-data.md:122-123` (`COUNT_BIG(*)`, `CHECKSUM_AGG(BINARY_CHECKSUM(*))`, `SUM`);
- `src/Ecr.Infrastructure/Persistence/OrphanScanner.cs:748-770`: нічна задача оновлює `doc.TableRow.IsOrphaned` без фільтра за станом проєкту.

**Проблема:** запобіжник 3 (`:517-539`) мав би довести, що «все, що зараз у джерелі, вже лежить в архіві». Але він рахує лише кількість і числову суму:
```sql
        IF @gapSrcCells <> @gapDstCells OR @gapSrcSum <> @gapDstSum
           OR @gapSrcRows <> @gapDstRows OR @gapSrcInst <> @gapDstInst
```
Зміна `ValueString` / `ValueDate` / `ValueRegistryEntryId` / `ValueBool` або прапора рядка (`IsOrphaned`, `IsDeleted`, `Ordinal`) між кроком 1 і кроком 2 не змінює ні кількості, ні суми. `TRUNCATE` її знищить, а прогін отримає `Completed`. Правки через UI заблоковано (`Archived` + `IsArchiving`, `EditRules.cs:200-202`). Фонові записувачі, що не питають `EditRules`, лишаються, зокрема `OrphanScanner`.

**Сценарій відтворення:** архівація року 2025, крок 1 скопіював період 202503. Нічний `OrphanScanJob` позначив рядок 202503 `IsOrphaned = 1`. Крок 2: кількості й суми збіглися → `TRUNCATE`. В `arc.TableRow` лишилося `IsOrphaned = 0`, а зміну загублено. Очікувано за `D-23`: розбіжність (50014), джерело ціле.
**Вплив:** низький, бо вікно — тривалість кроку 1, а записувачів мало. Утім саме цей запобіжник існує для такого вікна (`:517-525`).

**Зміна — покроково:** файл `src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql`.
1. `:541-544`:
   ```diff
            DECLARE @gapSrcInst bigint, @gapDstInst bigint;
   +        -- R-6 / D-23: третя сума - вміст (рядки, дати, Id, прапори), а не лише кількість і числова сума.
   +        DECLARE @gapSrcChk int, @gapDstChk int, @gapSrcRowChk int, @gapDstRowChk int;
   ```
2. `:546-548`:
   ```diff
   -        SELECT @gapSrcCells = COUNT_BIG(*), @gapSrcSum = ISNULL(SUM(ValueNumeric), 0)
   +        SELECT @gapSrcCells = COUNT_BIG(*), @gapSrcSum = ISNULL(SUM(ValueNumeric), 0),
   +               @gapSrcChk = CHECKSUM_AGG(BINARY_CHECKSUM(TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, ValueDate,
   +                                                         ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty))
            FROM doc.CellValue WITH (TABLOCKX)
            WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;
   ```
3. `:550-552`:
   ```diff
   -        SELECT @gapSrcRows = COUNT_BIG(*)
   +        SELECT @gapSrcRows = COUNT_BIG(*),
   +               @gapSrcRowChk = CHECKSUM_AGG(BINARY_CHECKSUM(Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned))
            FROM doc.TableRow WITH (TABLOCKX)
   ```
4. `:558-560` і `:562-564` — те саме для `arc.CellValue` (`@gapDstChk`) і `arc.TableRow` (`@gapDstRowChk`), з тими самими переліками колонок. Типи колонок `doc`/`arc` ідентичні (`12-archive-tables.sql:54-85, 263-265`; снапшот моделі), тож `BINARY_CHECKSUM` збігається.
5. `:594-597`:
   ```diff
            IF @gapSrcCells <> @gapDstCells OR @gapSrcSum <> @gapDstSum
               OR @gapSrcRows <> @gapDstRows OR @gapSrcInst <> @gapDstInst
   +           OR ISNULL(@gapSrcChk, 0) <> ISNULL(@gapDstChk, 0) OR ISNULL(@gapSrcRowChk, 0) <> ISNULL(@gapDstRowChk, 0)
               OR @gapSrcParts <> @gapDstParts
   ```
6. `docs/admin/operations-runbook.md:838` (таблиця помилок, рядок 50014): «прогалина або зміна вмісту (контрольна сума) в діапазоні».

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Jobs/ArchiveJobTests.cs`, кейс `Зміна_прапора_рядка_між_звіркою_і_TRUNCATE_не_дає_звітувати_Completed`. Він копіює прийом `Запис_у_проміжку_між_звіркою_і_TRUNCATE_не_дає_звітувати_Completed` (`:371-422`): тригер `itg.TR_ArchiveGapFlag` на `itg.ArchiveRun AFTER UPDATE` при `LastDonePeriodKey = <період>` виконує `UPDATE doc.TableRow SET IsOrphaned = 1 WHERE PeriodKey = <період> AND Id = {doc.RowIds[0]} AND IsOrphaned = 0`. Очікування: `ArchiveAsync` кидає `SqlException`, `LastRunAsync` → `Failed`, `doc.TableRow` періоду на місці. Період узяти унікальний серед тестів архіву (✎ виправлено 11.11: `202705` уже вживає `RestoreYearTests.cs:38`, `202706` бере R-1): перед вибором — `git grep -n "const int period" -- tests/Ecr.Infrastructure.Tests/Jobs`; межі партицій є лише до `202712` (`02-partitions.sql:26-30`), пізніший період — так, як `ArchiveBatchSizeTests.cs:212`. Без кроків 1-5 прогін `Completed`, тож тест червоний.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~ArchiveJobTests|FullyQualifiedName~ArchiveOverlapTests|FullyQualifiedName~RestoreYearTests|FullyQualifiedName~ArchiveLeastPrivilegeTests"`. Повторне накочення `03-archive-proc.sql` (×2, `CREATE OR ALTER`).
**Ризики і побічні ефекти:** додаткових сканувань немає, агрегат іде в тих самих `SELECT`. Додається CPU під Sch-M на кроці 2 (там уже повний скан діапазону). `CHECKSUM_AGG` не криптографічний: колізії можливі, але це третій бар'єр поверх двох наявних, як і задумано в `D-23`.
**Обґрунтування:** закриває відхід від `D-23` найменшою зміною. Порівняння `EXCEPT` по всіх рядках під Sch-M відкинуто: це другий повний прохід із сортуванням.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (запис `OrphanScanner` в архівний рік — за кодом; частоту на стенді не перевіряв).
**Залежності:** немає (D-12 звіту 02 правка `usp_RestoreYear` — інша процедура того самого файлу; робити послідовно, щоб не конфліктувати).

---

<a id="r-7"></a>
### R-7 — Low — Немає контракту SSRS для автора звітів, частина текстів застаріла
**Категорія:** docs.
**Де:**
- `docs/admin/admin-guide.md`: розділу про SSRS немає (зміст `:10-1048`). Згадки є лише в `§12` (Q-17, `:976-1040`): вказівки «`EXEC rpt.usp_GenerateTemplateViews`» без `rpt.usp_GenerateStableViews`;
- `src/Ecr.Api/Health/ReportViewsHealthCheck.cs:60` і сід `COLL-rpt-views-health.sql:7`: «…or run EXEC rpt.usp_GenerateTemplateViews» (стабільні вʼюхи не названо);
- `docs/build/TESTER-GUIDE.md:1491-1492`: «розрахункові колонки `NULL`… результати методологій в `calc.*` не підтягуються». Застаріло з Q-17.recalc (`05-rpt-views.sql:574-598`);
- `docs/tz/10-decisions.md:859` (`D-396`: «`doc.CellStyle`… при архівації не копіюється в `arc.*`»). Застаріло з коміту `65112112e` (`03-archive-proc.sql:450-454, 622-623`);
- `docs/admin/operations-runbook.md:820-848` (§7): нічого не сказано про наслідки для SSRS і про Sch-M на всю `doc.CellValue` під час `WITH CHECK` (`03-archive-proc.sql:507-509`).

**Проблема:** продукт обіцяє «збудуй звіт раз» (`D-53`, стабільні вʼюхи RPT-STABLE), але автор звітів ніде не знайде:
- що будувати треба на `rpt.v_<Т>_<А>_<Т>`, а не на `_vN`;
- що перейменування колонки потребує мапи переносу;
- що копії аркушів фільтруються `_SheetCopyOrdinal = 1`;
- що статус — колонка `_Status` (`D-380`);
- що робиться з архівними роками;
- що робиться при зміні типу.

DBA за підказкою health перегенерує лише `_vN`.

**Сценарій відтворення:** автор SSRS бере вʼюху `rpt.v_T_S_T_v1` (єдина згадана в `TESTER-GUIDE.md:1483` та `05-rpt-views.sql:41-43`). Після переносу проєкту на v2 звіт порожній (`DocumentVersionMigrationStore.cs:1767-1772`). Очікувано: документація вела б його на стабільну вʼюху.
**Вплив:** помилки авторів звітів, хибні дії DBA.

**Зміна — покроково:**
1. `docs/admin/admin-guide.md`: новий розділ `## 13. Звітність SSRS: контракт rpt.*`, пункти:
   - (а) обліковий запис і права: `rpt_reader`, `ALTER ROLE rpt_reader ADD MEMBER …`, лише `SELECT` на `rpt`;
   - (б) які обʼєкти стабільні: `rpt.v_<Шаблон>_<Аркуш>_<Таблиця>` (рекомендовано), `rpt.v_DocumentCells`, `rpt.v_DocumentCellsByPermit`, `rpt.v_Permit*`, після R-4 також `rpt.v_RegistryEntry`/`v_Unit`/`v_TemplateColumn`; `_vN` — лише для звітів «на конкретну версію»;
   - (в) службові колонки: `_ProjectId`, `_PeriodKey` (фільтрувати завжди: відсікання партицій), `_Status` (2 = Approved, `D-380`), `_TemplateVersion`, `_SheetCopyOrdinal`, `_SheetDisplayName`, `<Код>_Text`, `<Код>_Id`, після R-1 також `_IsArchived` (`IsArchived` у `rpt.v_DocumentCells*`);
   - (г) що переживає назва: нові версії, нові колонки, нові періоди, копії; перейменування колонки — лише з `tableRenames` при переносі; перейменування таблиці — після R-2; зміна типу — у межах родини колонка й супутники лишаються, зміну родини публікація відхиляє (`D-R3`), наявні конфлікти — `nvarchar` і попередження в health; архів — видно з `_IsArchived = 1` (`D-R1`), у `_vN` — ні;
   - (д) як перегенерувати вручну: `EXEC rpt.usp_GenerateTemplateViews @MonthRouting = <прапор>; EXEC rpt.usp_GenerateStableViews @MonthRouting = <прапор>;`.
2. `docs/admin/admin-guide.md` §12 (`:985, :990, :1002, :1024, :1032, :1040`): до кожного `EXEC rpt.usp_GenerateTemplateViews …` дописати `; EXEC rpt.usp_GenerateStableViews …` з тим самим `@MonthRouting`.
3. `docs/build/TESTER-GUIDE.md:1491-1492`: замінити на «колонки `Calculated` — число методології (`calc.CalculationResult` за активною прив'язкою), якщо результату немає, — значення комірки».
4. `docs/tz/10-decisions.md:859` (`D-396`): дописати позначку «✎ 2026-11-10: архівація переносить `doc.CellStyle` і `doc.CellFormulaOverride` у `arc.*` зі звіркою (коміт `65112112e`); архівний рік їх не показує, доки його не повернуто». Оновити `docs/CHECKSUMS.txt`.
5. `docs/admin/operations-runbook.md` §7 (після `:829`): абзац «Наслідки для SSRS: рік лишається видимим у стабільних вʼюхах і `rpt.v_DocumentCells*` з `_IsArchived`/`IsArchived = 1` (`D-R1`), у вʼюхах `_vN` — зникає; зрізи `rpt.ReportSnapshot` лишаються. Крок 2 тримає Sch-M на `doc.CellValue`/`doc.TableRow` до кінця `WITH CHECK` (скан усієї таблиці): усі читання й записи, зокрема SSRS, чекають — лише вікно обслуговування (`D-117`)». Окремо — про переривання: «обрив `usp_ArchiveYear` до `CATCH` лишає `Running` і `IsArchiving = 1`; повторіть виклик» (за аналогією з §7.1, `:878-882`).

**Тест-доказ:** документація. `ServiceAccountGrantsRunbookTests` і `docs/CHECKSUMS.txt`-сторож (якщо `10-decisions.md` під контрольною сумою) мусять лишитися зеленими.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Architecture.Tests` (сторожі документації); перевірка посилань вручну.
**Ризики і побічні ефекти:** немає.
**Обґрунтування:** контракт без опису для того, хто ним користується, не працює як контракт.
**Рішення PO:** не потрібне. ✎ Рішення PO 11.11 по R-1 (варіант А) і R-3 (варіант 3) уже внесено в пункти (в), (г) і 5.
**Впевненість:** підтверджено читанням.
**Залежності:** після R-1 і R-3 (документує їхню поведінку); підрозділ «Архівні роки» може вже створити R-1 (крок 7) — тоді R-7 доповнює розділ.

---

## Порядок впровадження

- **Лінія A (`05-rpt-views.sql`, `ReportViewGenerator.cs`, `ReportViewStableTests.cs`), строго послідовно:** R-3 → R-2 (разом із `MigrateDocumentVersionHandler.cs`, `RecalculationService.cs`) → R-4 → R-1. Усі правлять процедуру `usp_GenerateStableViews` або сусідні блоки того самого файлу. ✎ Після рішень PO 11.11 обидві (R-1 — варіант А, R-3 — варіант 3) готові до впровадження. R-3 ще правлює `PublishChecks.cs`, `PublishTemplateVersionHandler.cs`, `ITemplateVersionStore.cs`/`TemplateVersionStore.cs`, `IReportViewStatus.cs`, `ReportViewsHealthCheck.cs`, сід `RPTSTABLE-type-family.sql`; R-1 — `12-archive-tables.sql` (лише за заміром), тести `RestoreYearTests.cs`, `DocumentCellsByPermitViewTests.cs`, `StatusNumberingTests.cs`, новий `ReportArchiveBranchTests.cs`. `ReportViewGenerator.cs` правлять обидві (R-3 — `GenerateAsync`/`GenerateStableAsync`, R-1 — `SafeMessage`): послідовно.
- **Спільні з іншими звітами:** `docs/tz/10-decisions.md` + `docs/CHECKSUMS.txt` (рядки `D-R1`, `D-R3` тут, `D-X2` у звіті 08, `D-V7` у звіті 09) — злиття послідовне, суму перераховувати при кожному; номери `D-nnn` присвоїти на момент коміту (на `39aea41d9` останній — `D-474`, `D-475` уже згадано у звітах 01/04). `docs/build/02-contracts.md` — R-3 (§7.1) і звіти 08/09 правлять різні рядки. Міграцій EF немає ні в R-1, ні в R-3; OpenAPI не змінюється.
- **Лінія B (`03-archive-proc.sql`, `ArchiveJobTests.cs`):** R-6. Послідовно з `D-12` звіту 02 (та сама процедура-файл, інша процедура).
- **Лінія C (`ServiceAccountGrants.cs` + згенеровані блоки docs):** R-5. Незалежна.
- **Лінія D (лише docs):** R-7, після R-1/R-3 (рішення PO є з 11.11). Пункти 2-5 можна раніше.
- Лінії B і C паралельні з A. Спільних файлів між A, B і C немає.

## Відкинуто

- **`ArchiveJob` недосяжний (ні API, ні розкладу).** Свідомо: `RecurringScheduleService.cs:352-354` («архівація року — свідомий крок людини»), `D-264` (DBA-роботи на замовнику). Відомо з `docs/build/questions/Q-340.md` §6.4.
- **Процедура не перевіряє річний грейс (його перевіряє лише недосяжний `ArchiveJob.cs:93-104`).** Не дефект. Запобіжник 1 (`03-archive-proc.sql:102-126`) пускає лише проєкти `Status = 4`, а `ArchiveProjectHandler` ставить `Archived` лише коли всі періоди `Closed` і річний грейс минув (`ProjectQueryHandlers.cs:971`).
- **`rpt_reader` бачить усі проєкти й статуси (немає фільтра рядків).** Рішення: `D-53` («доступ до звітів лишається в SSRS»), `B16` R-5, `D-380`, `TESTER-GUIDE.md:1481-1488`.
- **Сирий шар віддає `Draft`.** `D-380`.
- **Звітних вʼюх над зрізами лише одна (`rpt.v_WaterReport_v1`), генератора `rpt.v_<Звіт>` немає.** Відоме обмеження: `TESTER-GUIDE.md:1599-1601`.
- **`arc.TableRow` не має `OrphanedAt`.** Колонку ніхто не читає (`git grep OrphanedAt` поза записувачами `OrphanScanner.cs:752, 769` і доменом — порожньо). Втрата значення при архівації без наслідків.
- **Архівний рік не показує стилі й позначки ручних значень.** Свідомо, коміт `65112112e` («доки його не повернуто»). Тексти документації — у R-7.
- **`calc.CalculationResult/Input/Step` і `ext.RowWindowValue` (партиційовані по `PeriodKey`) не архівуються, `arc.CalculationResult` ніхто не наповнює.** Не дефект цілісності: дані лишаються гарячими, `TRUNCATE` їх не чіпає (`03-archive-proc.sql:614-625`), посилання за `TableInstanceId` стабільні, бо Id з послідовності повертаються ті самі (`CalculationResult.cs:107-112`). Це питання обсягу `FG_HOT`, не коректності.
- **Тривалість кроку 2 (Sch-M + `WITH CHECK` по всій `doc.CellValue`).** Відомо: `D-117` (поріг 30 хв → двофазний варіант), `TODO-REMAINING.md` B3.3 («архівація не виміряна»), `Q-063`. Документацію додано в R-7.
- **Генератор не рахує `<Код>_Text` у стелі 1000 колонок.** Уже в звіті 02 (`02-data-layer.md:1177`).
- **Позначки `CellFormulaOverride` «лишаються на місці».** Твердження брифу застаріле, див. шапку.

## Перевірено й у порядку

- **Права SSRS переживають перегенерацію:** `GRANT SELECT ON SCHEMA::rpt TO rpt_reader` (`05-rpt-views.sql:1631-1633`). Вʼюхи змінюються `CREATE OR ALTER` (`:736, :1313`), `DROP VIEW` у `src` немає (`git grep -i "DROP VIEW"` — порожньо). Тест: `ReportViewGeneratorTests.cs:140-172` (читає довгу й згенеровану вʼюху, `doc.CellValue` → 229).
- **Найменші права й секрети:** `rpt_reader` — лише `rpt`. `ecr_viewer` має `DENY` на `sec.DataProtectionKey` і `sec.[User](PasswordHash, SecurityStamp)` (`:1650-1655`). Жодна вʼюха `rpt.*` не читає `sec.*`.
- **Без зайвих блокувань:** RCSI увімкнено (`06-rcsi.sql:28-30`), `NOLOCK` у вʼюхах немає. Генератор не виконує `ALTER`, якщо текст не змінився (`:751-759, 1331-1336`). Мапа слотів і вʼюха переписуються однією транзакцією (`:768-779`). Старт пропускає генерацію за відбитком (`:283-296`, `ReportViewGenerator.cs:83-87`).
- **Збій генерації не ламає публікацію й видимий:** `ReportViewGenerator.cs:99-127` → `/health/ready` `reportviews` = `Degraded` (`ReportViewsHealthCheck.cs:25-63`). Відсутні процедури дають підказку (`ReportViewGenerator.cs:199-225`).
- **Стабільні вʼюхи:** колонки об'єднуються за кодом і не зникають (`:833-834, 1000-1007`). `_TemplateVersion` береться з версії рядка (`:991-992`). Перейменування колонки за `cfg.ColumnRename` дає обидва імені (`:1009-1019`; тест `ReportViewStableTests.cs:515-553`). Зіткнення імен → 50409 без перезапису (`:919-933`). Копії аркушів: `_SheetCopyOrdinal`/`_SheetDisplayName` у кінці списку (`:1305-1310`).
- **Архівація — головне правило «спершу копія, потім звірка, потім знищення»:** крок 1 поза транзакцією, звірка кожного періоду (`03-archive-proc.sql:462-481`). Крок 2 — одна транзакція `DROP FK → звірка під TABLOCKX → TRUNCATE → ADD FK WITH CHECK` (`:511-635`, `D-117`).
- **Ідемпотентність і перекриття:** «уже заархівовано» (`:221-234`), U1-01 — відмова 50016 замість стирання архіву (`:260-303`), очищення лише незавершеної копії (`:321-340`).
- **Спільні партиції сусідніх проєктів:** запобіжник 1 (`:102-126`), журнал на кожен зачеплений проєкт (`:175-197`), межі партицій (`:136-148`).
- **Набір таблиць архіву повний для партиційованих `doc.*`:** `CellValue`, `TableRow`, `TableInstance` (із `SheetCopyId`), `CellPermitPart`, `CellStyle`, `CellFormulaOverride` (`:378-460, 614-625`). Колонки `arc.*` збігаються з `doc.*`, крім невживаного `OrphanedAt` (снапшот моделі vs `12-archive-tables.sql:41-140, 260-272`). Зовнішніх ключів на `doc.TableRow`/`doc.TableInstance`, крім двох, які знімає процедура, немає (міграції: лише `FK_TableRow_Instance`, `FK_CellValue_Row`).
- **Відновлення:** Id екземплярів і рядків беруться з послідовностей (`EcrDbContext.cs:361-362`), тож явна вставка Id не конфліктує, а посилання `calc.CalculationResult.TableInstanceId` лишаються дійсними. Звірка за ключами архіву (`:943-1008`). Відкат часткової копії Z6-01 (`:1024-1159`). Повтор безпечний (`NOT EXISTS`, прибирання `arc.*`).
- **Права на архівацію:** процедури `WITH EXECUTE AS OWNER`, викликачу досить `EXECUTE` (`ArchiveLeastPrivilegeTests`). Позначку `Archived` ставить `Project.Manage` + грант `Manage` (`ProjectQueryHandlers.cs:904-935`).
- **UI/API для архівного року:** лише читання (`EditRules.cs:200-202` і ще три місця). Читання прозоре з `arc.*` (`ArchiveAwareCellReader.cs:33-52`, тести `ArchiveJobTests.cs:217-333`). `RowStore` не фабрикує нові порожні екземпляри в архівному році (`RowStore.cs:957-970`).
- **Перенос версії заархівованого проєкту заборонено:** `MigrateDocumentVersionHandler.cs:800, 1442-1445` (`projectArchived`).
- **Видалення:** документа заархівованого проєкту заборонено (`DeleteDocumentHandler.cs:168`). Проєкт видаляється лише як чернетка без документів (`ФВ-1.15`, `D-469`).
- **Зрізи для SSRS переживають архівацію:** `rpt.ReportSnapshot/ReportRow` не партиційовані й не чіпаються. Джерело агрегації — `calc.CalculationResult` (`ReportSnapshotBuilder.cs:1222-1235`), який лишається гарячим.

## Самоперевірка

- **R-1 (✎ переписано після рішення PO 11.11):**
  - `file:line` відкрито повторно: `05-rpt-views.sql:51-52, 53-92, 55-75, 76-78, 79-90, 372, 401-406, 409-410, 687-693, 853, 903-907, 950, 1161, 1170-1174, 1186-1192, 1201, 1211-1212, 1278, 1290-1297, 1305-1310, 1312-1327, 1519, 1520-1621, 1536, 1538-1540, 1580-1583, 1601, 1615, 1624-1633`; `03-archive-proc.sql:305-308, 321-340, 342-490, 468-481, 507-509, 511-635, 611, 614-625, 747-922, 760-769, 808-815, 943-1008, 1046-1142`; `12-archive-tables.sql:37, 40-105, 263-272, 351-352`; `ReportViewGenerator.cs:208-215`; `deploy-ecr.ps1:2095-2099`; `SqlServerFixture.cs:539, 557`; міграції `InitialCreate.cs:20-21`, `Stage5Integration.cs:20-21`; `StatusNumberingTests.cs:22-23, 70-72`; `ReportViewStableTests.cs:62-65, 102-104, 309, 373, 472, 475, 782-786`; `RestoreYearTests.cs:38, 355-404`; `ReportViewGeneratorTests.cs:149-158, 239-245`; `TESTER-GUIDE.md:1489-1490`.
  - «Було» дослівне: `:1312-1327` (текст стабільної вʼюхи), `:950` (оголошення), `:51-52`, `:1519`, `:1536`, `:1621`, `ReportViewGenerator.cs:211-212`.
  - Компільованість: нові змінні `@selHead`/`@tail`/`@tailArc` оголошено (крок 4); `CONCAT` з `nvarchar(max)` — як наявний код. Типи колонок `arc.*` = `doc.*` (снапшот моделі vs `12-archive-tables.sql`), тож `UNION ALL` не змінює типів колонок вʼюх. `MissingObjectError`/`MissingObjectHint` — константи поруч із наявними `MissingProcedure*`.
  - Тест падає без фіксу: у `RestoreYearTests` після архівації 0 рядків і немає колонки `_IsArchived`; без `NOT EXISTS` — 2 рядки в проміжному стані; `ReportArchiveBranchTests` — на будь-якій розбіжності копії; без кроку 6 — немає підказки 208.
  - Обсяг `_vN` звірено з текстом варіанта А, який затвердив PO («Вʼюхи `_vN` не чіпаються»), і з лічильником таблиць (`:687-693`).
  - Дублі: відоме обмеження без `D-nnn` (`10-decisions.md`, `KNOWN-ISSUES`/`OPEN-ITEMS`/`TODO-REMAINING`/`WORK-QUEUE` — «архів…SSRS» порожньо); рішення тепер `D-R1`.
  - Виправлено під час перевірки: чернетка казала, що на порожньому `arc.*` архівна гілка коштує «один seek `doc.TableInstance` на екземпляр» — ні: `NOT EXISTS` виконується лише для архівних екземплярів; golden-тест `_vN` не змінюється (раніше не згадувався, тепер явно); `StatusNumberingTests` 4 → 6 додано; `ByPermit` тепер в обсязі (рішення PO), з повними замінами.
- **R-2:**
  - Відкрито: `05-rpt-views.sql:880-887, 1010-1015`, `MigrateDocumentVersionHandler.cs:1351-1357`, `DocumentVersionMigrationStore.cs:1767-1772, 1908-1912`, `VersionMigrationPlanner.cs:290-321`, `RecalculationService.cs:1862-1869`.
  - «Було» `RenamePairs` дослівне. Рядки `:987, 990, 992, 995, 997, 1006, 1304` містять саме `GroupId = @g` / `x.GroupId = @g` (перевірено читанням `:987-1006, 1301-1304`).
  - Типи існують: `SheetDef.Tables` (`SheetDef.cs:48`), `TableDef.Code` (`TableDef.cs:30`, string), `ColumnDef.TableDefId`, `InternalsVisibleTo Ecr.Application.Tests` (`Ecr.Application.csproj:22`).
  - Тест падає без фіксу: `RenamePairs` для однакових кодів колонок повертає порожньо, і стабільна вʼюха старої назви не бачить v2.
  - Дублі: звіти 02/04 і `VersionMigrationTableRenamesTests` про це не пишуть.
- **R-3 (✎ переписано після рішення PO 11.11):**
  - Відкрито: `05-rpt-views.sql:177-178, 833-834, 869-875, 880-887, 885-887, 921-933, 935-947, 958, 1000-1007, 1026-1029, 1038-1039, 1048-1071, 1081-1086, 1119, 1132, 1147, 1329-1330`; `ReportViewGenerator.cs:57-127, 134-181, 161-169`; `ReportViewsHealthCheck.cs:13-16, 31-67`; `IReportViewStatus.cs:8`; `ReportViewStatus.cs:13-36`; `PublishTemplateVersionHandler.cs:99, 117, 209, 242-255`; `PublishChecks.cs:3-5, 55-62, 579-590, 606-688`; `ExpressionRejection.cs:126`; `ParseResult.cs:51-57`; `ITemplateVersionStore.cs:343-345, 360`; `TemplateVersionStore.cs:6, 21, 1319-1335`; `Enums.cs:50-65`; `TemplateVersion.cs:34-36`; `EcrDbContext.cs:31-34`; `10-decisions.md:90`; `02-contracts.md:3338, 4388`.
  - «Було» дослівне: `:958`, `:1038-1039`, `:944-947` (рядок `SELECT` пропуску), `ReportViewGenerator.cs:127, 134, 166-169`, `ReportViewsHealthCheck.cs:56-57`, `IReportViewStatus.cs:8`, `PublishTemplateVersionHandler.cs:209`.
  - Компільованість: `PublishedColumnType` у `Ecr.Application.Ports` (у `PublishChecks.cs` уже `using Ecr.Application.Ports;`); EF-проєкція в конструктор запису — у фінальному `select`; `version.TemplateId` існує; `ReportViewFailure` стає записом із тілом (`const`) — позиційний конструктор не змінюється; інтерфейс `ITemplateVersionStore` реалізує лише `TemplateVersionStore` (тестові `DispatchProxy`/`Substitute` нового методу не потребують). Номер `50410` не вживається (`git grep`).
  - Тест падає без фіксу: без кроку 2 немає `F_Text`; без кроку 3d публікація зі зміною родини проходить; без 5b/5c `ReportViewStatus.Snapshot()` порожній.
  - Дублі: конфлікт → `nvarchar` — свідома поведінка генератора з тестом (`ReportViewStableTests.cs:139-163`), лишається для наявних даних; заборона публікації — нове рішення PO (`D-R3`). Перевірки родини типу при публікації в `PublishChecks` немає (`git grep -n "DataType" -- src/Ecr.Application/Templates/PublishChecks.cs` — лише Lookup/Formula-перевірки).
  - Вилучено: колишній крок 3 «зберегти `<Код>_Id` при конфлікті» (варіант 2, PO не обрав); у кроці 4 чернетки `status?.Failed` без урахування `Succeeded` на `:127` і без гілки пропуску — переписано (5b, 5c).
- **R-4:**
  - Відкрито: `05-rpt-views.sql:1631-1633, 1143-1155, 354-361, 71-72, 726-733, 1301-1310, 1412-1500`, `ReportViewGeneratorTests.cs:140-172`.
  - Колонки нових вʼюх звірено зі снапшотом моделі (`RegistryEntry`, `RegistryDef`, `uom.Unit`, `ColumnDef.HeaderL10n`). Мовні ключі `en/ru/kz` збігаються з сідом (`00-prelude.sql:4317, 7324`).
  - Тест падає без фіксу: 208.
  - Дублі: grep `v_Registry` / «довідник…SSRS» по docs — порожньо.
- **R-5:**
  - Відкрито: `ServiceAccountGrants.cs:19, 36, 42-66`, runbook `:2392-2412`, `ArchiveJob.cs:17-20`, `03-archive-proc.sql:13`.
  - «Було»: diff — лише додавання після наявних рядків.
  - `ServiceAccountGrants.Script` будує рядки `StringBuilder.Append`, тож новий цикл того самого вигляду.
  - Тест падає без фіксу: `DELETE` проходить.
  - Дублі: grep `db_datawriter.*arc` / `DENY…arc` — порожньо, звіт 01 цього не має.
- **R-6:**
  - Відкрито: `03-archive-proc.sql:541-552, 558-564, 594-597`, `D-23` (`10-decisions.md:50`), `04-data.md:122-123`, `OrphanScanner.cs:748-770`, `ArchiveJobTests.cs:371-422`.
  - «Було» дослівне (`:546-548`, `:550-552`, `:594-596`).
  - `CHECKSUM_AGG`/`BINARY_CHECKSUM` підтримуються на колонсторі; типи колонок `doc`/`arc` однакові.
  - Тест падає без фіксу: прогін `Completed`.
  - Дублі: grep `BINARY_CHECKSUM`/«трьох контрольних» по `docs/build` (поза цим аудитом) знаходить лише `02a-db-schema.md:3647-3672` (проєкт процедури, де суму рахували, але не порівнювали).
- **R-7:**
  - Відкрито: `ReportViewsHealthCheck.cs:60`, `COLL-rpt-views-health.sql:7`, `TESTER-GUIDE.md:1491-1492`, `10-decisions.md:859`, runbook `:820-848`, `admin-guide.md` зміст.
  - Зміни лише в документації.
  - Дублі не знайдено.
  - ✎ 11.11: пункти (в), (г), 5 і «Залежності» оновлено під рішення PO по R-1/R-3. R-6: виправлено хибне «202705 вільний» (його вживає `RestoreYearTests.cs:38`).
- **Виправлено/вилучено під час перевірки:**
  - кандидат «`usp_RestoreYear` не звіряє частини» вилучено як дубль `D-12`;
  - «`CellFormulaOverride` не архівується» вилучено, бо спростовано кодом (`65112112e`);
  - «`arc.TableRow` без `OrphanedAt`» перенесено у «Відкинуто» (колонку ніхто не читає);
  - «продуктивність `fam` на багаторічній історії при прапорі Q-17» не включено: без плану на стенді це припущення;
  - «немає грейсу в процедурі» перенесено у «Відкинуто» (покрито станом `Archived`).
