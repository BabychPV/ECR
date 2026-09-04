# questions.md — журнал питань, блокерів і виправлень

> Заповнюється **на ПК-2**. Порожній на старті.
>
> Сюди потрапляє все, що ти **не маєш права вирішити мовчки**: незрозуміла
> вимога, суперечність у документах, потреба змінити контракт, третя невдала
> спроба на ту саму помилку, будь-яке відхилення від `05-skeleton.md`.
>
> Записав → **зупинився**. Виняток — `BOOTSTRAP-FIX`: він записується і робота
> продовжується, бо це дрібні виправлення збірки, передбачені Етапом 0.

---

## Типи записів

| Тип | Коли | Дія після запису |
|---|---|---|
| `BOOTSTRAP-FIX` | Етап 0: мінімальне виправлення, щоб зібралося (using, версія пакета, шлях) | продовжити |
| `DECIDED` | неточність, яку ти **вирішуєш сам** (`08-workflow.md` §7): проза розходиться з контрактом, дрібниця скелета, локальна деталь | продовжити |
| `BLOCKER` | три невдалі спроби на ту саму помилку | **зупинка** |
| `CONTRACT` | реалізація не лягає на контракт із `02-contracts.md` | **зупинка** |
| `AMBIGUITY` | вимога допускає два різні тлумачення | **зупинка** |
| `CONFLICT` | два документи суперечать один одному | **зупинка** |
| `SCOPE` | помічена проблема поза поточним етапом | продовжити |
| `ENV` | розбіжність середовища з `04-environment.md` | продовжити або зупинка — за наслідком |
| `DATA` | розбіжність із реальними даними при звірці | **зупинка** |

## Шаблон запису

```markdown
### Q-NNN · <ТИП> · Етап N · <дата>

**Де:** <файл / модуль / тест>
**Контекст:** <що робив, коли натрапив>

**Суть:**
<одним абзацом — у чому саме проблема>

**Текст помилки / цитати документів:**
```
<повний текст, не переказ>
```

**Що вже пробував:**
1. <спроба 1 — результат>
2. <спроба 2 — результат>
3. <спроба 3 — результат>

**Гіпотези:**
- <гіпотеза 1>
- <гіпотеза 2>

**Що потрібно від людини:** <конкретне питання або рішення>
**Статус:** OPEN | RESOLVED · <як вирішено, ким, коли>
```

## Правила заповнення

1. **Цитуй, не переказуй.** Повний текст помилки і повна цитата документа —
   без них запис не має цінності для того, хто його читатиме.
2. **Один запис — одна проблема.** Не змішуй дві.
3. **Нумерація наскрізна**: `Q-001`, `Q-002`, … незалежно від етапу.
4. **Не видаляй записи.** Вирішене помічається `RESOLVED` із поясненням.
5. `BOOTSTRAP-FIX` пиши **стисло**, але обов'язково: що саме змінив і чому.

---

## Зведення

| ID | Тип | Тема | Статус |
|---|---|---|---|
| Q-001 | DECIDED | пакет документації перенесено в `docs/` | RESOLVED |
| Q-002 | BOOTSTRAP-FIX | `source/` (438 МБ реальних даних) у `.gitignore` | RESOLVED |
| Q-003 | BOOTSTRAP-FIX | чотири `.csproj` оголошені в `.sln`, але відсутні в пакеті | RESOLVED |
| Q-004 | BOOTSTRAP-FIX | версії пакетів: downgrade + вразливості | **OPEN** — мажор `NCalcSync 5→6` |
| Q-005 | BOOTSTRAP-FIX | `Entity<TId> : struct` проти `Permission : Entity<string>` | RESOLVED |
| Q-006 | BOOTSTRAP-FIX | аналізатори стилю ламають власний код пакета | **OPEN** — рішення про стиль |
| Q-007 | BOOTSTRAP-FIX | `IRepository.cs` — пропущений `///` | RESOLVED |
| Q-008 | BOOTSTRAP-FIX | `Ecr.Application → Ecr.Expressions` | RESOLVED |
| Q-009 | DECIDED | namespace `ParseResult.cs` | RESOLVED |
| Q-010 | DECIDED | секції на два файли, директиви `COPY FROM` | RESOLVED |
| Q-011 | BOOTSTRAP-FIX | відсутні `using` у 25 файлах | RESOLVED |
| Q-012 | DECIDED | `TemplateStructureDto` бере `ColumnDto`/`RowDto` з `Documents.Dto` | **OPEN** — семантика DTO |
| **Q-013** | **CONFLICT** | **циклічна залежність `FormulaEngine`** | **OPEN · ЗУПИНКА** |
| **Q-014** | **CONTRACT** | **десять контрактних типів не оголошені ніде** | **OPEN · ЗУПИНКА** |
| Q-015 | SCOPE | 41 файл у дереві без вмісту в `05*` | **OPEN** |
| Q-016 | ENV | Docker не запущений | **OPEN** |
| Q-017 | SCOPE | frontend: `typecheck` потребує згенерованих модулів | **OPEN** |

---

## Записи

### Q-001 · DECIDED · Етап 0 · 2026-09-04

**Де:** корінь репозиторію
**Контекст:** крок 1 Етапу 0 — створення структури папок.

**Суть:**
Пакет документації лежав у корені (`build/`, `tz/`, `reference/`,
`architecture/`, `README.md`, `CHECKSUMS.txt`), а дерево `05-skeleton.md` §1
і всі внутрішні посилання очікують його в `docs/`.

