# 06a — Тести: `Ecr.Domain.Tests`

> Частина [`06-tests.md`](06-tests.md). Тіло кожного тесту на Етапі 0 —
> `Assert.Fail("not implemented")`. Тест має **запускатися і падати**.

---

### `tests/Ecr.Domain.Tests/Ecr.Domain.Tests.csproj`
MODULE: tests-domain | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Domain.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.TestKit\Ecr.TestKit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

</Project>
```

---

### `tests/Ecr.Domain.Tests/ValueObjects/PeriodKeyTests.cs`
MODULE: tests-domain | STAGE: 1
CONTRACT: 02-contracts.md#value-objects

```csharp
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="PeriodKey"/> = <c>Year * 100 + Sequence</c> (R-A6).
/// Збіг із <c>YYYYMM</c> існує **лише для місячних періодів**, і саме тому
/// виводити місяць арифметикою заборонено.
/// </summary>
public sealed class PeriodKeyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Місячний_період_дає_ключ_у_форматі_YYYYMM()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Квартальний_період_дає_ключ_YYYY01_до_YYYY04_а_не_місяць()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Річний_період_дає_послідовність_1()
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData(1899, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 100)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_поза_допустимими_межами_кидає_виняток(int year, int sequence)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Діапазон_року_для_квартального_періоду_охоплює_чотири_ключі_а_не_дванадцять()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/ValueObjects/EcrCodeTests.cs`
MODULE: tests-domain | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Обмеження на <see cref="EcrCode"/> продиктоване лексером виразів: код
/// вживається всередині <c>[...]</c> без екранування (R-B6).
/// </summary>
public sealed class EcrCodeTests
{
    [Theory]
    [InlineData("Water_07")]
    [InlineData("Main")]
    [InlineData("A")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Допустимий_код_приймається(string code) => Assert.Fail("not implemented");

    [Theory]
    [InlineData("7001001")]      // не може починатися з цифри
    [InlineData("Water 07")]     // пробіл зламає лексер
    [InlineData("Water.07")]     // крапка — роздільник у посиланні
    [InlineData("Water]07")]     // дужка закриє посилання
    [InlineData("")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Недопустимий_код_відхиляється(string code) => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Код_довший_за_64_символи_відхиляється() => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/ValueObjects/RowKeyTests.cs`
