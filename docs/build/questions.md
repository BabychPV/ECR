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
| Q-004 | BOOTSTRAP-FIX | версії пакетів: downgrade + вразливості | RESOLVED · мажор `NCalcSync 5→6` підтверджено 2026-09-04 |
| Q-005 | **CONTRACT** | `Entity<TId> : struct` проти `Permission : Entity<string>` | RESOLVED · перекваліфіковано за рев'ю (В-1) |
| Q-006 | BOOTSTRAP-FIX | аналізатори ламають власний код пакета (18 правил) | **OPEN** — рішення про стиль |
| Q-007 | BOOTSTRAP-FIX | `IRepository.cs` — пропущений `///` | RESOLVED |
| Q-008 | BOOTSTRAP-FIX | `Ecr.Application → Ecr.Expressions` | RESOLVED |
| Q-009 | DECIDED | namespace `ParseResult.cs` | RESOLVED |
| Q-010 | DECIDED | секції на два файли, директиви `COPY FROM` | RESOLVED |
| Q-011 | BOOTSTRAP-FIX | відсутні `using` у 34 файлах | RESOLVED · доповнено за рев'ю (К-2) |
| Q-012 | DECIDED | `TemplateStructureDto` бере `ColumnDto`/`RowDto` з `Documents.Dto` | **OPEN** — семантика DTO |
| Q-013 | CONFLICT | циклічна залежність `FormulaEngine` | RESOLVED · варіант **A**, 2026-09-04 |
| Q-014 | CONTRACT | десять контрактних типів не оголошені ніде | RESOLVED · перенесені в `02-contracts.md`, 2026-09-04 |
| Q-015 | SCOPE | 41 файл у дереві без вмісту в `05*` | RESOLVED · усі створені, 2026-09-04 |
| Q-016 | ENV | Docker не запущений | **OPEN** |
| Q-017 | SCOPE | frontend: `typecheck` потребує згенерованих модулів | **OPEN** |
| Q-018 | CONFLICT | `Ecr.Calculations` і `Ecr.Adapters.PiAf` вимагають `Ecr.Infrastructure` | RESOLVED · варіант **B**, порти в контракті, 2026-09-04 |
| Q-019 | BOOTSTRAP-FIX | немає `[CollectionDefinition("SqlServer")]` | RESOLVED |
| Q-020 | DECIDED | мінімальні скелети для `StyleMapper`, `ImportDiffBuilder`, `CurrentUser` | RESOLVED |
| Q-021 | SCOPE | зонд: після зняття Q-014 і Q-018 збирається **все** | RESOLVED · інформаційний |
| Q-022 | BOOTSTRAP-FIX | `Ecr.TestKit` переривав `dotnet test` (`IsTestProject`) | RESOLVED |
| Q-023 | DECIDED | фронтенд: `index.html`, `main.tsx`, `api/types.ts`, `JSX` під React 19 | RESOLVED |
| Q-024 | DECIDED | п'ять скелетів, яких вимагає `Program.cs` | RESOLVED |
| Q-025 | SCOPE | результат рев'ю Етапу 0 і що з ним зроблено | RESOLVED |
| Q-028 | SCOPE | повний аудит Етапів 0–1 | RESOLVED |
| Q-029 | DECIDED | `01-filegroups.sql`: база — з підключення, каталог — автовизначення | RESOLVED |
| Q-026 | CONTRACT | `MethodologyRule`: сутність проти схеми БД | RESOLVED · схема права (ТЗ: `D-92`, `ФВ-9.5`, `ФВ-13.8`) |
| **Q-027** | **CONFLICT** | **22 сутності розходяться зі схемою БД** | **OPEN** · Етап 1 **не зачеплено** (`Q-028`), виконання за етапами 3–5 |

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
**Статус:** RESOLVED · 2026-09-04 перехід на `6.1.1` **підтверджено замовником**.
`04-environment.md` §3.1 варто оновити: там і досі написано `5.4.0`.

---

### Q-005 · CONTRACT · Етап 0 · 2026-09-04

> **Перекваліфіковано за рев'ю Етапу 0 (В-1).** Спершу записано як
> `BOOTSTRAP-FIX`, і робота пішла далі без зупинки. Це було неправильно:
> секція `05b` має заголовок `CONTRACT: 02-contracts.md#conventions`, а
> `Entity<TId>` — базовий тип **усіх** сутностей домену. Пом'якшення:
> у самому `02-contracts.md` цього типу немає (він живе лише в `05b`),
> тому формально це дефект скелета, а не тексту контракту, і зміна
> є розширенням, яке нічого не ламає. Але тип запису мав бути `CONTRACT`
> із зупинкою, а не `BOOTSTRAP-FIX`.

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

**Доповнення від 2026-09-04.** Коли збірка просунулася далі, той самий конфлікт
виявився ще в дванадцяти правилах — усі на коді, який пакет наказує брати
дослівно, або на заглушках, які за визначенням ще нічого не роблять:

```
CA1707  Remove the underscores from member name …    ← усі назви тестів українською з підкресленнями (08-workflow §9)
CA1716  Rename type RowSelector.Single … reserved language keyword  ← контрактний тип із 02b §11
CA1725  change parameter name builder to configurationBuilder       ← EcrDbContext.ConfigureConventions
CS9113  Parameter 'resolver' is unread                              ← первинні конструктори заглушок
CS0169  The field '_container' is never used                        ← SqlServerFixture, тіло ще не написане
CS0414  поле присвоєне, але не читається                            ← те саме
CS1572/CS1573/CS1574/CS1580/CS1584  неповні або нерезолвні XML-теги  ← XML-doc скелета
xUnit1026  Theory method does not use parameter 'kind'              ← заглушка Assert.Fail не використовує параметр
```

Повний перелік у `WarningsNotAsErrors` на сьогодні:
`IDE0011;IDE0040;IDE0065;CA1707;CA1711;CA1716;CA1720;CA1725;CA1822;CS0169;CS0414;CS9113;CS1572;CS1573;CS1574;CS1580;CS1584;xUnit1026`.

Більшість із них (`CS9113`, `CS0169`, `CS0414`, `xUnit1026`, `CA1822`) зникнуть
самі, щойно заглушки стануть реалізаціями. Решта (`CA1707`, `CA1711`, `CA1716`,
`CA1720`, `IDE0065`) — постійні, бо суперечать контрактним іменам і формату
скелета.

**Що потрібно від людини:** вирішити, як пакет живе далі — лишити ці правила
попередженнями назавжди чи привести код `02-contracts.md` до них (це вже
правка контрактів, не моя). Окремо варто зважити, чи не винести послаблення
для тестів (`CA1707`, `xUnit1026`) у власний `tests/Directory.Build.props`,
щоб вони не діяли на `src/`.
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

**Доповнення за рев'ю Етапу 0 (К-2).** Запис описував лише
`Ecr.Application` і `Ecr.Expressions`, хоча тим самим виправленням було
зачеплено ще **дев'ять** файлів. Рев'ювер знайшов їх скриптом — саме тому, що
в журналі їх не було. Повний перелік:

| Файл | Додано |
|---|---|
| `src/Ecr.Infrastructure/Caching/MetadataCache.cs` | `using Ecr.Infrastructure.Persistence;` |
| `src/Ecr.Infrastructure/Security/AccessDecisionService.cs` | те саме |
| `src/Ecr.Infrastructure/Security/SecurityStampValidator.cs` | те саме |
| `src/Ecr.Infrastructure/Jobs/PeriodStateJob.cs` | те саме |
| `src/Ecr.Infrastructure/Jobs/ArchiveJob.cs` | те саме |
| `src/Ecr.Infrastructure/Startup/SchemaValidator.cs` | те саме |
| `src/Ecr.Infrastructure/Startup/MetadataWarmup.cs` | те саме |
| `src/Ecr.Infrastructure/Jobs/OrphanScanJob.cs` | `using Ecr.Application.Ports;` |
| `src/Ecr.Application/Ports/ISimulationService.cs` | `using Ecr.Domain.Enums;` — знайдено вже **після** рев'ю, коли зняли послаблення `CS1574` (див. Q-025, В-4) |

Разом за `Q-011`: **34 файли**, у кожному додано лише `using`.
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