**Цитати документів:**
```
05-skeleton.md §1:   ├── docs/                 ← цей пакет (уже існує)
05a, .gitignore:     !docs/**/*.md
00-START-HERE.md:    Ти в папці `docs/build/`.
```

**Що зробив:** перенесено в `docs/`. `README.md` пакета став `docs/README.md`,
бо `05a` оголошує **свій** `README.md` у корені репозиторію.
`sha256sum -c CHECKSUMS.txt` після перенесення — усі файли OK, вміст жодного
документа не змінено.
**Статус:** RESOLVED

---

### Q-002 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `.gitignore`
**Контекст:** `git add -A` перед першим комітом.

**Суть:**
`source/` містить 438 МБ реальних даних чинної системи (`ECR Current Back end`:
`.sqlproj`, SQL, SSRS, PI AF, CLR-збірка; `ECR Current Front end`).
`.gitignore` зі скелета ігнорує `data/`, `*.xlsm`, `*.bak`, `*.dacpac`, але не
`source/`.

**Що зробив:** додав `source/` у `.gitignore` під власним коментарем. Спирався
на правило самого файла:
```
# ⚠ Реальні дані НІКОЛИ не потрапляють у репозиторій:
# на ПК-2 вони є, але це не привід їх комітити.
```
Інших рядків не чіпав.
**Статус:** RESOLVED

---

### Q-003 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `tests/Ecr.Api.Tests/`, `tests/Ecr.Calculations.Tests/`,
`tools/Ecr.Bootstrap.Excel/`, `tools/Ecr.Migration.PiAf/`
**Контекст:** `dotnet restore Ecr.sln`.

**Суть:**
`Ecr.sln` оголошує чотири проєкти, для яких у `05a-skeleton-solution.md` немає
секції з `.csproj`. Файли вихідного коду цих проєктів у пакеті є (`06d`, `05j`).

**Текст помилки:**
```
error MSB3202: The project file "D:\repos\ECR Web\tests\Ecr.Calculations.Tests\Ecr.Calculations.Tests.csproj" was not found. [Ecr.sln]
error MSB3202: The project file "D:\repos\ECR Web\tests\Ecr.Api.Tests\Ecr.Api.Tests.csproj" was not found. [Ecr.sln]
error MSB3202: The project file "D:\repos\ECR Web\tools\Ecr.Bootstrap.Excel\Ecr.Bootstrap.Excel.csproj" was not found. [Ecr.sln]
error MSB3202: The project file "D:\repos\ECR Web\tools\Ecr.Migration.PiAf\Ecr.Migration.PiAf.csproj" was not found. [Ecr.sln]
```

**Що зробив:** створив чотири `.csproj` **за зразком сусідніх із пакета**
(`Ecr.Application.Tests.csproj`, `Ecr.Infrastructure.Tests.csproj`,
`Ecr.DataGen.csproj`) — ті самі властивості, ті самі `PackageReference` без
версій (central package management). Посилання виведені з `using` у вихідних
файлах:

* `Ecr.Api.Tests` → `Ecr.Api`, `Ecr.TestKit`, `Microsoft.AspNetCore.Mvc.Testing`
  (потрібен для `WebApplicationFactory<Program>` у `ApiConventionTests`);
* `Ecr.Calculations.Tests` → `Ecr.Calculations`, `Ecr.TestKit`;
* `tools/Ecr.Bootstrap.Excel` → `Domain`, `Application`, `Infrastructure`,
  `Microsoft.Extensions.Hosting`, `ClosedXML` (його `Program.cs` описує читання
  книги через ClosedXML);
* `tools/Ecr.Migration.PiAf` → `Domain`, `Application`, `Infrastructure`,
  `Microsoft.Extensions.Hosting`.

**Нових пакетів у `Directory.Packages.props` не додавав** — усі вже оголошені.
**Статус:** RESOLVED

---

### Q-004 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `Directory.Packages.props`
**Контекст:** `dotnet restore Ecr.sln`.

**Суть:**
Версії з `04-environment.md` §3 не проходять restore з двох причин:
(1) `Microsoft.Data.SqlClient 6.0.1` — downgrade відносно вимоги
`Microsoft.EntityFrameworkCore.SqlServer` (`>= 6.1.1`);
(2) NuGet audit знаходить вразливості, а `Directory.Build.props` має
`TreatWarningsAsErrors=true` з порожнім `WarningsNotAsErrors`, тому
`NU1902`/`NU1903` стають помилками і зупиняють restore.

**Текст помилок (зведено до унікальних):**
```
error NU1109: Detected package downgrade: Microsoft.Data.SqlClient from 6.1.1 to centrally defined 6.0.1.
error NU1902: Package 'NCalcSync' 5.4.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-3w5p-95mh-gq75
error NU1902: Package 'NCalc.Core' 5.4.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-3w5p-95mh-gq75
error NU1903: Package 'Microsoft.AspNetCore.Authentication.Negotiate' 10.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-2p3q-h3hg-jcqq
error NU1903: Package 'Microsoft.AspNetCore.Authentication.Negotiate' 10.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-8prm-248r-h957
error NU1903: Package 'Microsoft.OpenApi' 2.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-v5pm-xwqc-g5wc
error NU1903: Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-2m69-gcr7-jv3q
error NU1903: Package 'SSH.NET' 2023.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-q939-rpr3-3284
error NU1903: Package 'System.Security.Cryptography.Xml' 9.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-23rf-6693-g89p
```

**Що зробив:**

