# 06c — Тести: `Ecr.Infrastructure.Tests`

> Частина [`06-tests.md`](06-tests.md).
>
> **Майже всі тести цього файлу — інтеграційні**: вони перевіряють саме те,
> чого немає в SQLite — партиціонування, складені FK, `TRUNCATE … WITH
> (PARTITIONS)`, RCSI, тригери, `SEQUENCE`. Тому вони позначені
> `[Trait("Category","Integration")]` і не входять у прогін за замовчуванням
> (`04-environment.md` §5).
>
> Це **не пропуск тестів**: вони запускаються окремою командою і є частиною
> Definition of Done Етапів 1 і 5.

---

### `tests/Ecr.Infrastructure.Tests/Ecr.Infrastructure.Tests.csproj`
MODULE: tests-infrastructure | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Infrastructure.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Infrastructure\Ecr.Infrastructure.csproj" />
    <ProjectReference Include="..\Ecr.TestKit\Ecr.TestKit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

</Project>
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/PhysicalModelTests.cs`
MODULE: tests-infrastructure | STAGE: 1
CONTRACT: 02a-db-schema.md#doc

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перевіряє фізичну модель на **реальному** SQL Server.
/// </summary>
/// <remarks>
/// Ці інваріанти неможливо перевірити на SQLite, а помилка в них виявляється
/// не при написанні коду, а на 108 млн рядків — коли міняти вже дорого.
/// </remarks>
[Collection("SqlServer")]
public sealed class PhysicalModelTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Первинний_ключ_CellValue_починається_з_партиційного_стовпця()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void CellValue_не_має_сурогатного_Id()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Усі_унікальні_індекси_партиційованих_таблиць_містять_PeriodKey()
        => Assert.Fail("not implemented");

    // ⚠ Обидва тести додані після Q-035: міграція EF кладе партиційовані
    // таблиці на PRIMARY, бо ON ps_ByPeriodKey(PeriodKey) вона виставити не
    // вміє. Без цих двох перевірок випадіння 07-partition-tables.sql із
    // розгортання не помітив би НІХТО — архівація «працює» і не звільняє
    // нічого, без жодної помилки.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Партиційовані_таблиці_лежать_на_схемі_партиціонування()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Кластерний_індекс_CellValue_стиснений_сторінково()
        => Assert.Fail("not implemented");

    // ⚠ Тест доданий після Q-060: сім сутностей без конфігурації EF лягали
    // конвенцією в `dbo` з множинним іменем, і міграція створювала таблиці,
    // яких у `02a-db-schema.md` немає. `SchemaValidator` цього не бачить —
    // він звіряє список міграцій, а не форму схеми. Перевірка потрібна саме
    // на розгорнутій базі: вона ловить і модель EF, і `.sql`-скрипти разом.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Міграція_не_створює_таблиць_поза_контрактними_схемами()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Складений_FK_не_дає_записати_комірку_в_колонку_чужої_таблиці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void FK_на_рядок_складений_із_PeriodKey_і_TableRowId()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запит_за_один_період_читає_рівно_одну_партицію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Явна_порожнеча_без_значень_проходить_CHECK_а_з_значенням_ні()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Конверсію_між_різними_розмірностями_неможливо_вставити_в_таблицю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Немає_жодної_колонки_типу_float_у_результатних_таблицях()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/SequenceTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>Id</c> береться з <c>SEQUENCE</c>, а не <c>IDENTITY</c>: значення
/// потрібні **до** вставки, щоб завантажити рядки і комірки одним проходом
/// <c>SqlBulkCopy</c> (B02 §2.3).
/// </summary>
[Collection("SqlServer")]
public sealed class SequenceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Резервування_діапазону_повертає_безперервні_ідентифікатори()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Паралельні_резервування_не_перетинаються()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Один_виклик_на_батч_а_не_на_рядок()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/ConcurrencyTests.cs`
MODULE: tests-infrastructure | STAGE: 1
CONTRACT: 02-contracts.md#dto

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Оптимістичне блокування.
/// </summary>
/// <remarks>
/// Ключовий тест — <c>Зміна_комірки_піднімає_RowVersion_рядка</c>.
/// <c>rowversion</c> на <c>doc.TableRow</c> **не змінюється сам**, коли ми
/// пишемо в <c>doc.CellValue</c>; забути про «дотик» рядка = зламати
/// блокування **тихо**: конфлікти перестануть виявлятися, і користувачі
/// почнуть непомітно затирати роботу один одного (B04 §2.4).
/// </remarks>
[Collection("SqlServer")]
public sealed class ConcurrencyTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_комірки_піднімає_RowVersion_рядка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запис_зі_застарілою_версією_рядка_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Конфлікт_в_одному_рядку_відхиляє_весь_батч()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Відповідь_на_конфлікт_містить_чуже_значення_автора_і_момент_зміни()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Два_користувачі_в_різних_рядках_не_конфліктують()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Структурна_зміна_таблиці_ловиться_через_If_Match_на_TableInstance()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/CellStoreTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Три операції над коміркою і те, що порожні не матеріалізуються.</summary>
[Collection("SqlServer")]
public sealed class CellStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запис_значення_створює_рядок_комірки()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Стирання_видаляє_рядок_комірки_а_не_обнуляє_його()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Явна_порожнеча_лишає_рядок_із_прапорцем_IsEmpty()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Незаповнені_комірки_не_створюються_і_не_повертаються()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Читання_зрізу_виконує_один_запит_а_не_запит_на_рядок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Масове_завантаження_вантажить_рядки_і_комірки_одним_проходом()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/TriggerTests.cs`
MODULE: tests-infrastructure | STAGE: 1
CONTRACT: 02a-db-schema.md#triggers

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Незмінність опублікованої версії забезпечується **на рівні БД**, а не лише
/// кодом: тригер має спрацювати навіть на прямому <c>UPDATE</c> повз застосунок.
/// </summary>
/// <remarks>
/// Окремо перевіряється сумісність із EF Core: без
/// <c>.ToTable(t =&gt; t.HasTrigger(...))</c> <c>SaveChanges</c> падає в
/// рантаймі через <c>OUTPUT</c>-клаузу (ТЗ §13.5 п.1) — помилку легко
/// пропустити до першого запису в проді.
/// </remarks>
[Collection("SqlServer")]
public sealed class TriggerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_типу_колонки_опублікованої_версії_відхиляється_тригером()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_підпису_колонки_опублікованої_версії_дозволена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_Ordinal_опублікованої_версії_дозволена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_RowKey_опублікованої_версії_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Видалення_формули_опублікованої_версії_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void SaveChanges_на_таблиці_з_тригером_не_падає_бо_оголошено_HasTrigger()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміни_у_чернетці_тригер_не_блокує()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/PresentationRevisionTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Інкремент ревізії — **одним statement із `OUTPUT`** (R-B7).
/// Read-modify-write у застосунку заборонений, бо інстансів ≥2 і дві
/// презентаційні правки одночасно дали б однакову ревізію.
/// </summary>
[Collection("SqlServer")]
public sealed class PresentationRevisionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Інкремент_повертає_нове_значення_одним_запитом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Паралельні_інкременти_дають_різні_значення()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Ключ_кешу_змінюється_після_презентаційної_правки()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/AuditTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Аудит: пакетність, незмінність, партиціонування по <c>ChangedAt</c>.</summary>
[Collection("SqlServer")]
public sealed class AuditTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Аудит_ста_комірок_пишеться_одним_запитом_а_не_ста()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Автором_зміни_є_UserId_а_не_SID()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_у_стані_Grace_позначається_як_пізня()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Аудит_партиціонується_за_моментом_зміни_а_не_за_звітним_періодом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_за_січень_у_березні_потрапляє_в_березневу_партицію_аудиту()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Jobs/ArchiveJobTests.cs`
MODULE: tests-infrastructure | STAGE: 5
CONTRACT: 02a-db-schema.md#archive-proc

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Архівація року.
/// </summary>
/// <remarks>
/// Головне правило процедури: **дані з джерела не видаляються, поки контрольні
/// суми не збіглися**. Тест на обрив посередині перевіряє саме це — і саме він
/// відрізняє відновлювану операцію від такої, що при збої лишає систему в
/// напівстані.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveJobTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Успішна_архівація_переносить_усі_рядки_і_звільняє_партиції()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Розбіжність_контрольних_сум_зупиняє_процес()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void При_розбіжності_дані_джерела_лишаються_на_місці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Обрив_посередині_дозволяє_продовжити_з_наступної_партиції()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Під_час_архівації_читання_бере_джерело_за_станом_а_не_за_датою()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Розархівація_повертає_рядки_без_втрат()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Читання_архівного_року_прозоре_для_викликача()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Діапазон_партицій_для_квартального_проєкту_охоплює_чотири_а_не_дванадцять()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Startup/SchemaValidatorTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Startup;