**Рішення замовника (2026-09-04): варіант A.**
`src/Ecr.Expressions/FormulaEngine.cs` → `src/Ecr.Infrastructure/Expressions/FormulaEngine.cs`,
namespace `Ecr.Expressions` → `Ecr.Infrastructure.Expressions`. Вміст класу,
сигнатури і `TODO` не змінені; додано `<remarks>` із поясненням, чому клас
живе саме тут. Контракти не чіпані.
**У `05-skeleton.md` §1 треба перенести рядок `FormulaEngine.cs`** із
`src/Ecr.Expressions/` у `src/Ecr.Infrastructure/Expressions/`.
**Статус:** RESOLVED · варіант A, 2026-09-04

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

---

**Рішення замовника (2026-09-04): скласти чернетки на затвердження.**

Усі десять оголошені. Розміщені **поруч зі своїми портами** — за конвенцією
самого пакета (пор. `IBackgroundJobScheduler.cs`, де в тому самому файлі
живуть `IBackgroundJob`, `IJobProgress` і `JobStatus`; `IExcelExporter.cs`,
де поруч `ExcelExportOptions`). Нових файлів через це не з'явилося, крім
`EvaluationResult.cs`, який і так є в дереві `05-skeleton.md` §1.

Кожен тип має в XML-doc блок `⚠ Q-014, обґрунтування` з позначкою
**тверде / часткове / слабке (здогадка)** і переліком того, на що я спирався.
Чотири типи позначені як здогадка — їх треба переглянути насамперед.

| Тип | Файл | Обґрунтування | На чому спирається |
|---|---|---|---|
| `FormulaDependencyRef` | `Ports/IFormulaEngine.cs` | **тверде** | колонки `cfg.FormulaDependency` + наявний `ExtractedDependency` (`05d`) + `TODO` «спроєктувати в контрактний тип» |
| `EvaluationResult` | `Expressions/Evaluation/EvaluationResult.cs` | **тверде** | `TODO` «загорнути результат і діагностики»; `Evaluator` повертає `ExpressionValue`, діагностика в пакеті одна — `ExpressionDiagnostic` |
| `CalculationOutput` + `CalculationOutputValue` + `CalculationTraceStep` | `Ports/ICalculationModule.cs` | **тверде** | 1:1 з `calc.CalculationResult` і `calc.CalculationStep` |
| `CalculationInput` + `CalculationArgument` | `Ports/ICalculationModule.cs` | часткове | колонки `calc.CalculationInput`; гранульованість «один рядок» — із `CalculationInputBuilder`. `TableInstanceId` і `PeriodKey` додав я |
| `MethodologyDescriptor` | `Ports/ICalculationModule.cs` | часткове | `CanHandle` вимагає `Level`; три режими — з `MethodologyVersion`, кожен визначає числа |
| `DependencyContext` | `Ports/IFormulaEngine.cs` | часткове | параметри `DependencyExtractor.Extract`; `TemplateVersionId` додав я |
| `CollectionResult` + `SourceDataPoint` + `TimeInterval` | `Ports/IExternalDataSource.cs` | часткове | `ext.RawDataPoint`; `FailedIntervals` — пряма вимога `TODO` «часткова відмова батча — це НЕ загальний провал» |
| **`FormulaNode`** | `Ports/IFormulaEngine.cs` | **слабке** | лише те, що `OrderingResult.Order` — це `int`, а `TopologicalSorter` має повернути шлях циклу |
| **`SourceEntityDescriptor`** | `Ports/IExternalDataSource.cs` | **слабке** | колонки `ext.SourceEntity` + `TODO` «збирати атрибути з їхнім UOM» |
| **`CollectionRequest`** | `Ports/IExternalDataSource.cs` | **слабке** | параметри `CollectionRunner.RunAsync` + `itg.CollectionRun`; `SourcePath` і `MaxPoints` додав я |

**Ухвалено 2026-09-04** за вказівкою замовника «вирішуй помилки відповідно ТЗ».
Перед закріпленням два з чотирьох «слабких» типів уточнено за схемою, і вони
перестали бути здогадкою:

* **`FormulaNode` → тверде.** Приведено до колонок `cfg.FormulaDef`
  (`02a` рядок 374): `FormulaDefId`, `TableDefId`, `Scope`, `ColumnDefId`,
  `RowDefId`. Ключове уточнення — вузол оперує **`RowDefId`, а не `RowKey`**:
  формула належить *визначенню* рядка. Це закріплено перевіркою
  `CK_Formula_Scope`, яка вимагає рівно ту комбінацію
  `ColumnDefId`/`RowDefId`, що відповідає `Scope`. Результат сортування лягає
  в `cfg.FormulaDef.EvaluationOrder`, яке «обчислюється при `Publish`, не в
  рантаймі» (`ФВ-9.4`).
* **`SourceEntityDescriptor` → часткове.** Поля звірені з `ext.SourceEntity`
  (`02a` рядок 1342); додано посилання на `ФВ-11.2` («адаптер **не створює
  артефактів у базі джерела**») — `Discover` лише читає.

`CollectionRequest` лишається найслабшим: `SourcePath` виведений із природного
ключа `ext.RawDataPoint (SourceEntityId, SourcePath, Timestamp)`, `MaxPoints` —
із вимоги `TODO` «батчі обмеженого розміру». Обидва поля потрібні механічно,
але їхній набір ніде не зафіксований.

**Усі десять типів перенесені в `02-contracts.md` §5** — у блоки своїх портів,
за конвенцією пакета. Перевірено скриптом: **37 із 37** блоків із рядком-шляхом
`// src/...` збігаються з файлами на диску побайтово, тобто контракт і код
мають одне джерело істини.
**Статус:** RESOLVED · 2026-09-04

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

**Уточнення від 2026-09-04 після повного зонда (`Q-021`).** З 41 файла збірку
блокують лише чотири, і три з них уже закриті мінімальними скелетами
(`Q-020`):

| Файл | Стан |
|---|---|
| `Ecr.Adapters.Excel/StyleMapper.cs` | створено скелет (`Q-020`) |
| `Ecr.Adapters.Excel/ImportDiffBuilder.cs` | створено скелет (`Q-020`) |
| `Ecr.Api/Auth/CurrentUser.cs` | створено скелет (`Q-020`) |
| `Ecr.Expressions/Evaluation/EvaluationResult.cs` | **блокує** — це частина `Q-014` |

Решта **33** збірку не ламають (41 оголошено в дереві − 8 створено:
`StyleMapper`, `ImportDiffBuilder`, `CurrentUser` за `Q-020`; `index.html`
за `Q-023`; `Excel/DependencyInjection.cs`, `JobsHealthCheck`,
`SourcesHealthCheck` за `Q-024`; `EvaluationResult.cs` за `Q-014`):
`UnitOfWork.cs` ніде не типізований (у DI він лише в тексті `TODO`);
`Excel/DependencyInjection.cs` — `AddExcelAdapters()` згадується теж усередині
рядка `TODO`, а не викликається; п'ять `Jobs/*` і `ReportSnapshotBuilder.cs`
ніхто не інстанціює; шість `.sql` потрібні лише на етапі розгортання БД;
`index.html` — лише для `npm run build`; 13 контролерів, два health-checks і
п'ять DTO-файлів — для функціонального зрізу Етапу 1, не для компіляції.

**Що потрібно від людини:** доповнити `05*` секціями для цих файлів.

---

**Закрито 2026-09-04** за вказівкою «вирішуй помилки відповідно ТЗ».
Створено **всі 41**. Дерево `05-skeleton.md` §1 і диск тепер розходяться рівно
в одному місці — `FormulaEngine.cs`, який свідомо переїхав за `Q-013` (A), і
це вже відображено в дереві.

Джерело для кожної групи, щоб нічого не вигадувати:

| Група | Звідки взято |
|---|---|
| **6 `.sql`** | `02a-db-schema.md` §1 і §16 — усі шість **уже написані там** із рядками-локаторами `-- src/…`. Витягнуті дослівно, як контракти. Нічого не складав |
| **13 контролерів** | `02-contracts.md` §9 — таблиця з 57 ендпоінтів: метод, шлях, право, етап. Кожна дія делегує наявному обробнику; логіки в контролерах немає |
| **5 `*Dtos.cs`** | Схема `02a` (склад полів) + ендпоінти §9 (що саме віддається). Це DTO **відповідей** для списків; DTO запитів оголошені поруч зі своїми контролерами, як у наявному `CellsController` |
| **`MethodologyFunctions.cs`** | `02b` §8 — таблиця з 13 функцій діалекту з їхньою семантикою, дослівно |
| **`UnitOfWork.cs`** | Контракт `IUnitOfWork` (два методи) + `D-29` про заборону довгих транзакцій |
| **`ReportSnapshotBuilder.cs`** | Контракт `IReportSnapshotBuilder` (три методи) + `D-52`, `D-65`, `ФВ-0.3` |
| **5 `Jobs/*`** | Контракт `IBackgroundJob` + профільні вимоги: `ФВ-11.3` (збір), `D-66` (партиції), `D-52` (зрізи) |
| **`PiAfCatalogReader.cs`** | `ФВ-11.2` («адаптер не створює артефактів у базі джерела») + `ФВ-16.9` (UOM у каталозі) |