| Пакет | Було | Стало | Мажорна лінія |
|---|---|---|---|
| усі `Microsoft.EntityFrameworkCore.*` | 10.0.0 | 10.0.11 | без змін |
| усі `Microsoft.Extensions.*` | 10.0.0 | 10.0.11 | без змін |
| усі `Microsoft.AspNetCore.*` (разом із `Mvc.Testing`) | 10.0.0 | 10.0.11 | без змін |
| `Microsoft.Data.SqlClient` | 6.0.1 | 6.1.6 | без змін |
| `Testcontainers.MsSql` | 4.0.0 | 4.14.0 | без змін |
| **`NCalcSync`** | **5.4.0** | **6.1.1** | **5 → 6, ЗМІНЕНО** |

Транзитивні `Microsoft.OpenApi`, `SQLitePCLRaw.*`, `SSH.NET`,
`System.Security.Cryptography.Xml` підтягнулися виправленими самі — окремих
`PackageVersion` для транзитивного пінінгу не додавав.
`Scalar.AspNetCore 2.0.0`, `ClosedXML 0.104.2`, `Quartz 3.13.1`, `xunit 2.9.2`,
`NSubstitute 5.3.0`, `NetArchTest.Rules 1.3.2`, `coverlet.collector 6.0.2`,
`Microsoft.NET.Test.Sdk 17.12.0` — **без змін**, резолвляться як написано.
Список заборонених пакетів не чіпав; жодного нового пакета не з'явилося.

**⚠ Мажорна лінія `NCalcSync` змінена свідомо** (`04-environment.md`: «Мажорні
лінії міняти не можна без запису в `questions.md` з поясненням»).
Причина: у лінії 5.x виправлення **не існує** — advisory
`GHSA-3w5p-95mh-gq75` (DoS через необмежене обчислення факторіала) закритий
лише в `6.1.1`; останній 5.x — 5.13.0 і теж вразливий.
Ризик зараз мінімальний: **NCalc у коді ще не використовується жодного разу**
(`grep` по `src/`: лише згадки в XML-doc `IFormulaEngine` і в `Enums.cs`),
перше реальне використання — Етап 2. Якщо на Етапі 2 API 6.x виявиться
несумісним із задумом `D-19`, рішення відкочується разом із записом про
прийнятий ризик.

**Що потрібно від людини:** підтвердити мажорний перехід `NCalcSync 5 → 6`
або дати вказівку лишитися на 5.13.0 з явним виключенням `NU1902`.
**Статус:** OPEN · restore працює; рішення про мажор потребує підтвердження

---

### Q-005 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Domain/Abstractions/Entity.cs`,
`src/Ecr.Domain/Entities/Security/Permission.cs`
**Контекст:** перший `dotnet build Ecr.sln`.

**Суть:**
Базовий клас із `05b` §2 обмежений `where TId : struct`, а `Permission` із
того самого `05b` успадковує `Entity<string>`.

**Текст помилки:**
```
D:\repos\ECR Web\src\Ecr.Domain\Entities\Security\Permission.cs(10,21): error CS0453:
The type 'string' must be a non-nullable value type in order to use it as parameter 'TId'
in the generic type or method 'Entity<TId>'
```

**Чому правий `Permission`, а не `Entity`:** `02a-db-schema.md` рядок 1651 —
```sql
CREATE TABLE sec.Permission
(
    Code        nvarchar(64)  NOT NULL,
    [Group]     nvarchar(64)  NOT NULL,
    NameL10n    nvarchar(max) NOT NULL,
    IsDangerous bit           NOT NULL CONSTRAINT DF_Perm_Dang DEFAULT(0),
    CONSTRAINT PK_Permission PRIMARY KEY (Code)
);
```
Первинний ключ — рядковий код, на нього посилається
`FK_RolePerm_Perm … REFERENCES sec.Permission (Code)`. Правити `Permission`
означало б правити схему БД — заборонено.

**Що зробив:** послабив обмеження `Entity<TId>`:
`where TId : struct, IEquatable<TId>` → `where TId : IEquatable<TId>`.
Щоб зберегти семантику і не отримати `NullReferenceException` на
`default(string)`, порівняння переведено з `Id.Equals(...)` на
`EqualityComparer<TId>.Default.Equals(...)`, а автовластивість дістала
ініціалізатор `= default!` (інакше `CS8618`, який `.editorconfig` оголошує
помилкою). Для `int` і `long` поведінка не змінилася.
`<typeparam>` доповнено згадкою `string`.
**Статус:** RESOLVED

---

### Q-006 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `Directory.Build.props`
**Контекст:** `dotnet build Ecr.sln` після Q-005.

**Суть:**
Конфігурація пакета (`TreatWarningsAsErrors=true` + порожній
`WarningsNotAsErrors` + `EnforceCodeStyleInBuild=true` +
`AnalysisLevel=latest-recommended` + `.editorconfig`, де стильові правила
оголошені як `warning`) робить помилками **власний код пакета**. Частину цих
зауважень неможливо виправити, не порушивши заборону чіпати імена типів.

**Текст помилок (унікальні правила):**
```
error CA1711: Rename type name Permission so that it does not end in 'Permission'
error CA1720: Identifier 'String' contains type name     (Enums.cs — ColumnDataType.String)
error CA1720: Identifier 'Int' contains type name        (Enums.cs — ColumnDataType.Int)
error CA1720: Identifier 'Decimal' contains type name    (Enums.cs — ColumnDataType.Decimal)
error CA1822: Member 'CanConvert' does not access instance data and can be marked as static
error IDE0040: Accessibility modifiers required          (IClock.cs)
error IDE0011: Add braces to 'if' statement              (CellValueData, LocalizedText, PeriodKey)
error IDE0065: Using directives must be placed outside of a namespace declaration
               (CellValueData, EcrCode, LocalizedText, PeriodKey, RowKey)
```