/// <summary>
/// Перевірки при старті. Мета — **впасти зрозуміло**, а не працювати на
/// несумісному середовищі й з'ясувати це на першому записі (ФВ-7.9).
/// </summary>
[Collection("SqlServer")]
public sealed class SchemaValidatorTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Незастосована_міграція_у_режимі_Validate_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Міграція_у_БД_якої_немає_у_збірці_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Відсутність_схеми_партиціонування_зупиняє_старт_із_інструкцією()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Вимкнений_RCSI_дає_критичний_стан_здоровя_але_не_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Standard_старший_за_2016_SP1_приймається()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Standard_до_2016_SP1_зупиняє_старт_бо_модель_архівації_не_працює()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Developer_Edition_розпізнається_як_Enterprise()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явний_режим_Standard_перекриває_автовизначення()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Persistence/SeedTests.cs`
MODULE: tests-infrastructure | STAGE: 1
CONTRACT: 02a-db-schema.md#seed

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Seed: ідемпотентність і повнота.</summary>
[Collection("SqlServer")]
public sealed class SeedTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Повторний_запуск_не_створює_дублікатів()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Створюються_три_мови_і_рівно_одна_за_замовчуванням()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Створюються_усі_права_з_каталогу()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Небезпечні_права_не_потрапляють_у_вбудовані_ролі_автоматично()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Кожна_розмірність_має_рівно_одну_базову_одиницю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Похідні_одиниці_посилаються_на_чисельник_і_знаменник()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Множники_базових_одиниць_відповідають_фікстурі()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Infrastructure.Tests/Caching/MetadataCacheTests.cs`
MODULE: tests-infrastructure | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна
/// правка створює новий ключ, а не псує старий (D-16).
/// </summary>
[Collection("SqlServer")]
public sealed class MetadataCacheTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Повторне_читання_не_звертається_до_БД()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Після_презентаційної_правки_повертається_новий_знімок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Старий_знімок_лишається_валідним_для_старого_ключа()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Знімок_містить_індекси_колонок_і_рядків_для_швидкого_доступу()
        => Assert.Fail("not implemented");
}
```

---

## Решта тестів інфраструктури

| Файл | STAGE | Що доводить |
|---|---:|---|
| `Security/PasswordHasherTests.cs` | 3 | різні солі; перевірка сталого часу; `NeedsRehash` при зміні параметрів |
| `Security/AccessProfileCacheTests.cs` | 3 | зміна `SecurityStamp` дає інший ключ; профіль будується раз |
| `Jobs/PeriodStateJobTests.cs` | 3 | переходи в поясі майданчика; `Pinned` не перезаписується |
| `Jobs/PartitionCheckJobTests.cs` | 5 | попередження за нестачі запасу партицій; DDL **не виконується** застосунком |
| `Jobs/ConsistencyCheckJobTests.cs` | 5 | виявляє осиротілі комірки, порушені FK у гібридному режимі, розбіжності архіву |
| `Persistence/UnitOfWorkTests.cs` | 1 | транзакція коротка; вкладені транзакції заборонені |
| `Reporting/ReportSnapshotBuilderTests.cs` | 5 | статус зрізу успадковується від даних; `IsCurrent` перемикається однією транзакцією |

Усі сім наведені **повністю** нижче.

---

## Решта тестів інфраструктури — повний текст

### `tests/Ecr.Infrastructure.Tests/Security/PasswordHasherTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Security/PasswordHasherTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>Хешування паролів локальних облікових записів (ФВ-6.5).</summary>
public sealed class PasswordHasherTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Однаковий_пароль_дає_різні_хеші_через_різні_солі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перевірка_виконується_за_сталий_час()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void NeedsRehash_істинний_після_зміни_параметрів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_потрапляє_в_текст_винятку()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Security/AccessProfileCacheTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Security/AccessProfileCacheTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Кеш профілю доступу. Профіль будується **раз на сесію** (ФВ-6.10), але
/// відкликання прав діє **негайно** — через зміну `SecurityStamp`, яка дає
/// інший ключ кешу.
/// </summary>
public sealed class AccessProfileCacheTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_будується_один_раз_на_сесію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_SecurityStamp_дає_інший_ключ_кешу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відкликання_ролі_діє_негайно_а_не_після_закінчення_cookie()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_симуляції_не_потрапляє_в_кеш()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Переходи станів періоду. Рахуються в **поясі майданчика**, не за
/// UTC-опівніччю (D-68): період, що закривається «31 числа о 23:59», має
/// закритися о 23:59 там, де сидять люди.
/// </summary>
public sealed class PeriodStateJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Переходи_рахуються_в_поясі_майданчика()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Pinned_поточний_період_не_перезаписується_задачею()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Стан_є_збереженим_значенням_а_не_функцією_від_now()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_запуск_не_змінює_вже_переведені_періоди()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Одночасний_Reopen_серіалізується_а_не_губиться()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Jobs/PartitionCheckJobTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Jobs/PartitionCheckJobTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Перевірка запасу партицій. Задача **алертить**, а `SPLIT` робить SQL Agent
/// (D-66): обліковий запис застосунку не має DDL-прав у PROD.
/// </summary>
public sealed class PartitionCheckJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Нестача_запасу_партицій_дає_попередження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Задача_не_виконує_DDL()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Достатній_запас_не_породжує_шуму()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Jobs/ConsistencyCheckJobTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Jobs/ConsistencyCheckJobTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Нічна перевірка інваріантів (ФВ-7.7). **Знахідка — баг, а не шум**: якщо
/// перевірка регулярно щось знаходить і це вважають нормою, вона перестає
/// працювати як сигнал.
/// </summary>
public sealed class ConsistencyCheckJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Виявляє_осиротілі_комірки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Виявляє_порушені_FK_у_гібридному_режимі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Звіряє_архів_із_джерелом_за_контрольними_сумами()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ставить_і_знімає_IsOrphaned_в_обидва_боки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Не_чіпає_закриті_періоди()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): під RCSI вони
/// роздувають version store, і пік «останнього дня періоду» стає збоєм.
/// </summary>
public sealed class UnitOfWorkTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Вкладені_транзакції_заборонені()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Масовий_імпорт_іде_батчами_з_окремим_commit()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Відкат_повертає_стан_повністю()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuilderTests.cs`

```csharp
// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuilderTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізів для звітності. У `rpt.*` потрапляють **усі** зрізи,
/// включно з `Draft` — щоб числа можна було перевірити **до** затвердження
/// (D-65). Фільтр для регулятора стоїть у вʼюсі, не в RDL (ФВ-10.11).
/// </summary>
public sealed class ReportSnapshotBuilderTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Статус_зрізу_успадковується_від_стану_даних()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Чернеткові_зрізи_теж_потрапляють_у_rpt()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вʼюха_для_регулятора_віддає_лише_Approved_і_Submitted()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void IsCurrent_перемикається_однією_транзакцією()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Поданий_зріз_не_перебудовується_ніколи()
        => Assert.Fail("not implemented");
}
```