Додатково створено три файли, яких немає ні в дереві, ні в `05*`, але без яких
код не повний — усі внесені в дерево:
`src/Ecr.Api/Controllers/UiStringsController.cs` (ендпоінти `/ui-strings/…` є в
§9, а контролера для них не було), `src/Ecr.Application/DependencyInjection.cs`
і `src/Ecr.Api/Startup/StartupSequence.cs` (`Q-024`).

Усі нові файли — скелети за `05-skeleton.md` §3: повна сигнатура, XML-doc
українською, тіло `NotImplementedException` зі змістовним `TODO`. Жодної
реалізації не написано: це Етап 0.
**Статус:** RESOLVED · 2026-09-04

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

---

### Q-018 · CONFLICT · Етап 0 · 2026-09-04 · **БЛОКЕР**

**Де:** `src/Ecr.Calculations/CalculationOutputWriter.cs`, `ConstantResolver.cs`,
`MethodologyResolver.cs`; `src/Ecr.Adapters.PiAf/CollectionRunner.cs`,
`CatchUpPlanner.cs`
**Контекст:** `dotnet build Ecr.sln` після зняття `Q-013`.

**Суть:**
П'ять файлів із `05f` і `05g` напряму типізовані на класи `Ecr.Infrastructure`,
але `05-skeleton.md` §4 не дозволяє цим проєктам посилатися на інфраструктуру.

**Цитати:**
```
05-skeleton.md §4:
    Ecr.Calculations      → Ecr.Domain, Ecr.Application, Ecr.Expressions
    Ecr.Adapters.PiAf     → Ecr.Domain, Ecr.Application

05f, CalculationOutputWriter.cs:
    public sealed class CalculationOutputWriter(
        Ecr.Infrastructure.Persistence.BulkCellLoader bulk,
        Ecr.Infrastructure.Persistence.EcrDbContext db)
```

**Текст помилки:**
```
src\Ecr.Calculations\CalculationOutputWriter.cs(13,9): error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
src\Ecr.Calculations\CalculationOutputWriter.cs(14,9): error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
src\Ecr.Calculations\ConstantResolver.cs(12,42):       error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
src\Ecr.Calculations\MethodologyResolver.cs(10,45):    error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
src\Ecr.Adapters.PiAf\CollectionRunner.cs(18,9):       error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
src\Ecr.Adapters.PiAf\CatchUpPlanner.cs(11,40):        error CS0234: The type or namespace name 'Infrastructure' does not exist in the namespace 'Ecr'
```

**Що вже пробував:**
1. Додати `using` — не допомагає: проблема не в імпорті, а у відсутньому
   посиланні на проєкт.
2. Тимчасово додати `ProjectReference` на `Ecr.Infrastructure` в обидва
   проєкти (діагностичний зонд, уже прибраний): **обидва зібралися, і за ними
   зібралося все інше** — `Ecr.Api`, `tools/*`, усі `tests/*`. Циклу немає,
   технічно варіант робочий. Але це мовчазна зміна архітектурної межі, тому
   зонд знято.
3. Шукав, чи є для цих залежностей порт у `Ecr.Application.Ports`, який мали
   б використати замість конкретних класів. Для запису результатів такого
   порту **немає**: `IUnitOfWork` і `ICellStore` не покривають
   `SqlBulkCopy` у `calc.CalculationResult`.

**Гіпотези:**
* **A.** Додати `ProjectReference` на `Ecr.Infrastructure` в `Ecr.Calculations`
  і `Ecr.Adapters.PiAf`, а таблицю `05-skeleton.md` §4 виправити.
  Заборонний список (⛔) цього не забороняє; вісім арх-правил `tz/03` §3.3 —
  теж ні (правило 2 стосується лише `Ecr.Application`). **Найдешевше, але
  розмиває межу: `Ecr.Calculations` стає нетестованим без EF Core.**
* **B.** Ввести відсутні порти (щось на кшталт `ICalculationResultWriter`,
  `IMethodologyStore`, `IConstantStore`, `ICollectionStateStore`) у
  `Ecr.Application.Ports`, а реалізації лишити в `Ecr.Infrastructure`.
  Тоді `Ecr.Calculations` і `Ecr.Adapters.PiAf` лишаються чистими.
  **Правильно архітектурно, але це нові контракти — рішення не моє.**
* **C.** Перенести ці п'ять класів в `Ecr.Infrastructure` — так само, як
  вчинили з `FormulaEngine` за `Q-013` A. **Послідовно з уже прийнятим
  рішенням, але `Ecr.Calculations` тоді майже порожніє.**

**Що потрібно від людини:** вибрати A, B або C.

---

**Рішення замовника (2026-09-04): варіант B.**

Уведено чотири порти в `Ecr.Application.Ports`:

| Порт | Хто використовує | Що віддає |
|---|---|---|
| `IMethodologyStore` | `MethodologyResolver` | опубліковані версії, правила, формули, речовини, виходи |
| `IConstantStore` | `ConstantResolver` | **кандидатів** на константу, а не готове значення |
| `ICalculationResultStore` | `CalculationOutputWriter` | резерв Id, пакетний запис результатів і трейсу, інвалідація `rpt.*` |
| `ICollectionStore` | `CollectionRunner`, `CatchUpPlanner` | сутність і джерело, прогін, upsert точок, покриття |

**Принцип розділення:** порт віддає **дані**, логіка лишається в
`Ecr.Calculations` / `Ecr.Adapters.PiAf`. Тому `IConstantStore` повертає всіх
кандидатів: правило «кілька кандидатів на одну дату — помилка конфігурації,
а не привід узяти перший» (ФВ-16.5) неможливо перевірити, якщо сховище вже
вибрало один запис. З тієї самої причини `IMethodologyStore` не «знаходить
чинну версію», а віддає опубліковані: вибір за `EffectiveFrom` — це домен.

Нових DTO майже не з'явилося: порти оперують доменними сутностями
(`MethodologyVersion`, `MethodologyRule`, `MethodologyConstant`, `SourceEntity`,
`DataSource`) — так само, як наявний `IMetadataCache` віддає
`TemplateVersionSnapshot`.

**Змінені конструктори** (єдина зміна в самих класах; тіла і `TODO` збережені,
посилання на `EcrDbContext` у текстах `TODO` замінені на виклики портів):
```
ConstantResolver(EcrDbContext db)                      → ConstantResolver(IConstantStore constants)
MethodologyResolver(EcrDbContext db)                   → MethodologyResolver(IMethodologyStore store)
CalculationOutputWriter(BulkCellLoader, EcrDbContext)  → CalculationOutputWriter(ICalculationResultStore store)
CatchUpPlanner(EcrDbContext db)                        → CatchUpPlanner(ICollectionStore store)
CollectionRunner(..., EcrDbContext db)                 → CollectionRunner(..., ICollectionStore store)
```

**Реалізацій портів не створював.** У дереві `05-skeleton.md` §1 їх немає, а
компіляції вони не потрібні: `Infrastructure/DependencyInjection.cs` — це
`TODO`-рядок, а не код. Реалізації належать Етапам 4 і 5 разом із рештою
`Ecr.Calculations` і `Ecr.Adapters.PiAf`. Це треба врахувати в `07-checkpoints.md`.

**Таблицю `05-skeleton.md` §4 виправляти не довелося** — межа збережена
такою, як написано.