`CA1711` вимагає перейменувати тип `Permission`, `CA1720` — члени
`ColumnDataType.String/Int/Decimal`. І те, і те — контрактні імена
(`02-contracts.md` §2, `02a-db-schema.md`), змінювати заборонено назавжди.
`IDE0065` спрацьовує на файлах із поміткою «копіювати **без змін**»
(`05b` §1) — правити їх означає розсинхронити копію з єдиним джерелом істини.
`CA1822` — наслідок того, що тіло методу поки що `NotImplementedException`.

**Що зробив:** правила лишив **увімкненими**, але вивів зі списку блокуючих:
```xml
<WarningsNotAsErrors>IDE0011;IDE0040;IDE0065;CA1711;CA1720;CA1822</WarningsNotAsErrors>
```
Вони видимі як попередження на кожній збірці. Жодного правила не вимкнено,
`.editorconfig` не чіпав, `AnalysisLevel` і `EnforceCodeStyleInBuild` не
знижував, код під аналізатор не підганяв.

**Що потрібно від людини:** вирішити, як пакет живе далі — лишити ці шість
правил попередженнями назавжди чи привести код `02-contracts.md` до них
(це вже правка контрактів, не моя).
**Статус:** OPEN · збірка не блокується; рішення про стиль — за людиною

---

### Q-007 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Application/Ports/IRepository.cs`, рядок 6
**Контекст:** `dotnet build Ecr.sln`.

**Суть:**
Друкарська помилка в `05c-skeleton-application.md` (рядок 50): усередині
XML-doc пропущено префікс `///`, через що рядок став кодом.

**Цитата джерела (`05c`, рядки 47–51):**
```
/// <summary>
/// Сховище агрегата. Навмисно вузьке: <see cref="IQueryable{T}"/> назовні не
/// віддається, бо тоді деталі провайдера протікають у use-cases і
<c>ToList()</c> без <c>Take()</c> стає питанням дисципліни, а не типу.
/// </summary>
```

**Текст помилки:**
```
src\Ecr.Application\Ports\IRepository.cs(6,1):  error CS1570: XML comment has badly formed XML -- 'Expected an end tag for element 'summary'.'
src\Ecr.Application\Ports\IRepository.cs(6,12): error CS1002: ; expected
src\Ecr.Application\Ports\IRepository.cs(6,12): error CS1022: Type or namespace definition, or end-of-file expected
src\Ecr.Application\Ports\IRepository.cs(6,2):  error CS0116: A namespace cannot directly contain members such as fields, methods or statements
```

**Що зробив:** додав `/// ` на початок рядка. Текст не змінював.
**Статус:** RESOLVED

---

### Q-008 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Application/Ecr.Application.csproj`
**Контекст:** `dotnet build Ecr.sln`.

**Суть:**
`IFormulaEngine` (контракт, `02-contracts.md` §5) оперує типами з
`Ecr.Expressions` — `ParseResult`, `ParsedExpression`, `IEvaluationContext`,
`OrderingResult`, — але `Ecr.Application.csproj` посилався лише на `Ecr.Domain`.

**Розбіжність документів:**
```
05-skeleton.md §4:   Ecr.Application       → Ecr.Domain
tz/03 §3.3:          Ecr.Application  use-cases, порти, DTO. Знає Domain і Expressions.
```
Заборонний список `05-skeleton.md` §4 (⛔) залежності `Application → Expressions`
**не** містить; вісім арх-правил `tz/03` §3.3 — теж ні (правило 2 забороняє лише
`Application → Infrastructure`). Тому правий `tz/03`, а таблиця в
`05-skeleton.md` §4 неповна.

**Що зробив:** додав `ProjectReference` на `Ecr.Expressions` з коментарем-посиланням
на `tz/03` §3.3. Після цього `Ecr.Application` компілюється.
**Статус:** RESOLVED · але цей крок оголив **Q-013**

---

### Q-009 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Expressions/Parsing/ParseResult.cs`
**Контекст:** директива `COPY FROM` у `05d`.

**Суть:**
`05d` наказує перенести `ParseResult`, `ParsedExpression`,
`ExpressionDiagnostic` з `02b-expressions.md#ast` у
`src/Ecr.Expressions/Parsing/ParseResult.cs`, але у блоці `02b` оголошено
`namespace Ecr.Expressions;` (там файл лежить у корені проєкту:
`// src/Ecr.Expressions/ParseResult.cs`).

**Як вирішив:** namespace = `Ecr.Expressions.Parsing`. Дві причини:
1. `05-skeleton.md` §3 — «Namespace: file-scoped, збігається зі шляхом»;
2. `TypeChecker.cs` і `UnitChecker.cs` із того самого `05d` явно пишуть
   `IReadOnlyList<Ecr.Expressions.Parsing.ExpressionDiagnostic>`, тобто автор
   очікує саме цей namespace.

Решта вмісту скопійована дослівно; рядок-локатор у першому рядку приведено до
фактичного шляху.
**Статус:** RESOLVED

---

### Q-010 · DECIDED · Етап 0 · 2026-09-04

**Де:** `05c`, `05d`, `05g`, `02b`, `02-contracts.md`, `02c`, `06-tests.md`
**Контекст:** крок 2 Етапу 0 — видобування файлів із markdown.

**Суть:** п'ять секцій `05*` мають заголовок на **два** файли, і не в усіх є
два блоки коду. Плюс три директиви `COPY FROM`.