MODULE: tests-domain | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="RowKey"/> — не <see cref="EcrCode"/>: він допускає цифрові ключі
/// (<c>"7001001"</c>) і GUID, бо стоїть в окремій позиції граматики.
/// </summary>
public sealed class RowKeyTests
{
    [Theory]
    [InlineData("7001001")]
    [InlineData("C009")]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    [InlineData("row-1.2")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Допустимий_ключ_приймається(string key) => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_динамічного_рядка_це_GUID_у_форматі_N_без_дефісів()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Два_виклики_NewDynamic_дають_різні_ключі() => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/ValueObjects/CellValueDataTests.cs`
MODULE: tests-domain | STAGE: 1

```csharp
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Три різні стани комірки, які **не можна зводити один до одного** (R-B4):
/// «не заповнювали», «заповнили порожнім», «є значення».
/// </summary>
public sealed class CellValueDataTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_рівно_одним_значенням_вважається_коректним()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_двома_значеннями_одночасно_вважається_некоректним()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_не_має_жодного_значення()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутність_комірки_це_різні_стани()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Configuration/TemplateVersionTests.cs`
MODULE: tests-domain | STAGE: 1
CONTRACT: 02a-db-schema.md#cfg

```csharp
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Інваріанти публікації — найважливіші в системі (ФВ-7.1, ФВ-7.2).
/// </summary>
public sealed class TemplateVersionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нова_версія_створюється_у_стані_Draft_з_нульовою_ревізією()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публікація_фіксує_автора_і_момент()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторна_публікація_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Опублікована_версія_структурно_заморожена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Структурна_зміна_опублікованої_версії_кидає_ECR_TMPL_0409()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_кешу_містить_і_версію_і_ревізію_презентації()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Презентаційна_ревізія_приймається_лише_як_наступна_за_поточною()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Units/UnitConverterTests.cs`
MODULE: tests-domain | STAGE: 4
CONTRACT: 02b-expressions.md#convert

```csharp
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Units;

/// <summary>
/// Маршрут конверсії з чотирьох кроків і, головне, **відмова** при різних
/// розмірностях: це те, що не дає щільності стати «конверсією» (ФВ-16.3, 16.5).
/// </summary>
public sealed class UnitConverterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_в_ту_саму_одиницю_повертає_значення_без_змін()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_у_кілограми_множаться_на_тисячу()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Явна_конверсія_має_пріоритет_над_маршрутом_через_базову_одиницю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_градусів_Цельсія_у_Кельвіни_враховує_зсув()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_між_різними_розмірностями_кидає_ECR_UOM_0422()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_кубометрів_у_кілограми_неможлива_бо_це_контекстний_коефіцієнт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_не_втрачає_точності_на_decimal()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Documents/PeriodStateCalculatorTests.cs`
MODULE: tests-domain | STAGE: 3

```csharp
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Переходи станів періоду і, окремо, **пояс майданчика**: межі рахуються не
/// в UTC, інакше «останній день періоду» настає для користувача в інший час
/// (D-68).
/// </summary>
public sealed class PeriodStateCalculatorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void До_дати_відкриття_період_у_стані_Scheduled()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Усередині_періоду_стан_Open()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_завершення_періоду_і_до_HardClose_стан_Grace()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_HardClose_стан_Closed()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_повертає_період_у_Grace_до_вказаного_моменту()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Межі_рахуються_у_поясі_майданчика_а_не_в_UTC()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Опівночі_за_поясом_майданчика_період_ще_відкритий_хоча_в_UTC_уже_наступна_доба()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_у_режимі_Auto_це_найраніший_Open()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_відкритих_немає_поточним_стає_найпізніший_Grace()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_немає_ні_Open_ні_Grace_поточного_періоду_немає()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Documents/ProjectCurrentPeriodTests.cs`
MODULE: tests-domain | STAGE: 3

```csharp
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// <c>Project.CurrentPeriod</c> — **наша конфігурація** (D-77), а не значення
/// з зовнішньої системи.
/// </summary>
public sealed class ProjectCurrentPeriodTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Новий_проєкт_має_режим_Auto()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Фіксація_періоду_без_причини_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_режимі_Pinned_автоматичне_оновлення_не_змінює_поточний_період()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зняття_фіксації_повертає_режим_Auto()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Configuration/ChangeClassifierTests.cs`
MODULE: tests-domain | STAGE: 1

```csharp
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Чотири класи змін (ФВ-7.4). Від класу залежить, чи дозволена зміна взагалі:
/// <c>Breaking</c> у версії з документами — **відмова операції**, а не
/// попередження.
/// </summary>
public sealed class ChangeClassifierTests
{
    [Theory]
    [InlineData("HeaderL10n")]
    [InlineData("Ordinal")]
    [InlineData("DisplayFormat")]
    [InlineData("IsHidden")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Презентаційні_поля_класифікуються_як_Presentation(string field)
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData("DataType")]
    [InlineData("Precision")]
    [InlineData("UnitId")]
    [InlineData("LookupRegistryDefId")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_типу_точності_або_одиниці_класифікується_як_Guarded(string field)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_коду_колонки_за_наявності_документів_це_Breaking()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Додавання_нової_колонки_це_Safe()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Configuration/ColumnDefValidationTests.cs`
MODULE: tests-domain | STAGE: 1

```csharp
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Структурна валідація значення проти опису колонки.</summary>
public sealed class ColumnDefValidationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Текст_у_числовій_колонці_відхиляється_з_ECR_CELL_0422()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожнє_значення_в_обовязковій_колонці_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Lookup_колонка_вимагає_посилання_на_запис_реєстру_а_не_текст()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Колонка_типу_Unit_зберігає_посилання_на_одиницю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Запис_в_обчислену_колонку_відхиляється_з_ECR_CELL_4221()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Число_з_більшою_кількістю_знаків_ніж_Scale_відхиляється()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Domain.Tests/Configuration/PeriodAccessRuleTests.cs`
MODULE: tests-domain | STAGE: 3

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Правила доступу до періоду — заміна кнопки <c>Protect</c> (ФВ-2.15).
/// Фікстура задає аркуш `Waste_08` доступним лише в періодах 1–3.
/// </summary>
public sealed class PeriodAccessRuleTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(12, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Правило_діє_лише_для_періодів_у_заданому_діапазоні(byte sequence, bool applies)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Правило_без_меж_діє_для_всіх_періодів()
        => Assert.Fail("not implemented");
}
```

---

## Решта тестів домену

| Файл | STAGE | Що доводить |
|---|---:|---|
| `Configuration/RegistryDefTests.cs` | 4 | інкремент `DataRevision`; перемикання `SourceKind` |
| `Configuration/FormulaDefTests.cs` | 2 | `EvaluationOrder` встановлюється лише при `Publish` |
| `Dictionaries/RegistryEntryTests.cs` | 4 | темпоральність: `IsValidOn` для дат усередині і поза інтервалом |
| `Documents/TableRowTests.cs` | 1 | `Touch` піднімає `ModifiedAt`; soft delete не видаляє |
| `Documents/CellValueTests.cs` | 1 | `Apply` замінює значення; `ToData` віддає рівно те, що записано |
| `Calculations/MethodologyVersionTests.cs` | 4 | публікація автором останньої правки відхиляється (`ECR-CALC-0409`); публікація без `ChangeReason` і `EffectiveFrom` неможлива |
| `Security/ResourceGrantTests.cs` | 3 | `IsDeny` виграє над будь-яким рівнем |
| `Workflow/ApprovalStateTests.cs` | 3 | `Reopen` без причини відхиляється; перехід `Approved → Draft` лише через `Reopen` |

Усі вісім наведені **повністю** нижче.

---

## Решта тестів домену — повний текст

### `tests/Ecr.Domain.Tests/Configuration/RegistryDefTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Configuration/RegistryDefTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Визначення довідника: версійність даних і зміна master-джерела.</summary>
public sealed class RegistryDefTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Зміна_запису_інкрементує_DataRevision()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void DataRevision_не_змінюється_від_читання()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перемикання_SourceKind_змінює_master()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Configuration/FormulaDefTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Configuration/FormulaDefTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Порядок обчислення формул — **обчислюваний**, не введений (ФВ-9.4).
/// Дозволити задати його руками означало б, що додана формула тихо зміщує
/// решту.
/// </summary>
public sealed class FormulaDefTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void EvaluationOrder_встановлюється_лише_при_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спроба_задати_EvaluationOrder_ззовні_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Токен_поза_оголошеним_списком_аргументів_дає_помилку_публікації()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Темпоральність запису довідника (ФВ-8.5). Межі вікна **включні** з обох
/// боків — саме на цьому найлегше помилитися на день.
/// </summary>
public sealed class RegistryEntryTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("2026-01-01", true)]   // рівно ValidFrom
    [InlineData("2026-06-30", true)]   // рівно ValidTo
    [InlineData("2025-12-31", false)]  // день до
    [InlineData("2026-07-01", false)]  // день після
    public void IsValidOn_включає_обидві_межі(string date, bool expected)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidFrom_означає_чинність_від_початку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidTo_означає_чинність_без_обмеження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SoftDelete_не_видаляє_запис_фізично()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Documents/TableRowTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Documents/TableRowTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Рядок таблиці. Головне тут — <c>ModifiedAt</c>: без його підняття
/// <c>RowVersion</c> не змінюється, і оптимістичне блокування **тихо не
/// працює** (B04 §2.4).
/// </summary>
public sealed class TableRowTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Touch_піднімає_ModifiedAt()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_комірки_рядка_піднімає_ModifiedAt_рядка()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Soft_delete_лишає_рядок_у_сховищі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void RowKey_динамічного_рядка_є_GUID_у_форматі_N()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Documents/CellValueTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Documents/CellValueTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Комірка. Три стани розрізняються завжди: **є значення** / **явна
/// порожнеча** (`IsEmpty`) / **комірки немає** (ФВ-3.8, R-B4).
/// </summary>
/// <remarks>
/// Формула трактує другий і третій однаково, але аудит і експорт — ні. Злиття
/// цих станів здається спрощенням рівно доти, доки не треба довести, що
/// користувач свідомо лишив клітинку порожньою.
/// </remarks>
public sealed class CellValueTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Apply_замінює_значення_і_тип()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ToData_віддає_рівно_те_що_записано()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутня_комірка_це_різні_стани()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ValueUnitId_приймається_лише_для_DataType_Unit()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнення_двох_типізованих_колонок_одночасно_відхиляється()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Calculations/MethodologyVersionTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Calculations/MethodologyVersionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Публікація версії методології — найнебезпечніша операція в системі, бо
/// змінює вже подані числа. Тому правило чотирьох очей перевіряється
/// **системно** (D-40), а не інструкцією.
/// </summary>
public sealed class MethodologyVersionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_автором_останньої_правки_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_ChangeReason_неможлива()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_зеленого_тесту_неможлива()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вікна_дії_опублікованих_версій_не_перетинаються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Опублікована_версія_не_редагується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void NumericMode_за_замовчуванням_Legacy()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Security/ResourceGrantTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Security/ResourceGrantTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>
/// `IsDeny` **виграє завжди**, на будь-якому рівні успадкування (ФВ-6.6).
/// </summary>
/// <remarks>
/// Це свідома жорсткість. Альтернатива «конкретніший рівень перемагає» дає
/// ситуації, де людина має доступ і ніхто не може пояснити чому — а пояснити
/// доступ важливіше, ніж зробити його гнучким.
/// </remarks>
public sealed class ResourceGrantTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Deny_на_проєкті_перекриває_Manage_на_аркуші()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Deny_на_колонці_перекриває_Write_на_таблиці()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Рівні_упорядковані_від_None_до_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_успадковується_від_проєкту_до_колонки()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Domain.Tests/Workflow/ApprovalStateTests.cs`

```csharp
// tests/Ecr.Domain.Tests/Workflow/ApprovalStateTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// Стан робочого процесу на аркуш × період — **єдине джерело істини** про
/// статус (D-38, D-93).
/// </summary>
public sealed class ApprovalStateTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_без_причини_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перехід_Approved_у_Draft_можливий_лише_через_Reopen()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_із_Draft_відхиляється_бо_нема_чого_відкривати()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Стан_одного_аркуша_не_зачіпає_інші_аркуші_періоду()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reject_вимагає_коментаря()
        => Assert.Fail("not implemented");
}
```