**Уточнення статусу за рев'ю Етапу 0 (В-2).** Замовник обрав **напрям**
(«ввести порти»), а 15 сигнатур у чотирьох портах склав я. Найзмістовніша з
них — `IConstantStore.GetCandidatesAsync`, яка навмисно повертає *кандидатів*,
а не готове значення: від цього залежить, **де** перевіряється ФВ-16.5
(«кілька кандидатів на одну дату — помилка конфігурації, а не привід узяти
перший»). Форма цих портів визначає, які дані доходять до рушія розрахунку,
тож затверджувати її треба нарівні з `Q-014`. У `02-contracts.md` порти
свідомо не перенесені до затвердження.
**Ухвалено 2026-09-04** за тією самою вказівкою. Чотири порти перенесені в
`02-contracts.md` §5 разом із приміткою, навіщо кожен уведений. Форма лишилася
такою, як описано вище: порт віддає **дані**, правила предметної області
лишаються в `Ecr.Calculations` і `Ecr.Adapters.PiAf`.
**Статус:** RESOLVED · 2026-09-04

---

### Q-019 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `tests/Ecr.Infrastructure.Tests/SqlServerCollection.cs` (створено)
**Контекст:** `dotnet build Ecr.sln`.

**Суть:**
Десять тестових класів позначені `[Collection("SqlServer")]` і приймають
`SqlServerFixture` у первинному конструкторі, але визначення колекції в пакеті
немає — ні в `06c`, ні в `06-tests.md`.

**Текст помилки (по одному на кожен із десяти класів):**
```
tests\Ecr.Infrastructure.Tests\Persistence\AuditTests.cs(8,49): error xUnit1041:
Fixture argument 'sql' does not have a fixture source
(if it comes from a collection definition, ensure the definition is in the same assembly as the test)
```

**Що зробив:** створив `SqlServerCollection.cs` у **тій самій збірці**
(`Ecr.Infrastructure.Tests`, як вимагає аналізатор):
```csharp
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }
```
Вміст повністю визначений наявними атрибутами — тут нема чого вигадувати.
Заодно додано відсутній `using Xunit;` у `tests/Ecr.TestKit/SqlServerFixture.cs`
(`IAsyncLifetime` не резолвився).
**Статус:** RESOLVED

---

### Q-020 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Adapters.Excel/StyleMapper.cs`,
`src/Ecr.Adapters.Excel/ImportDiffBuilder.cs`, `src/Ecr.Api/Auth/CurrentUser.cs`
**Контекст:** три з 41 файла `Q-015` блокують збірку.

**Суть:**
Ці три файли оголошені в дереві, не мають вмісту в `05*` і водночас потрібні
для компіляції: `ExcelExporter` приймає `StyleMapper` у конструкторі,
`ExcelImporter` — `ImportDiffBuilder`, `CellsController` — `Ecr.Api.Auth.CurrentUser`.

**Як вирішив** — перевіркою `08-workflow.md` §7: «чи зміниться від мого рішення
число у звіті, форма API або запис у базі?» Ні:

* `StyleMapper`, `ImportDiffBuilder` — **порожні класи** з XML-doc і
  посиланням на `Q-015`. Жодного члена не вигадував: `ExcelExporter` і
  `ExcelImporter` беруть їх лише як залежності й ніде не викликають.
* `CurrentUser` — реалізує `ICurrentUser`, форма якого **повністю задана
  контрактом** (`UserId`, `UserName`, `CorrelationId`, `Language`).
  Усі чотири члени — `NotImplementedException` із змістовним `TODO`, як
  вимагає `05-skeleton.md` §3. Нічого нового не додано.

**Решту 38 файлів не створював.** Зокрема 13 контролерів: їхній вміст — це
форма API, а вона в `02-contracts.md` §9 і в `10-decisions.md`, і вигадувати
її заборонено.
**Статус:** RESOLVED

---

### Q-021 · SCOPE · Етап 0 · 2026-09-04 — перевірка «що буде, коли знімемо блокери»

**Де:** уся збірка
**Контекст:** діагностичний зонд перед зупинкою.

**Суть:**
Щоб не давати дефекти по краплині, я тимчасово підставив заглушки десяти типів
`Q-014` і посилання на `Ecr.Infrastructure` для `Q-018`, зібрав рішення
повністю, зафіксував **усі** решту помилок і зонди прибрав.

**Результат: після зняття `Q-014` і `Q-018` збирається все** —
`Ecr.Domain`, `Ecr.Expressions`, `Ecr.Application`, `Ecr.Infrastructure`,
`Ecr.Calculations`, `Ecr.Adapters.Excel`, `Ecr.Adapters.PiAf`, `Ecr.Api`,
`tools/*` і всі вісім тестових проєктів. Інших прихованих дефектів компіляції
немає.

По дорозі зонд виявив і дав закрити відразу: `Q-019` (визначення колекції
xUnit), `Q-020` (три скелети), доповнення до `Q-006` (ще 12 правил
аналізаторів) і ще вісім відсутніх `using` у `Ecr.Infrastructure`,
`Ecr.Calculations`, `Ecr.Adapters.PiAf` (`EcrDbContext`, `IOrphanScanner`,
`IBackgroundJob`, `IJobProgress`) — усі дописані в межах `Q-011`.

**Статус:** RESOLVED · інформаційний запис

---

### Q-022 · BOOTSTRAP-FIX · Етап 0 · 2026-09-04

**Де:** `tests/Ecr.TestKit/Ecr.TestKit.csproj`
**Контекст:** `dotnet test Ecr.sln --filter "Category!=Integration"`.

**Суть:**
`Ecr.TestKit` — бібліотека фікстур: посилається на `xunit`, але не на
`Microsoft.NET.Test.Sdk`. `dotnet test` на рівні рішення намагається запустити
її як тестову збірку, не знаходить раннера і **перериває весь прогін**, уже
після того, як решта проєктів відпрацювала.

**Текст помилки:**
```
Testhost process for source(s) 'D:\repos\ECR Web\tests\Ecr.TestKit\bin\Debug\net10.0\Ecr.TestKit.dll' exited with error: Error:
Test Run Aborted.
```
(`dotnet test` завершувався з кодом 1, попри те що всі 486 тестів були знайдені
й відпрацювали як очікувано)

**Що зробив:** додав `<IsTestProject>false</IsTestProject>` у `PropertyGroup`.
Це стандартний спосіб виключити допоміжний проєкт із прогону; складу пакетів
і посилань не чіпав.
**Статус:** RESOLVED

---

### Q-023 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Web/index.html`, `src/main.tsx`, `src/api/schema.d.ts`,
`src/api/types.ts`, `src/app/App.tsx`, `src/features/grid/DocumentGrid.tsx`,
`src/api/client.ts`
**Контекст:** `npm run typecheck` і `npm run build` — крок 5 Етапу 0.

**Суть:** фронтенд не збирався з чотирьох різних причин.

**Текст помилок:**
```
src/api/client.ts(1,28):                  error TS2307: Cannot find module './schema'
src/api/client.ts(1,1):                   error TS6133: 'paths' is declared but its value is never read.
src/features/grid/DocumentGrid.tsx(1,36): error TS2307: Cannot find module '@/api/types'
src/features/grid/DocumentGrid.tsx(1,1):  error TS6133: 'TableSliceDto' is declared but its value is never read.
src/features/grid/useCellPatch.ts(1,60):  error TS2307: Cannot find module '@/api/types'
src/app/App.tsx(23,24):                   error TS2503: Cannot find namespace 'JSX'.
src/features/grid/DocumentGrid.tsx(39,58): error TS2503: Cannot find namespace 'JSX'.
```

**Що зробив:**

1. **`index.html`** — оголошений у дереві `05-skeleton.md` §1, вмісту не має
   (Q-015). Створено мінімальний Vite-документ із `#root`.
2. **`src/main.tsx`** — точки входу немає ні в дереві, ні в `05i`, але без неї
   `index.html` нема що завантажувати. Створено стандартне монтування
   `App` через `createRoot` + `StrictMode`, плюс імпорти CSS Mantine.
3. **`src/api/schema.d.ts`** — це **згенерований** файл
   (`npm run api:types` з живого бекенда, а бекенду потрібен SQL Server).
   Створено заглушку з явним попередженням, що генератор її перезапише.
4. **`src/api/types.ts`** — модуля немає ні в дереві, ні в `05i`, але на нього
   посилаються `DocumentGrid.tsx` і `useCellPatch.ts`. Це **дослівний**
   переклад DTO з `02-contracts.md` §10 (`TableSliceDto`, `ColumnDto`, `RowDto`,
   `PatchCellsRequest`, `PatchRow`, `PatchCell`, `PatchCellsResponse`,
   `ValidationMessageDto`, `CellConflictDto`) — нічого не додано і не прибрано.
   У шапці файла записано, що після першої генерації схеми ці типи треба
   замінити посиланнями в неї, інакше вони розійдуться з сервером мовчки.