| Секція | Блоків | Як розділив |
|---|---:|---|
| `05c` `Ports/IExcelExporter.cs` / `IExcelImporter.cs` | 1 | `IExcelExporter` + `ExcelExportOptions` → перший файл; `IExcelImporter` + `ImportPreview` + `ImportChange` + `ImportRejection` → другий |
| `05c` `Common/PagedResult.cs` і `CursorPagination.cs` | 1 | `PagedResult<T>` → перший; `CursorRequest` → другий |
| `05d` `Graph/DependencyGraph.cs` і `TopologicalSorter.cs` | 2 | по блоку на файл (`OrderingResult` — разом із сортувальником) |
| `05g` `Excel/ExcelImporter.cs` і `ImportDiffBuilder.cs` | 1 | створено лише `ExcelImporter.cs`; **`ImportDiffBuilder.cs` вмісту в пакеті не має** — див. Q-015 |
| `05g` `Excel/DependencyInjection.cs` і `PiAf/DependencyInjection.cs` | 1 | створено лише `PiAf/DependencyInjection.cs`; **Excel-варіанта в пакеті немає** — див. Q-015 |

Директиви `COPY FROM`, виконані дослівно:

* `02b` §11 → `src/Ecr.Expressions/Ast/AstNode.cs` і
  `src/Ecr.Expressions/Parsing/ParseResult.cs` (див. Q-009);
* `02-contracts.md` — 32 блоки, кожен із рядком-шляхом `// src/...` у першому
  рядку → файли, перелічені в `05b` §1 і `05c` §1, плюс ті, що є лише в
  контрактах (`IUiStringCatalog`, `ISimulationService`, `IOrphanScanner`,
  `EcrProblemDetails`, `TemplateDiffDto`, `TemplateStructureDto`,
  `RegistryEntryDto`, `SimulationResultDto`);
* `02c` §9 → `tests/Ecr.TestKit/Fixtures/water-demo.json` (JSON провалідовано
  парсером — коректний).

Розбіжностей вмісту з джерелом немає: витягування механічне, скриптом.
**Разом створено 344 файли.**
**Статус:** RESOLVED

---

### Q-011 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** 25 файлів у `Ecr.Application` і `Ecr.Expressions`
**Контекст:** `dotnet build Ecr.sln`, серія `error CS0246`.

**Суть:**
`05-skeleton.md` §3 вимагає «усі потрібні `using`, явно», але в частині файлів
скелета їх немає.

**Приклад тексту помилки:**
```
src\Ecr.Application\Workflow\SubmitSheetHandler.cs(17,5): error CS0246:
The type or namespace name 'ICurrentUser' could not be found (are you missing a using directive or an assembly reference?)
```

**Що зробив:** додав відсутні `using` — і нічого більше.

| Тип | Namespace | Файлів |
|---|---|---:|
| `ICurrentUser` | `Ecr.Application.Common` | 14 |
| `LocalizedText`, `PeriodKey` | `Ecr.Domain.ValueObjects` | 3 |
| `ChangeClass`, `TableLayoutKind`, `TableRowMode` | `Ecr.Domain.Enums` | 2 |
| `TemplateDiffDto`, `TemplateStructureDto` | `Ecr.Application.Templates.Dto` | 2 |
| `RegistryEntryDto`, `RegistryEntryUpsertDto` | `Ecr.Application.Registries.Dto` | 2 |
| `ColumnDto`, `RowDto` | `Ecr.Application.Documents.Dto` | 1 (див. Q-012) |
| `SimulationResultDto` | `Ecr.Application.Calculations.Dto` | 1 |
| `AccessProfile` | `Ecr.Application.Security` | 1 |
| `ParseResult`, `ParsedExpression`, `IEvaluationContext`, `OrderingResult` | `Ecr.Expressions.*` | 1 |
| `ExpressionValueType` | `Ecr.Expressions.Ast` | 1 |

Жодної сигнатури, жодного імені типу, жодного тіла методу не змінено.
**Статус:** RESOLVED

---

### Q-012 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs`
**Контекст:** `error CS0246: 'ColumnDto'`, `error CS0246: 'RowDto'`.

**Суть:**
`TemplateStructureDto` (namespace `Ecr.Application.Templates.Dto`) використовує
`ColumnDto` і `RowDto`, а оголошені вони рівно один раз — у
`Documents/Dto/TableSliceDto.cs` (namespace `Ecr.Application.Documents.Dto`).
У `02-contracts.md` §10 обидва DTO лежать в одному розділі без `using`, тому
розбіжність з'явилася при розкладанні по папках.

**Як вирішив:** додав `using Ecr.Application.Documents.Dto;`. Нових типів не
створював, форму наявних не змінював.

**⚠ Зауваження для рев'ю (не блокує збірку):** `RowDto` має поле
`IReadOnlyDictionary<string, object?> Cells`, тобто це рядок **зі значеннями**.
У структурі шаблону значень бути не може — там опис рядків
(`RowKey`, `Ordinal`, `RowKind`, `Label`). Схоже, `TemplateStructureDto` мав би
мати власні `ColumnDto`/`RowDto`. Форму DTO я не вигадував: це форма відповіді
`GET /template-versions/{id}/structure`, а рішення про форму API — не моє
(`08-workflow.md` §7).
**Статус:** OPEN · компілюється; семантика DTO — питання до автора контракту

---

### Q-013 · CONFLICT · Етап 0 · 2026-09-04 · **БЛОКЕР, робота зупинена**

**Де:** `src/Ecr.Expressions/FormulaEngine.cs`
**Контекст:** `dotnet build Ecr.sln` після Q-008.

