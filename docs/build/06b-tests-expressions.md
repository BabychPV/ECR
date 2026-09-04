# 06b — Тести: `Ecr.Expressions.Tests`

> Частина [`06-tests.md`](06-tests.md). Семантика — [`02b`](02b-expressions.md),
> очікувані числа — [`02c`](02c-fixtures.md).
>
> **Це найважливіший набір тестів у системі.** Розбіжність зі старими числами
> народжується саме тут: у семантиці `null`, у режимі округлення і в
> календарній конвенції. Помилка в моделі даних видно одразу; помилка тут дає
> правдоподібні числа, які виявляють на звірці через місяць.

---

### `tests/Ecr.Expressions.Tests/Ecr.Expressions.Tests.csproj`
MODULE: tests-expressions | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Expressions.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Expressions\Ecr.Expressions.csproj" />
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

### `tests/Ecr.Expressions.Tests/Parsing/OperatorPrecedenceTests.cs`
MODULE: tests-expressions | STAGE: 2
CONTRACT: 02b-expressions.md#precedence

```csharp
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Пріоритети операторів за таблицею 02b §2.</summary>
public sealed class OperatorPrecedenceTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("-2 ^ 2", -4)]          // унарний мінус слабший за степінь
    [InlineData("2 * 3 % 4", 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Арифметика_обчислюється_за_пріоритетами(string expr, int expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Степінь_правоасоціативний_2_у_3_у_2_дорівнює_512()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_слабша_за_додавання()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_слабше_за_конкатенацію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AND_сильніший_за_OR()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Одинарне_і_подвійне_дорівнює_це_синоніми_порівняння()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Evaluation/NullSemanticsTests.cs`
MODULE: tests-expressions | STAGE: 2
CONTRACT: 02b-expressions.md#null

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// **Два різні правила щодо `null`, які не можна плутати** (02b §6):
/// в агрегатах він поглинається, у бінарних операторах — поширюється.
/// Саме тут народжуються розбіжності зі старою системою.
/// </summary>
public sealed class NullSemanticsTests
{
    // ——— Поглинання в агрегатах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_ігнорує_null_елементи()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_порожньої_множини_дорівнює_нулю_а_не_null()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_не_рахує_null_ані_в_сумі_ані_в_дільнику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_порожньої_множини_дорівнює_null_а_не_нулю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void PRODUCT_порожньої_множини_дорівнює_одиниці()
        => Assert.Fail("not implemented");

    // ——— Поширення в бінарних операторах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Додавання_до_null_дає_null()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Множення_null_на_нуль_дає_null_а_НЕ_нуль()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_трактує_null_як_порожній_рядок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_двох_null_дає_TRUE()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_null_з_числом_дає_null_а_не_FALSE()
        => Assert.Fail("not implemented");