5. **`JSX.Element`** — під React 19 і `@types/react` 19 глобального namespace
   `JSX` більше немає. Додано `import type { JSX } from 'react';` у два файли.
   Тип не змінено. Це дефект `05i` відносно версій, зафіксованих у
   `04-environment.md` §4.
6. **Мертвий імпорт `TableSliceDto`** у `DocumentGrid.tsx` прибрано: компонент
   за власним `TODO` вантажить зріз сам («завантажити зріз через useTableSlice»),
   у пропси він не входить. У `client.ts` імпорт `paths` натомість збережено і
   зв'язок зі схемою явно виражено як `export type ApiPaths = paths;`.

**Що потрібно від людини:** пункти 2 і 4 — це файли, яких пакет не описує.
Варто внести їх у `05i`, щоб рев'ювер не сприйняв їх як самодіяльність.
**Статус:** RESOLVED

---

### Q-024 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Application/DependencyInjection.cs`,
`src/Ecr.Api/Startup/StartupSequence.cs`,
`src/Ecr.Adapters.Excel/DependencyInjection.cs`,
`src/Ecr.Api/Health/JobsHealthCheck.cs`, `SourcesHealthCheck.cs`
**Контекст:** `dotnet build` — останні п'ять помилок перед зеленою збіркою.

**Суть:**
`Program.cs` (`05h`) викликає п'ять речей, яких у пакеті немає:
`AddExcelAdapters()`, `AddEcrApplication()`, `RunEcrStartupSequenceAsync()`,
`AddCheck<JobsHealthCheck>`, `AddCheck<SourcesHealthCheck>`.

**Текст помилок:**
```
src\Ecr.Api\Program.cs(17,18): error CS1061: 'IServiceCollection' does not contain a definition for 'AddExcelAdapters'
src\Ecr.Api\Program.cs(20,18): error CS1061: 'IServiceCollection' does not contain a definition for 'AddEcrApplication'
src\Ecr.Api\Program.cs(26,30): error CS0234: The type or namespace name 'JobsHealthCheck' does not exist in the namespace 'Ecr.Api.Health'
src\Ecr.Api\Program.cs(27,30): error CS0234: The type or namespace name 'SourcesHealthCheck' does not exist in the namespace 'Ecr.Api.Health'
src\Ecr.Api\Program.cs(35,11): error CS1061: 'WebApplication' does not contain a definition for 'RunEcrStartupSequenceAsync'
```

**Як вирішив** — перевіркою `08-workflow.md` §7: жоден із п'яти не змінює
числа, форму API чи запис у базі. Це реєстрація в контейнері і два
health-checks, чиї ендпоінти `Program.cs` уже оголосив сам.
Усі п'ять — скелети за правилом `05-skeleton.md` §3: повна сигнатура,
XML-doc українською, тіло `NotImplementedException` зі змістовним `TODO`.
Зміст `TODO` не вигаданий, а зібраний із наявних документів:

* `AddExcelAdapters` — за складом `Ecr.Adapters.Excel` і `05g`;
* `AddEcrApplication` — за переліком тек `Ecr.Application`; окремо записано
  заборону реєстрації скануванням збірки (арх-правило 8 `tz/03` §3.3:
  без `Assembly.Load` і `Activator.CreateInstance` за рядком);
* `RunEcrStartupSequenceAsync` — сім кроків **дослівно** з коментаря в
  `Program.cs` і `B01` §6.3, разом із їхнім порядком;
* `JobsHealthCheck`, `SourcesHealthCheck` — за зразком наявного
  `DatabaseHealthCheck` (`05h`); критерії Degraded/Unhealthy взяті з ФВ-11.3
  та ІНТ-3.3.

`Ecr.Api/Startup/` — нова тека: у дереві `05-skeleton.md` §1 її немає.

**Що потрібно від людини:** унести ці п'ять файлів (і теку
`src/Ecr.Api/Startup/`) у `05-skeleton.md` §1 і `05c`/`05g`/`05h`.
**Статус:** RESOLVED

---

### Q-025 · SCOPE · Етап 0 · 2026-09-04 — результат рев'ю Етапу 0

**Де:** весь етап
**Контекст:** рев'ю з чистим контекстом за `07-checkpoints.md` («ПРОМПТИ РЕВ'Ю»).
Універсальний промпт написаний для Етапів 1–6, тому для Етапу 0 пункт 3
інвертовано: `NotImplementedException` і `Assert.Fail` тут — вимога, а не дефект.

**Вердикт рев'ювера: не PASS.** Вісім правок, усі в журналі й документації;
правок у `src/` і `tests/` рев'ю не вимагало.

#### Прийнято і виправлено