**Суть:**
`FormulaEngine` реалізує `IFormulaEngine`, який за контрактом лежить у
`Ecr.Application.Ports`. Отже `Ecr.Expressions` мусить посилатися на
`Ecr.Application`. Але `Ecr.Application` уже посилається на `Ecr.Expressions`
(Q-008 — бо контракт `IFormulaEngine` оперує типами виразів).
**Циклічна залежність між проєктами; у .NET неможлива.**

**Цитати документів:**
```
05-skeleton.md §1:      src/Ecr.Expressions/FormulaEngine.cs

02-contracts.md §5:     // src/Ecr.Application/Ports/IFormulaEngine.cs
                        namespace Ecr.Application.Ports;
                        public interface IFormulaEngine
                        {
                            ParseResult Parse(string expression, ExpressionDialect dialect);
                            IReadOnlyList<FormulaDependencyRef> ExtractDependencies(ParsedExpression expression, DependencyContext context);
                            EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);
                            OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
                        }

05d, FormulaEngine.cs:  using Ecr.Application.Ports;
                        …
                        namespace Ecr.Expressions;
                        public sealed class FormulaEngine(
                            Parser parser, DependencyExtractor dependencyExtractor,
                            Evaluator evaluator, TopologicalSorter sorter) : IFormulaEngine

tz/03 §3.3:             Ecr.Application  use-cases, порти, DTO. Знає Domain і Expressions.

tz/03 §3.4:             | `IExpressionEngine` | власний парсер + NCalc | два діалекти |
```

**Текст помилки:**
```
D:\repos\ECR Web\src\Ecr.Expressions\FormulaEngine.cs(1,11): error CS0234: The type or namespace name 'Application' does not exist in the namespace 'Ecr' (are you missing an assembly reference?)
D:\repos\ECR Web\src\Ecr.Expressions\FormulaEngine.cs(18,33): error CS0246: The type or namespace name 'IFormulaEngine' could not be found (are you missing a using directive or an assembly reference?)
```

**Що вже пробував:**
1. `Ecr.Application → Ecr.Expressions` (Q-008): `Ecr.Application` компілюється,
   `Ecr.Expressions/FormulaEngine.cs` лишається червоним.
2. Зворотне посилання `Ecr.Expressions → Ecr.Application`: цикл, MSBuild
   відхиляє. Не застосовував.
3. Пошук реєстрації `IFormulaEngine` у DI, щоб зрозуміти задум автора:
   у пакеті її **немає взагалі** — ні в `Infrastructure/DependencyInjection.cs`,
   ні в `Api/Program.cs`. Отже підказки, де мав жити клас, у пакеті немає.

**Гіпотези (жодну не застосовував — це склад проєктів, Етап 0 його чіпати забороняє):**

* **A.** `FormulaEngine.cs` мав жити в `Ecr.Infrastructure` — єдиному проєкті,
  який за `05-skeleton.md` §4 знає і `Application`, і `Expressions`.
  `Ecr.Expressions` лишається чистим ядром; дефект — у розташуванні одного
  файла в `05-skeleton.md` §1. **Найдешевший варіант.**
* **B.** Порт мав жити в `Ecr.Expressions` — у `tz/03` §3.4 він і названий
  інакше, `IExpressionEngine`, а `02-contracts.md` §5 помилково зібрав його
  разом із портами застосунку. Тоді `Ecr.Application → Ecr.Expressions`
  лишається, а `IFormulaEngine` переїжджає. **Змінює контракт.**
* **C.** Типи виразів (`ParseResult`, `EvaluationResult`, …) мали жити в
  `Ecr.Domain` — тоді `Application` і `Expressions` незалежні одне від одного.
  **Найдорожчий варіант, зачіпає `05b`, `05c`, `05d`.**

**Що потрібно від людини:** вибрати A, B або C. Це склад проєктів і
архітектурна межа — `08-workflow.md` §5 і §7 прямо забороняють вирішувати це
самому.
**Статус:** OPEN · **робота зупинена**

---

### Q-014 · CONTRACT · Етап 0 · 2026-09-04 · **БЛОКЕР, робота зупинена**

**Де:** `src/Ecr.Application/Ports/IFormulaEngine.cs`,
`src/Ecr.Application/Ports/ICalculationModule.cs`,
`src/Ecr.Application/Ports/IExternalDataSource.cs`
**Контекст:** `dotnet build Ecr.sln`.

**Суть:**
Три порти з `02-contracts.md` §5 використовують **десять типів, яких у пакеті
не існує ніде**. Перевірено `grep` по всьому `docs/` — `build/`, `tz/`,
`reference/`, `architecture/`: ці імена **вживаються**, але не **оголошуються**.

| Тип | Де вживається | Оголошення в пакеті |
|---|---|---|
| `FormulaDependencyRef` | `IFormulaEngine.ExtractDependencies` | немає |
| `DependencyContext` | `IFormulaEngine.ExtractDependencies` | немає |
| `FormulaNode` | `IFormulaEngine.BuildEvaluationOrder` | немає |
| `EvaluationResult` | `IFormulaEngine.Evaluate` | немає — файл `src/Ecr.Expressions/Evaluation/EvaluationResult.cs` є в дереві `05-skeleton.md` §1, але секції з вмістом немає (Q-015) |
| `MethodologyDescriptor` | `ICalculationModule.CanHandle` | немає |
| `CalculationInput` | `ICalculationModule.ExecuteAsync` | немає |
| `CalculationOutput` | `ICalculationModule.ExecuteAsync` | немає |
| `SourceEntityDescriptor` | `IExternalDataSource.DiscoverAsync` | немає |
| `CollectionRequest` | `IExternalDataSource.ReadAsync` | немає |
| `CollectionResult` | `IExternalDataSource.ReadAsync` | немає |