    // ——— Порожня комірка проти явної порожнечі ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсутня_комірка_бере_DefaultValue_колонки()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Явна_порожнеча_ігнорує_DefaultValue_і_дає_null()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Evaluation/ErrorSemanticsTests.cs`
MODULE: tests-expressions | STAGE: 2

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Помилки — **значення**, а не винятки: одна зіпсована комірка не має валити
/// перерахунок усієї таблиці (02b §6.4).
/// </summary>
public sealed class ErrorSemanticsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_помилку_а_не_виняток()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_null_дає_помилку_ділення_на_нуль()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помилка_поширюється_через_арифметику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_перехоплює_помилку()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_НЕ_перехоплює_null_бо_це_не_помилка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірка_з_помилкою_зберігається_видимою_а_не_як_порожня()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Functions/RoundingTests.cs`
MODULE: tests-expressions | STAGE: 2

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Режим округлення. **Банківське округлення дало б інші числа у звіті**,
/// тому скрізь <c>MidpointRounding.AwayFromZero</c> (02b §7, 02c E12–E13).
/// </summary>
public sealed class RoundingTests
{
    [Theory]
    [InlineData("2.5", 0, "3")]
    [InlineData("3.5", 0, "4")]     // банківське дало б 4 — збіг, тому потрібен наступний випадок
    [InlineData("0.5", 0, "1")]     // банківське дало б 0 — тут різниця видна
    [InlineData("-2.5", 0, "-3")]
    [InlineData("1.2345", 2, "1.23")]
    [InlineData("1.2350", 2, "1.24")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROUND_округлює_від_нуля_а_не_до_парного(string value, int digits, string expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обчислення_ведеться_в_decimal_і_не_втрачає_точності()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Binding/RangeExpansionTests.cs`
MODULE: tests-expressions | STAGE: 2
CONTRACT: 02b-expressions.md#ranges

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Діапазони розкриваються **при публікації**, а не в рантаймі.
/// </summary>
/// <remarks>
/// Тест <c>Зміна_Ordinal_після_публікації_не_змінює_результат</c> — головний
/// у цьому файлі. Якби діапазон обчислювався за <c>Ordinal</c> у рантаймі,
/// презентаційна правка мовчки змінювала б числа: даних не зіпсовано,
/// а результат інший — найгірший клас помилок.
/// </remarks>
public sealed class RangeExpansionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_розкривається_у_список_ключів_за_Ordinal()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зміна_Ordinal_після_публікації_НЕ_змінює_результат_формули()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Розкритий_діапазон_зберігається_із_порядковим_номером_кожного_рядка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Неіснуюча_межа_діапазону_дає_ECR_TMPL_4222_при_публікації()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Видалені_рядки_не_потрапляють_у_розкритий_діапазон()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Для_динамічної_таблиці_діапазон_записується_предикатом_а_не_переліком()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкретний_RowKey_для_динамічної_таблиці_відхиляється()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Graph/TopologicalSorterTests.cs`
MODULE: tests-expressions | STAGE: 2

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Graph;

/// <summary>
/// Цикл — помилка **публікації**, а не тихо неправильне число в проді
/// (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Залежності_обчислюються_раніше_за_залежні_формули()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_із_двох_формул_виявляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_із_трьох_формул_виявляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_містить_шлях_циклу_а_не_лише_прапорець()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Самопосилання_формули_це_цикл()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/Binding/UnitCheckerTests.cs`
MODULE: tests-expressions | STAGE: 4
CONTRACT: 02b-expressions.md#convert

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Перевірка одиниць при публікації — **головна цінність механізму одиниць**:
/// помилка ловиться до продуктиву, а не на звірці через місяць (ФВ-16.7).
/// </summary>
public sealed class UnitCheckerTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_величин_у_різних_одиницях_відхиляється_з_ECR_TMPL_4223()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_після_явного_CONVERT_проходить()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Ділення_маси_на_час_дає_похідну_одиницю_масової_витрати()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результат_несумісний_з_оголошеною_одиницею_колонки_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Агрегація_колонки_з_одиницею_на_рядок_без_приведення_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Неявної_конверсії_не_відбувається_навіть_коли_вона_очевидна()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/PeriodContextTests.cs`
MODULE: tests-expressions | STAGE: 4
CONTRACT: 02c-fixtures.md#calc

```csharp
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Календарна конвенція (D-78).
/// </summary>
/// <remarks>
/// Різниця між режимами на тих самих даних — **3.3 %** (фікстура 02c §7), і
/// виглядає вона як помилка формули, а не як різниця конвенції. Тому тест на
/// обидва режими обов'язковий.
/// </remarks>
public sealed class PeriodContextTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Actual_дає_фактичну_кількість_днів_у_січні()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed360_дає_тридцять_днів_у_будь_якому_місяці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_дає_рік_у_365_днів_навіть_у_високосному()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Секунди_періоду_це_дні_помножені_на_86400()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перерахунок_у_грами_за_секунду_відрізняється_між_Actual_і_Fixed360()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/GoldenFixtureTests.cs`
MODULE: tests-expressions | STAGE: 2
CONTRACT: 02c-fixtures.md#expected

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Обчислення формул фікстури і звірка з **очікуваними числами з
/// `water-demo.json`**.
/// </summary>
/// <remarks>
/// Очікування беруться з файлу, а не з коду тесту. Якщо код дає інше — правий
/// файл (`08-workflow.md` §4). Підганяти очікування під результат коду
/// заборонено: саме так «зелені» тести перестають щось означати.
/// </remarks>
public sealed class GoldenFixtureTests
{
    [Theory]
    [InlineData("7001001", "3650.750")]
    [InlineData("7001002", "1750.750")]   // Feb — комірки НЕМАЄ
    [InlineData("7001003", "4100.500")]   // Mar — ЯВНА порожнеча
    [InlineData("7001101", "1380.500")]
    [InlineData("7001102", "930.000")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F1_сума_місяців_у_рядку(string rowKey, string expected)
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData("Jan", "4750.625")]
    [InlineData("Feb", "4020.500")]
    [InlineData("Mar", "3041.375")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F2_баланс_підсумовує_два_діапазони(string column, string expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F3_крос_аркушний_rollup_поверхневих_вод()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядок_C009_збігається_з_рядком_7009000_бо_це_два_шляхи_до_того_самого_числа()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F6_конверсія_кубометрів_у_тонни_через_щільність()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F7_одиниця_на_рядок_приводить_тонни_і_кілограми_до_спільної_одиниці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F9_крос_період_у_першому_періоді_дає_null_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F10_предикатний_діапазон_підсумовує_лише_рядки_за_умовою()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/EdgeCaseTests.cs`
MODULE: tests-expressions | STAGE: 2
CONTRACT: 02c-fixtures.md#edge

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Двадцять чотири крайові випадки з <c>02c §8</c>. Кожен — окремий тест;
/// назва відповідає ідентифікатору випадку.
/// </summary>
public sealed class EdgeCaseTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E01_SUM_порожньої_множини_дорівнює_нулю() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E02_AVERAGE_порожньої_множини_дорівнює_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E03_null_помножений_на_нуль_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E04_ділення_на_нуль_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E05_ділення_на_null_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E06_помилка_поширюється() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E07_IFERROR_перехоплює_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E08_IFERROR_не_перехоплює_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E09_конкатенація_з_null_дає_другий_операнд() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E10_порівняння_з_null_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E11_рівність_null_дає_TRUE() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E12_округлення_двох_з_половиною_дає_три() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E13_округлення_мінус_двох_з_половиною_дає_мінус_три() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E14_конверсія_різних_розмірностей_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E15_конверсія_градусів_у_Кельвіни_дає_273_15() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E16_крос_період_за_межу_проєкту_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E17_цикл_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E18_порівняння_числа_з_текстом_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E19_зміна_Ordinal_не_змінює_результат_діапазону() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void E20_дублікат_RowKey_відхиляється() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E21_агрегація_різних_одиниць_без_CONVERT_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E22_відсутня_комірка_бере_DefaultValue() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E23_явна_порожнеча_ігнорує_DefaultValue() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E24_нуль_ціла_один_плюс_нуль_ціла_два_дорівнює_рівно_нуль_ціла_три()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Expressions.Tests/ClientServerEquivalenceTests.cs`
MODULE: tests-expressions | STAGE: 2

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Тест еквівалентності клієнт/сервер.
/// </summary>
/// <remarks>
/// Клієнтський обчислювач (<c>formulajs</c>) — **лише підказка** під час
/// введення; збережене значення завжди рахує сервер (D-20). Але якщо підказка
/// систематично розходиться з результатом, користувач перестає їй вірити —
/// і саме тому набір спільних випадків має збігатися.
///
/// Реалізація: набір виразів і очікувань зберігається у спільному JSON, який
/// читають і цей тест, і vitest-тест на клієнті (див. `06e`).
/// </remarks>
public sealed class ClientServerEquivalenceTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спільний_набір_виразів_дає_однакові_результати_на_сервері()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Набір_покриває_усі_функції_діалекту_шаблонів()
        => Assert.Fail("not implemented");
}
```

---

## Решта тестів виразів

| Файл | STAGE | Що доводить |
|---|---:|---|
| `Lexing/LexerTests.cs` | 2 | цифри на початку допустимі лише всередині `[...]`; подвоєння `''`; інваріантний формат чисел |
| `Parsing/ReferenceParsingTests.cs` | 2 | чотири скорочені форми посилання; `{Month}`; `[Period:±N]` |
| `Parsing/DialectTests.cs` | 2 | `@Arg`/`CST.`/`!F` заборонені в `Template`; посилання на комірки заборонені в `Methodology` |
| `Binding/TypeCheckerTests.cs` | 2 | `Number + Text` відхиляється; `Date − Date` дає число; `Boolean` в арифметиці відхиляється |
| `Functions/AggregateFunctionTests.cs` | 2 | поведінка всіх 11 функцій на порожній множині і з `null` |
| `Functions/MethodologyFunctionTests.cs` | 4 | 13 додаткових функцій; `SWITCH` без збігу і без default дає `null` |
| `Evaluation/DynamicPredicateTests.cs` | 2 | предикат обчислюється в рантаймі; заборонені конструкції відхиляються при публікації |

Усі сім наведені **повністю** нижче.

---

## Решта тестів виразів — повний текст

### `tests/Ecr.Expressions.Tests/Lexing/LexerTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Lexing/LexerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Lexing;

/// <summary>
/// Лексер. Тут вирішується, чи буде `1.5` числом на будь-якій машині: формат
/// **інваріантний**, кома десятковим роздільником не є ніколи.
/// </summary>
public sealed class LexerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ідентифікатор_не_починається_з_цифри_поза_дужками()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цифровий_RowKey_допустимий_усередині_квадратних_дужок()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Подвоєний_апостроф_дає_один_символ_у_рядку()
        => Assert.Fail("not implemented");

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1.5")]
    [InlineData("0.001")]
    [InlineData("1000000")]
    public void Число_читається_в_інваріантному_форматі(string literal)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кома_не_є_десятковим_роздільником_у_жодній_культурі()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Parsing/ReferenceParsingTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Parsing/ReferenceParsingTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Чотири скорочені форми посилання, плейсхолдер місяця і крос-період.</summary>
public sealed class ReferenceParsingTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[Jan]")]
    [InlineData("[7001001].[Jan]")]
    [InlineData("[Main].[7001001].[Jan]")]
    [InlineData("[Water_07].[Main].[7001001].[Jan]")]
    public void Чотири_форми_посилання_розбираються(string expression)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Плейсхолдер_Month_підставляє_колонку_місяця()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_період_розбирається_з_від_ємним_і_додатним_зсувом()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вихід_за_межі_проєкту_дає_null_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_рядків_розбирається_у_список_ключів()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Parsing/DialectTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Parsing/DialectTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Два діалекти, один парсер (ФВ-9.5). Різниця — у **дозволених** посиланнях
/// і функціях, а не в граматиці.
/// </summary>
/// <remarks>
/// Третього діалекту немає: рядковий фільтр гранта виведено з обсягу (D-92)
/// саме тому, що вимагав би окремої граматики і компіляції в SQL-предикат.
/// </remarks>
public sealed class DialectTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("@Arg")]
    [InlineData("CST.DENSITY")]
    [InlineData("!OtherFormula")]
    public void Конструкції_методологій_заборонені_в_діалекті_шаблонів(string token)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_комірки_заборонені_в_діалекті_методологій()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функція_поза_набором_діалекту_дає_ECR_TMPL_0422()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функції_поточного_часу_заборонені_в_обох_діалектах()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Binding/TypeCheckerTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Binding/TypeCheckerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Типізація при публікації. Мета — щоб несумісність типів була **помилкою