| № | Зауваження | Що зроблено |
|---|---|---|
| К-1 | `09-commands.md` і `progress.md` брешуть про інтеграційні тести: «61 знайдено, не запускалися» | Перевірив сам: **119 знайдено, 119 запущено і впало** (`Ecr.Infrastructure.Tests` 116, `Ecr.Application.Tests` 3). Docker для них не потрібен: тіла — `Assert.Fail`, до `SqlServerFixture` виконання не доходить. Причина помилки: я рахував `--list-tests \| grep -c` замість того, щоб запустити. Числа виправлено в обох файлах |
| К-2 | Дев'ять файлів із доданими `using` не описані в `Q-011` | `Q-011` доповнено повним переліком; разом 34 файли |
| К-4 | Мовчазна правка тексту `TODO` в `MethodologyResolver.cs` приховала розбіжність сутності зі схемою | Текст повернуто до `MatchJson`, як у `05f`; відкрито **`Q-026`** |
| В-1 | `Q-005` мав бути `CONTRACT`, а не `BOOTSTRAP-FIX` | Перекваліфіковано, з поясненням, чому це важливо |
| В-2 | `Q-018` стоїть `RESOLVED`, хоча форму 15 сигнатур склав я | Статус → `OPEN`, «форма портів чекає затвердження», нарівні з `Q-014` |
| В-4 | 18 послаблених правил; `CS1574/CS1580/CS1584` глушать зламані `<see cref>` | Знято `CS1574;CS1580;CS1584` — і **це одразу знайшло реальний дефект**: `ISimulationService.cs` посилався на `EditDenyReason.SimulationReadOnly` без `using Ecr.Domain.Enums;`. Виправлено, дописано в `Q-011`. `CA1707` і `xUnit1026` винесені в новий `tests/Directory.Build.props` і на `src/` більше не діють |
| В-6 | Арифметика: «37 файлів без вмісту» і «BOOTSTRAP-FIX: 11» | Перевірив пофайлово: **33** і **10**. Виправлено в `progress.md` і `Q-015` |
| В-7 | Повідомлення коміту `stage-0-verified` каже «тести зелені» | Для Етапу 0 зелений тест був би провалом. Історію не переписував (на неї вже посилається рев'ю); натомість тег `stage-0-reviewed` анотований із правильним формулюванням, а `progress.md` містить фактичні числа |

#### Відхилено після перевірки

**К-3 «`README.md` змінено без запису» — хибне спрацювання.** Перевірив
побайтово: блок із `05a-skeleton-solution.md` і файл на диску **збігаються**.
Рев'ювер, найпевніше, закрив блок на першій вкладеній огорожі ```` ``` ````
(усередині `README.md` є вкладений блок ```` ```bash ````) і порівняв
обрізану версію. Це варто мати на увазі майбутнім рев'ю: у `05a` є рівно один
файл із вкладеною огорожею.

#### Прийнято до відома, рішення за людиною

`В-3` (`Q-014` блокує Етапи 2, 4, 5), `В-5` (`Q-013` і `Q-018` варто
перепідтвердити письмово), `N-1`…`N-4` (порядок `using`, `eslint.config.js`).

#### Що рев'ю підтвердило без зауважень

Скелет відтворено дослівно (255 із 302 секцій побайтово; решта — виключно
додані `using` і зафіксовані записами); контракти не змінені (27 із 34 блоків
`02-contracts.md` побайтово, решта — лише **додавання**, жодного члена не
прибрано); правила залежностей чисті; **жоден тест не втрачено, не послаблено
і не пропущено** — 470 `[Fact]`, 19 `[Theory]`, 70 `[InlineData]` збігаються з
`06a`…`06e` один в один, нуль `Skip`; заборонених пакетів немає; advisory
`GHSA-3w5p-95mh-gq75` перевірено за першоджерелом і обґрунтування `Q-004`
правдиве; реальні дані в репозиторій не потрапили.

**Статус:** RESOLVED

---

### Q-026 · CONTRACT · Етап 0 · 2026-09-04 · **потребує рішення**

**Де:** `src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs`
проти `docs/build/02a-db-schema.md`, `CREATE TABLE calc.MethodologyRule`
**Контекст:** знайдено рев'ю Етапу 0 (К-4).

**Суть:**
Доменна сутність і схема БД описують ту саму таблицю **несумісно**: поле
предиката зветься по-різному, а обов'язкової колонки `Code`, яка входить в
унікальний ключ, у сутності немає взагалі.

**Цитати:**

`02a-db-schema.md` рядок 1069:
```sql
CREATE TABLE calc.MethodologyRule
(
    Id                   int           IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int           NOT NULL,
    Code                 nvarchar(64)  NOT NULL,
    MatchJson            nvarchar(max) NOT NULL,
    Priority             int           NOT NULL CONSTRAINT DF_MR_Prio DEFAULT(100),
    IsActive             bit           NOT NULL CONSTRAINT DF_MR_Act  DEFAULT(1),
    CONSTRAINT PK_MethodologyRule PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyRule UNIQUE (MethodologyVersionId, Code),
    CONSTRAINT FK_MR_Version FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id)
);
```

`05b-skeleton-domain.md` рядок 2321 (створено дослівно):
```csharp
public MethodologyRule(int methodologyVersionId, string conditionExpression, int priority)
{
    MethodologyVersionId = methodologyVersionId;
    ConditionExpression = conditionExpression;
    Priority = priority;
    IsActive = true;
}
...
/// <summary>Умова діалекту методологій; посилається на реєстри й атрибути.</summary>
public string ConditionExpression { get; private set; } = null!;
```

`05f-skeleton-calculations.md` рядки 108 і 152:
```
"2) підібрати методології ПРАВИЛАМИ (MethodologyRule.MatchJson), не жорстким списком;\n" +
"TODO: застосувати MethodologyRule.MatchJson (предикат по колонках рядка) у порядку " +
```

**Розбіжності — дві, і обидві предметні:**

1. **Ім'я поля.** Два джерела з трьох (`02a` і `05f`) кажуть `MatchJson`,
   сутність каже `ConditionExpression`. Це не косметика: `MatchJson` натякає
   на структурований предикат (JSON), `ConditionExpression` — на вираз
   діалекту методологій, який розбирає наш парсер. Це **різні механізми
   зіставлення** і різні місця, де перевіряється матриця покриття (ФВ-13.4).
   Той самий тип `nvarchar(max)` обидва варіанти влаштовує, тому база
   помилки не покаже — розбіжність вилізе як «правило не спрацювало».
2. **Відсутнє поле `Code`.** У схемі воно `NOT NULL` і входить в
   `UQ_MethodologyRule (MethodologyVersionId, Code)`. Сутність його не має,
   тому створити валідний рядок через доменний конструктор **неможливо**.

**Чому не вирішую сам** (`08-workflow.md` §7): і те, і те — запис у базі і
механізм, за яким методологія добирає рядки документа, тобто **числа**.
Вибір між `MatchJson` і `ConditionExpression` — це вибір формату
конфігурації, а не назви змінної.

**Що вже зробив:** повернув текст `TODO` у `MethodologyResolver.cs` до
`MatchJson`, як у `05f`, і додав туди явну позначку `⚠ Q-026`, щоб розбіжність
не загубилася. Сутність і схему **не чіпав**.

**Що потрібно від людини:**
1. Яке ім'я і який формат правильні — `MatchJson` (структурований предикат)
   чи `ConditionExpression` (вираз діалекту методологій)?
2. Чи потрібне полю `Code` місце в сутності (схема вимагає його `NOT NULL`
   і в унікальному ключі)?

---

**Вирішено 2026-09-04 за вказівкою замовника «вирішуй помилки відповідно ТЗ».
Правий `MatchJson`; `Code` додано.** Обидва висновки випливають із ТЗ, а не з
уподобання:

1. **`ConditionExpression` семантично неможливий.** Опис поля казав «умова
   **діалекту методологій**». Але діалект `Methodology` посилань на комірки
   документів **не має взагалі** — `02b-expressions.md` §3.4:
   > Посилання на комірки документів (`[Sheet].[Table]…`) у діалекті
   > `Methodology` **заборонені**: методологія працює з підготовленими
   > аргументами, а не лізе в документ сама.

   А правило має зіставляти саме **рядки документа** (`ФВ-13.3`). Тобто цим
   діалектом умову зіставлення виразити нічим.
2. **Третій діалект під це не створюється.** `D-92` прямо відкидає таку ідею
   в сусідньому випадку (рядковий фільтр у гранті):
   > він вимагав би **третього** діалекту виразів і компіляції в SQL-предикат
   > …, тоді як система будується на двох діалектах і одному парсері (`ФВ-9.5`).
3. **Структурований предикат — наскрізна конвенція пакета.** Те саме завдання
   в інших місцях розв'язане саме так, і там сутність зі схемою **збігається**
   (перевірив): `cfg.TableRelationDef.MatchJson` («як зіставляються рядки»),
   `cfg.CalculationBinding.MatchJson` («як зіставити рядок документа з
   результатом»), `cfg.FormulaDependency.FilterJson` («предикат для
   `RowMode = Dynamic`»). `MethodologyRule` — єдине місце, де узгодженість
   порушена.
4. **`Code` обов'язковий.** Схема має його `NOT NULL` і в
   `UQ_MethodologyRule (MethodologyVersionId, Code)`; без нього доменний
   конструктор не може створити валідний рядок. Плюс `ФВ-13.9` вимагає при
   публікації перевіряти **перетин** правил — а щоб повідомити про конфлікт,
   правило треба назвати.

**Що зроблено:** у `src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs`
`ConditionExpression` → `MatchJson`, додано `Code` (через `EcrCode`), опис
поля виправлено з «умова діалекту методологій» на «структурований предикат;
посилається на реєстри й атрибути» — дослівно за `ФВ-13.8`. Конструктор:
`(int methodologyVersionId, EcrCode code, string matchJson, int priority)`.
Схему `02a` **не чіпав**. Позначку `⚠ Q-026` з `MethodologyResolver.cs` знято
не буде до Етапу 4, коли правило реалізується.

**Це окремий випадок ширшої проблеми — див. `Q-027`.**
**Статус:** RESOLVED · схема права; 2026-09-04

**Додаткова обставина, яку я перевірив.** `MatchJson` є в схемі ще у двох
таблицях — `cfg.TableRelationDef` (рядок 454) і `cfg.CalculationBinding`
(рядок 543), — і в **обох** випадках відповідні сутності
(`TableRelationDef.cs`, `CalculationBinding.cs`) мають поле саме `MatchJson`.
Тобто `MethodologyRule` — єдине місце в пакеті, де узгодженість порушена.
Це схиляє до того, що дефект у сутності, а не в схемі, але вибір усе одно
не мій: `ConditionExpression` може бути свідомим рішенням саме для правил
методологій, де предикат посилається на реєстри й атрибути.


---

### Q-027 · CONFLICT · Етап 0 · 2026-09-04 · **системний дефект пакета**

**Де:** `src/Ecr.Domain/Entities/**` проти `docs/build/02a-db-schema.md`
**Контекст:** після `Q-026` перевірив, чи це поодинокий випадок. Написав скрипт,
який зіставляє властивості **кожної** доменної сутності з колонками однойменної
таблиці схеми (з поправкою на службові поля аудиту і на конвенцію обгортки
значеннєвого типу `XxxValue` ↔ колонка `Xxx`).

**Суть:**
Це **не поодинокий випадок**. З 49 сутностей **22 не збігаються** зі своєю
таблицею — і це вже **після** виправлення `MethodologyRule` за `Q-026`
(до нього було 23). `05b-skeleton-domain.md` і `02a-db-schema.md` писалися незалежно і
розійшлися. Наслідок практичний: модуль 1.3 Етапу 1 («`EcrDbContext`,
конфігурації сутностей») неможливо виконати чесно — конфігурацію нема на що
покласти, а `SchemaValidator` на старті відхилятиме базу.

**Повний перелік (23, із них `MethodologyRule` уже виправлено):**

| Сутність | Таблиця | У СХЕМІ, немає в сутності | У СУТНОСТІ, немає в схемі |
|---|---|---|---|
| `ApprovalRoute` | `wf.ApprovalRoute` | `TemplateVersionId` | — |
| `ApprovalStep` | `wf.ApprovalStep` | `IsOptional` | — |
| `CalculationResult` | `calc.CalculationResult` | `DocumentId`, `SourceRowKey` | `IsCurrent`, `SourceRowId` |
| `CollectionSchedule` | `ext.CollectionSchedule` | `IsEnabled`, `LastRunAt`, `LookbackDays` | `IsActive`, `LookbackMinutes` |
| `DataSource` | `ext.DataSource` | `Catalog`, `MaxParallel`, `SecondaryEndpoint` | — |
| `EntityFieldMap` | `ext.EntityFieldMap` | `TargetColumnDefId`, `TargetKind`, `TargetRegistryFieldDefId`, `TransformCode` | `TargetField` |
| `Methodology` | `calc.Methodology` | `Group` | — |
| `MethodologyConstant` | `calc.MethodologyConstant` | `Category`, `Source` | — |
| `MethodologyFormula` | `calc.MethodologyFormula` | — | `ArgumentsJson` |
| `MethodologyOutput` | `calc.MethodologyOutput` | `Ordinal` | `MethodologyFormulaId` |
| `MethodologyRule` | `calc.MethodologyRule` | `Code`, `MatchJson` | `ConditionExpression` | **← `Q-026`, уже виправлено** |
| `MethodologySubstance` | `calc.MethodologySubstance` | — | `IsActive` |
| `MethodologyVersion` | `calc.MethodologyVersion` | `ContentHash`, `Level`, `Version` | `EffectiveTo`, `LastEditedByUserId`, `VersionNumber` |
| `PasswordPolicy` | `sec.PasswordPolicy` | `ExpirationDays`, `RequireDigit`, `RequireSpecial`, `RequireUpper` | `ExpiryDays`, `HistoryDepth`, `RequireComplexity` |
| `Permission` | `sec.Permission` | `NameL10n` | — |
| `RegistryEntry` | `dic.RegistryEntry` | `DeletedAt`, `DeletedByUserId`, `Ordinal` | — |
| `RegistryEntryLink` | `dic.RegistryEntryLink` | `LeftEntryId`, `LinkKind`, `RightEntryId` | `FromEntryId`, `RegistryRelationDefId`, `ToEntryId` |
| `RegistryExternalKey` | `dic.RegistryExternalKey` | `DataSourceId`, `ExternalPath`, `LastSyncedAt` | `SystemCode` |
| `RegistryValue` | `dic.RegistryValue` | `ValueNumeric`, `ValueRefEntryId`, `ValueUnitId` | `UnitId`, `ValueDecimal`, `ValueRegistryEntryId` |
| `RoleAssignment` | `sec.RoleAssignment` | `ValidFrom`, `ValidTo` | `PrincipalSid` |
| `ScriptVersion` | `calc.ScriptVersion` | `CompiledAt`, `CompilerDiagnostics`, `ContentHash` | `LastTestedAt`, `Status` |
| `SourceEntity` | `ext.SourceEntity` | `Code`, `DisplayName`, `EntityPath`, `RegistryDefId` | `SourcePath`, `TargetRegistryDefId` |
| `SubmissionSnapshot` | `calc.SubmissionSnapshot` | `CalendarMode`, `ContentHash`, `MethodologyVersionsJson`, `NumericMode`, `PayloadJson`, `TemplateVersionId` | `Checksum`, `PayloadCompressed`, `VersionsJson` |

Три класи розбіжностей, і вони різні за наслідками:

1. **Різна назва того самого поля** (`LookbackMinutes`↔`LookbackDays`,
   `ValueDecimal`↔`ValueNumeric`, `Checksum`↔`ContentHash`, `VersionNumber`↔`Version`,
   `ExpiryDays`↔`ExpirationDays`, `TargetRegistryDefId`↔`RegistryDefId`,
   `FromEntryId`/`ToEntryId`↔`LeftEntryId`/`RightEntryId`) — найнебезпечніший клас,
   бо `LookbackMinutes` проти `LookbackDays` **міняє число**: 7 днів проти 7 хвилин.
2. **Поле є в схемі, але сутність його не має** — рядок неможливо створити
   доменним конструктором, якщо колонка `NOT NULL` (як `Code` у `Q-026`).
3. **Поле є в сутності, але не в схемі** — його ніде зберігати
   (`IsCurrent` у `CalculationResult`, `PrincipalSid` у `RoleAssignment`).

**Правило, за яким це вирішується:**
**Правий `02a-db-schema.md`.** Підстава — `08-workflow.md` §7: «зміна контракту:
сигнатури, DTO, коду помилки, **схеми БД**» заборонена, тобто схема є контрактом,
а сутність — реалізацією поверх неї; і `05-skeleton.md` §3, де «схема БД»
названа серед контрактів, а сутності — ні. Виняток допускається там, де сутність
свідомо не зберігає поле (обчислюване), — але тоді це має бути видно з коду.

**Чому не роблю все зараз:** сутності належать різним етапам
(`07-checkpoints.md`): метадані й документи — Етап 1, безпека — Етап 3,
розрахунки — Етап 4, зовнішні джерела — Етап 5. Правити їх усі на Етапі 0
означало б вийти за SCOPE (`08-workflow.md` §6) і зробити це наосліп, без
тестів, які перевіряють кожне поле. `MethodologyRule` виправлено раніше — як
пряме продовження `Q-026`, знайденого рев'ю.

**План:** кожна сутність приводиться до схеми **у своєму етапі**, разом із
конфігурацією EF, яка це і перевіряє. Для Етапу 1 це модуль 1.3 і сутності
`cfg.*`/`doc.*`; решта — у 3, 4 і 5. Кожна правка — окремим рядком у `progress.md`.

**Статус:** OPEN · правило визначене, виконання рознесене за етапами

---

### Q-028 · SCOPE · Етап 0–1 · 2026-09-04 — повний аудит узгодженості

**Де:** увесь репозиторій
**Контекст:** наскрізна перевірка на вимогу замовника: «щоб усе узгоджувалось,
компілювалось і не було імпакту на наступні модулі». Кожен пункт перевірений
скриптом або запуском, а не переглядом.

#### Що перевірено і результат

| # | Перевірка | Результат |
|---|---|---|
| 1 | Чиста збірка з нуля, `Debug` | **0 errors**, 937 попереджень |
| 2 | Чиста збірка з нуля, `Release` | **0 errors** |
| 3 | Класи попереджень | 13 категорій, **усі** з `WarningsNotAsErrors`; жодного несподіваного класу. `CS1574/1580/1584` серед них немає — отже зламаних `<see cref>` у коді немає |
| 4 | Тести, `Category!=Integration` | 486: 27 passed, 459 failed, **0 skipped** |
| 5 | Тести, `Category=Integration` | 119: 0 passed, 119 failed, 0 skipped |
| 6 | Frontend | `typecheck` чисто, `vitest` 23 failed, `vite build` OK |
| 7 | **Цілісність тестів** | 96 файлів; **імена методів збігаються з `06*` один в один**, нічого не втрачено і не перейменовано; `[Fact]` 470, `[Theory]` 19, `[InlineData]` 73 — як у docs; `Skip` 0, `Ignore` 0; `Assert.Fail` **474** (було 489, замінено рівно 15 — стільки методів у чотирьох файлах модуля 1.1) |
| 8 | Скелет проти `05*`/`06*` | 302 секції: **251 збігається побайтово**, 50 відрізняються, 1 відсутній |
| 9 | Кожна з 50 розбіжностей | має запис у журналі: `Q-002`, `Q-004`…`Q-008`, `Q-011`, `Q-018`, `Q-022`, `Q-023`, `Q-024`, `Q-026` і чотири файли модуля 1.1 Етапу 1. **Незаписаних немає** |
| 10 | Відсутній файл | `src/Ecr.Expressions/FormulaEngine.cs` — свідомий переїзд за `Q-013` A; у дереві вже відображено |
| 11 | Контракт проти коду | **45 із 45** блоків із рядком-локатором збігаються побайтово: `02-contracts.md` 37/37, `02a` 6/6, `02b` 2/2 |
| 12 | Дерево `05-skeleton.md` §1 проти диска | 253 файли, **0 відсутніх** |
| 13 | `Ecr.Domain` | **жодного** `PackageReference` (єдине входження слова — у коментарі-забороні) |
| 14 | `Ecr.Application` | жодного `using Microsoft.EntityFrameworkCore`, `Ecr.Infrastructure`, `Ecr.Adapters` |
| 15 | Будь-що → `Ecr.Api` | немає |
| 16 | Контрактні типи | **жодного** `NotImplementedException` у `ValueObjects`, `Enums`, `Ports`, DTO — як вимагає `05-skeleton.md` §3 |
| 17 | Дублікати імен публічних типів | лише `DependencyInjection` — по одному на збірку, у різних просторах імен, методи розширення різні; неоднозначності немає |
| 18 | `tests/Directory.Build.props` | успадковує корінь (`net10.0`, `TreatWarningsAsErrors=true`) і додає рівно `CA1707;xUnit1026`; `CS1574` у тестах теж лишається помилкою |

#### Головний висновок про вплив на наступні модулі

**`Q-027` не блокує Етап 1.** Розподіл 22 розбіжних сутностей за схемами:

| Схема | Сутностей | Етап, на якому виправляється |
|---|---:|---|
| `calc` | 8 | Етап 4 |
| `dic` | 4 | Етап 4 |
| `ext` | 4 | Етап 5 |
| `sec` | 3 | Етап 3 |
| `wf` | 2 | Етап 3 |
| `arc` | 1 | Етап 5 (дзеркало архіву) |
| **`cfg` і `doc`** | **0** | — |

Сутності `cfg.*` і `doc.*` — рівно ті, які конфігурує модуль 1.3, — **усі
збігаються зі схемою**. Отже `EcrDbContext` і конфігурації Етапу 1 можна писати
без жодного попереднього рішення.

**Форма типів `Q-014` і портів `Q-018` нічим не зафіксована в тестах наступних
етапів** — перевірено `grep` по всіх 96 тестових файлах і по описах у
`06b`/`06d`: жодної згадки. Це двобічний висновок: конфлікту немає, але й
перевірити правильність форми до Етапів 2, 4 і 5 нічим. Чотири типи, позначені
як здогадка (`CollectionRequest` найперше), лишаються ризиком саме там.

#### Знайдено і виправлено під час аудиту

1. **`02b-expressions.md`: застарілий локатор.** Блок вказував
   `// src/Ecr.Expressions/ParseResult.cs`, тоді як файл після `Q-009` живе в
   `Parsing/ParseResult.cs`. Локатор і тіло синхронізовано з диском — після
   цього 45 із 45.
2. **`Q-027`: неузгоджене число.** У тексті «22», у таблиці 23 рядки. Причина:
   таблиця містить `MethodologyRule`, уже виправлений за `Q-026`. Формулювання
   уточнено.

#### Знайдено, **не** виправлено — потребує рішення

**`01-filegroups.sql` не можна виконати як є.** У чотирьох рядках стоїть
плейсхолдер `<path>`:
```sql
ALTER DATABASE [Ecr] ADD FILE
  (NAME = N'Ecr_hot', FILENAME = N'<path>\Ecr_hot.ndf', SIZE = 4GB, FILEGROWTH = 1GB) TO FILEGROUP [DATA_HOT];
```
`05-skeleton.md` §3 вимагає, щоб SQL були «повністю, робочі», тож формально це
дефект. Але шлях до файлів — рішення розгортання, різне для DEV, UAT і PROD, і
вигадувати його я не маю права. Файл лишено **побайтово таким, як у `02a`** —
інакше зламалася б щойно встановлена рівність контракту і коду.

**Вирішено — див. `Q-029`.** Замовник указав, що база має задаватися рядком
підключення. Плейсхолдер прибрано: назва бази тепер береться з підключення
(`DB_NAME()`), каталог даних визначається автоматично з властивостей інстансу.

**Статус:** RESOLVED · аудит виконано, усі знахідки закриті

---

### Q-029 · DECIDED · Етап 0 · 2026-09-04

**Де:** `src/Ecr.Infrastructure/Persistence/Sql/01-filegroups.sql`,
`06-rcsi.sql` і відповідні блоки `02a-db-schema.md` §1
**Контекст:** аудит `Q-028` показав, що `01-filegroups.sql` не виконується як є —
у чотирьох рядках стоїть плейсхолдер `<path>`. Вказівка замовника: «шлях до бд
має бути вказаний як коннекшнстрінг і налаштовуватись в веб».

**Суть:**
У вказівці змішані **дві різні речі**, і одну з них треба було розділити перед
тим, як щось робити.

**Що вже було саме так.** Підключення застосунку до БД — виключно рядок
підключення. Перевірено:
```
appsettings.json:                  "ConnectionStrings": { "Ecr": "" }   ← порожньо, секрет у файл не пишеться (D-11, ФВ-6.11)
04-environment.md §6:              ECR_ConnectionStrings__Ecr
grep по src/ на Server=, .mdf, .ndf, C:\ :  жодного жорсткого шляху
```
Єдине входження — fallback у `EcrDbContextFactory` для `dotnet ef migrations`;
це design-time інструмент, не рантайм.

**Чого рядком підключення зробити не можна.** `<path>` — це
`FILENAME = N'…\Ecr_hot.ndf'` в `ALTER DATABASE … ADD FILE`: шлях у файловій
системі **хоста SQL Server**, куди фізично лягають файли даних. Рядок
підключення каже, *куди підключитися*, а не *де на диску сервера створити
файл*. Виразити друге через перше неможливо.

**Чому і у вебі це налаштувати не можна** — дві причини з ТЗ:
1. `D-66`: застосунок не має DDL-прав, скрипт виконує SQL Agent під окремим
   principal. Кнопка у вебі налаштовувала б те, що застосунок ніколи не має
   права виконати.
2. Порядок старту (`B01` §6.3, крок 1 — «дочекатися БД»): файлові групи
   створюються **до** того, як база існує і застосунок уперше стартує.
   Веб-форма для цього потребувала б бази, якої ще немає.

**Що зробив — прибрав хардкод зовсім, обома способами, які тут доречні:**

1. **Назва бази більше не зашита.** Було `ALTER DATABASE [Ecr] …` у восьми
   місцях двох скриптів — стало `DB_NAME()` через `QUOTENAME`. Базу задає
   **підключення, з яким запущено скрипт**: `09-commands.md` §3 уже викликає
   їх як `sqlcmd -S localhost -d Ecr -E -i …`. Тобто назва бази живе рівно в
   одному місці — там само, де й у рядку підключення застосунку. Це і є те,
   про що йшлося у вказівці, у формі, яка тут технічно можлива.
2. **Каталог даних визначається сам** —
   `SERVERPROPERTY('InstanceDefaultDataPath')`, тобто типовий каталог даних
   інстансу. За звичайного розгортання редагувати не треба **нічого**:
   скрипт виконується як є на DEV, UAT і PROD і кладе файли туди, куди
   інстанс кладе свої. Якщо каталог визначити не вдалося — `THROW 50020` зі
   зрозумілим текстом, а не крипт��чна помилка SQL.
3. **Окремий `@ArchivePath`** із типовим значенням «той самий». Це єдиний
   випадок, коли шлях справді треба задати руками: сенс окремої файлової
   групи `DATA_ARCHIVE` у тому, щоб архів лежав на дешевшому носії, і цього
   з властивостей інстансу не вивести.
4. **Скрипти стали ідемпотентними.** Було: повторний запуск падав на
   «filegroup already exists». Стало: `IF NOT EXISTS` по `sys.filegroups`,
   `sys.database_files` і `sys.databases`. Для скрипта, який DBA запускає
   вручну, це не косметика.
5. Розміри і приріст файлів збережені точно як у `02a` (4/4/4/2 ГБ,
   приріст 1/4/2/1 ГБ).

**Що НЕ змінював:** рядок підключення, `appsettings.json`, спосіб читання
конфігурації — там уже все правильно. І не додавав жодної веб-форми для
шляху: див. дві причини вище.

**Перевірка:** плейсхолдерів `<path>` у скриптах не лишилося; жорстких
`DATABASE [Ecr]` теж; `02a` і файли на диску збігаються побайтово (6 із 6).

**Що варто зробити людині:** дописати в runbook рядок про `@ArchivePath` —
коли архівна файлова група має жити на окремому носії, це єдине місце, де
шлях задається явно.
**Статус:** RESOLVED · 2026-09-04