**Текст помилок (унікальні):**
```
src\Ecr.Application\Ports\IFormulaEngine.cs(24,19):     error CS0246: The type or namespace name 'FormulaDependencyRef' could not be found
src\Ecr.Application\Ports\IFormulaEngine.cs(24,90):     error CS0246: The type or namespace name 'DependencyContext' could not be found
src\Ecr.Application\Ports\IFormulaEngine.cs(27,5):      error CS0246: The type or namespace name 'EvaluationResult' could not be found
src\Ecr.Application\Ports\IFormulaEngine.cs(33,55):     error CS0246: The type or namespace name 'FormulaNode' could not be found
src\Ecr.Application\Ports\ICalculationModule.cs(17,20): error CS0246: The type or namespace name 'MethodologyDescriptor' could not be found
src\Ecr.Application\Ports\ICalculationModule.cs(20,10): error CS0246: The type or namespace name 'CalculationOutput' could not be found
src\Ecr.Application\Ports\ICalculationModule.cs(20,42): error CS0246: The type or namespace name 'CalculationInput' could not be found
src\Ecr.Application\Ports\IExternalDataSource.cs(16,24): error CS0246: The type or namespace name 'SourceEntityDescriptor' could not be found
src\Ecr.Application\Ports\IExternalDataSource.cs(22,10): error CS0246: The type or namespace name 'CollectionResult' could not be found
src\Ecr.Application\Ports\IExternalDataSource.cs(22,38): error CS0246: The type or namespace name 'CollectionRequest' could not be found
```

Ті самі типи вживаються і далі за течією: `Ecr.Calculations/CalculationInputBuilder.cs`,
`CalculationOutputWriter.cs`, `Ecr.Adapters.PiAf/PiSqlClientDataSource.cs`,
`PiWebApiDataSource.cs`, `Ecr.Expressions/FormulaEngine.cs`.

**Чому не вигадав сам:**
`08-workflow.md` §7 — «Не вирішуй ніколи: зміна контракту: сигнатури, DTO,
коду помилки, схеми БД». Це не деталь реалізації:
`CalculationInput`/`CalculationOutput` — доказова база розрахунку
(`calc.CalculationInput` у `02a-db-schema.md`, `B08`), `CollectionResult` —
форма відповіді збору з PI AF, `FormulaDependencyRef` — те, що лягає в
`cfg.FormulaDependency` і визначає граф перерахунку. Форма кожного з них
впливає на записи в базі і на числа у звіті.

**Часткові джерела, які можуть допомогти автору контракту:**
`reference/backend/B08-calc-schema.md` і `B13-ecr-calculation-engine.md`
описують `calc.CalculationInput` як **таблицю**;
`reference/design/13-backend-assignment.md` і
`architecture/docs/13-backend-assignment.md` згадують
`CollectionRequest`/`CollectionResult` у прозі. Виводити з опису таблиці форму
`record` — саме та «творчість», яку Етап 0 забороняє.

**Що потрібно від людини:** оголошення цих десяти типів у `02-contracts.md`
(або вказівка, з якого документа взяти їх дослівно).
**Статус:** OPEN · **робота зупинена**

---

### Q-015 · SCOPE · Етап 0 · 2026-09-04

**Де:** `05-skeleton.md` §1 (дерево) проти `05a`…`05j`
**Контекст:** крок 2 Етапу 0 — звірка створеного з деревом.

**Суть:**
41 файл оголошений у дереві `05-skeleton.md` §1, але **не має секції з вмістом**
у жодній частині `05a`…`05j`. Створити їх «точно як написано» неможливо —
писати нема чого. Не створював навмисно: вигадати вміст контролера означає
вигадати форму API (`08-workflow.md` §7).

**Перелік (41):**
```
src/Ecr.Api/Controllers/   TemplatesController · TemplateVersionsController ·
                           ProjectsController · PeriodsController · DocumentsController ·
                           RegistriesController · UnitsController · MethodologiesController ·
                           SecurityController · AuditController · JobsController ·
                           SourcesController · ReportsController                    (13)
src/Ecr.Api/Auth/          CurrentUser.cs                                            (1)
src/Ecr.Api/Health/        JobsHealthCheck.cs · SourcesHealthCheck.cs                (2)
src/Ecr.Application/*/Dto/ TemplateDtos.cs · PeriodDtos.cs · RegistryDtos.cs ·
                           UnitDtos.cs · CalculationDtos.cs                          (5)
src/Ecr.Expressions/       Evaluation/EvaluationResult.cs ·
                           Functions/MethodologyFunctions.cs                         (2)
src/Ecr.Infrastructure/Persistence/Sql/  01-filegroups.sql · 02-partitions.sql ·
                           03-archive-proc.sql · 04-partition-maintenance.sql ·
                           05-rpt-views.sql · 06-rcsi.sql                            (6)
src/Ecr.Infrastructure/    Persistence/UnitOfWork.cs · Reporting/ReportSnapshotBuilder.cs ·
                           Jobs/CollectionJob.cs · Jobs/ConsistencyCheckJob.cs ·
                           Jobs/ReportSnapshotJob.cs · Jobs/PartitionCheckJob.cs ·
                           Jobs/NotificationJob.cs                                   (7)
src/Ecr.Adapters.Excel/    ImportDiffBuilder.cs · StyleMapper.cs · DependencyInjection.cs  (3)
src/Ecr.Adapters.PiAf/     PiAfCatalogReader.cs                                      (1)
src/Ecr.Web/               index.html                                                (1)
```