/// публікації**, а не дивним числом у звіті через місяць.
/// </summary>
public sealed class TypeCheckerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_плюс_текст_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різниця_дат_дає_число()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Boolean_в_арифметиці_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_числа_з_текстом_дає_помилку_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Несумісні_одиниці_без_CONVERT_дають_ECR_TMPL_4223()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Functions/AggregateFunctionTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Functions/AggregateFunctionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Одинадцять функцій діалекту шаблонів на **порожній множині** і з `null`.
/// </summary>
/// <remarks>
/// В агрегатах `null` **поглинається**, у бінарних операціях —
/// **поширюється** (`02b-expressions.md` §6). Це різні правила навмисно:
/// сума трьох місяців, де
/// один порожній, має дорівнювати сумі двох; а `A + B` з невідомим `B`
/// невідоме, і мовчазний нуль тут був би неправильним числом.
/// </remarks>
public sealed class AggregateFunctionTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("COUNT")]
    public void Агрегат_на_порожній_множині_має_визначений_результат(string function)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поглинається_в_агрегатах()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поширюється_в_бінарних_операціях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_діагностику_а_не_виняток()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVG_ігнорує_null_а_не_рахує_його_нулем()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Functions/MethodologyFunctionTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Functions/MethodologyFunctionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>Додаткові функції діалекту методологій.</summary>
public sealed class MethodologyFunctionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_без_збігу_і_без_default_дає_null()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_повертає_перший_збіг_а_не_останній()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_між_різними_розмірностями_дає_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_однакових_одиниць_не_змінює_значення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void PeriodContext_дає_тривалість_за_CalendarMode_методології()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_і_Actual_дають_різні_числа_у_високосний_рік()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Expressions.Tests/Evaluation/DynamicPredicateTests.cs`

```csharp
// tests/Ecr.Expressions.Tests/Evaluation/DynamicPredicateTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Предикатні діапазони для динамічних таблиць. На відміну від звичайних
/// діапазонів, які матеріалізуються при публікації (ФВ-2.9), предикат
/// **обчислюється в рантаймі** — рядків на момент публікації ще немає.
/// </summary>
public sealed class DynamicPredicateTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_обчислюється_в_рантаймі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_на_порожній_таблиці_дає_порожню_множину()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_у_предикаті_відхиляється_при_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_періодне_посилання_у_предикаті_відхиляється()
        => Assert.Fail("not implemented");
}
```