Реально створених файлів: **344**. Секцій із вмістом у `05*`/`06*`: 302;
плюс 32 з `02-contracts.md`, 8 із розділених секцій і 2 з `COPY FROM`.

**Наслідки, які вже видно:**

* `Evaluation/EvaluationResult.cs` — **прямо блокує збірку** (Q-014).
* `ImportDiffBuilder` — його вимагає конструктор `ExcelImporter`
  (`ImportDiffBuilder diffBuilder`), тобто збірка `Ecr.Adapters.Excel`
  впаде, щойно дійде черга.
* `AddExcelAdapters()` викликається в `Program.cs` (`05h`, рядок 31), а
  `Ecr.Adapters.Excel/DependencyInjection.cs` не існує.
* `UnitOfWork` — реалізація порту `IUnitOfWork`, згадана в
  `Infrastructure/DependencyInjection.cs` (`AddScoped<IUnitOfWork, UnitOfWork>()`).
* Шість `.sql` — `09-commands.md` §3 наказує виконувати їх `sqlcmd`;
  `06-rcsi.sql` вмикає `READ_COMMITTED_SNAPSHOT` (`04-environment.md` §2.1).
* `index.html` — без нього `npm run build` неможливий у принципі.
* Решта (13 контролерів, два health-checks, п'ять DTO-файлів) збірку не ламає,
  але зріз Етапу 1 («через API можна створити шаблон, наповнити структуру…»)
  без них не досяжний.

**Що потрібно від людини:** доповнити `05*` секціями для цих файлів — або
принаймні для тих семи, що блокують збірку: `EvaluationResult.cs`,
`ImportDiffBuilder.cs`, `Excel/DependencyInjection.cs`, `UnitOfWork.cs`,
шість `.sql`, `index.html`.
**Статус:** OPEN

---

### Q-016 · ENV · Етап 0 · 2026-09-04

**Де:** середовище ПК-2
**Контекст:** §1 `09-commands.md` — перевірка середовища.

**Суть:** звірка з `04-environment.md` §1.

| Компонент | Очікується | Фактично | Оцінка |
|---|---|---|---|
| .NET SDK | 10.0.x | **10.0.301** (поруч є 8.0.412) | OK |
| Node.js | 22.x LTS | **22.19.0** | OK |
| npm | 10.x | **10.9.3** | OK |
| git | будь-яка | 2.46.2.windows.1 | OK |
| Інтернет (nuget.org, npmjs) | потрібен | доступний | OK |
| **Docker** | будь-яка | **не запущений** | **деградований режим** |
| SQL Server | 2019+ | не перевірявся — див. нижче | — |

**Текст помилки Docker:**
```
error during connect: Get "http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.51/containers/json":
open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.
```

**Наслідок** (`04-environment.md` §5): тести з трейтом `Category=Integration`
не запускаються; партиціонування, `TRUNCATE PARTITIONS`, складені FK і RCSI
лишаються неперевіреними. Це передбачений режим, а не помилка конфігурації.

SQL Server не перевірявся навмисно: `04-environment.md` §2.1 фіксує заміри від
2026-09-04 (`D-101`) і каже перевіряти не наново, а **розбіжність**; жодна
задача Етапу 0 до БД не звертається.
**Статус:** OPEN · враховувати на етапах з інтеграційними тестами

---

### Q-017 · SCOPE · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Web`
**Контекст:** `npm install`, `npx vitest run`, `npx tsc --noEmit`.

**Суть:**
`npm install` пройшов на версіях із `04-environment.md` §4 **без жодної
правки** (362 пакети). `vitest` знаходить і запускає 23 тести, усі падають з
`not implemented` — саме як вимагає Етап 0. А `typecheck` не проходить:
бракує згенерованих і неоголошених модулів.

**Текст помилок:**
```
src/api/client.ts(1,28):              error TS2307: Cannot find module './schema' or its corresponding type declarations.
src/api/client.ts(1,1):               error TS6133: 'paths' is declared but its value is never read.
src/features/grid/DocumentGrid.tsx(1,36): error TS2307: Cannot find module '@/api/types' or its corresponding type declarations.
src/features/grid/DocumentGrid.tsx(1,1):  error TS6133: 'TableSliceDto' is declared but its value is never read.
src/features/grid/useCellPatch.ts(1,60):  error TS2307: Cannot find module '@/api/types'
src/app/App.tsx(23,24):                   error TS2503: Cannot find namespace 'JSX'.
src/features/grid/DocumentGrid.tsx(39,58): error TS2503: Cannot find namespace 'JSX'.
```

**Розбір:**
1. `./schema` — це `src/api/schema.d.ts`, який генерується командою
   `npm run api:types` з **запущеного** бекенда
   (`openapi-typescript http://localhost:5080/openapi/v1.json`). Бекенд не
   збирається (Q-013, Q-014), тому генерувати нема з чого. Очікувано.
2. `@/api/types` — такого модуля немає ні в дереві `05-skeleton.md` §1,
   ні в `05i`. Ще один випадок Q-015, уже на фронтенді.
3. `JSX.Element` під React 19 і `@types/react` 19 треба писати як
   `React.JSX.Element` — глобальний namespace `JSX` прибрано. Дефект `05i`.
   Не правив: це Етап 6, а не мій SCOPE.

`npm run build` не запускав: він потребує `index.html`, якого немає (Q-015),
і `tsc -b`, який упаде на тих самих помилках.
**Статус:** OPEN · Етап 6
