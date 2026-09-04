# 06d — Тести: застосунок, розрахунки, API

> Частина [`06-tests.md`](06-tests.md). Три проєкти:
> `Ecr.Application.Tests`, `Ecr.Calculations.Tests`, `Ecr.Api.Tests`.

---

### `tests/Ecr.Application.Tests/Ecr.Application.Tests.csproj`
MODULE: tests-application | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Application.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Application\Ecr.Application.csproj" />
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

> `Ecr.Calculations.Tests.csproj` і `Ecr.Api.Tests.csproj` — за тим самим
> зразком; у `Api.Tests` додається `Microsoft.AspNetCore.Mvc.Testing`.

---

### `tests/Ecr.Application.Tests/Security/AccessDecisionTests.cs`
MODULE: tests-application | STAGE: 3
CONTRACT: 02c-fixtures.md#access

```csharp
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Дванадцять сценаріїв доступу з фікстури <c>02c §6</c> плюс три додаткові
/// з <c>tz/07</c> §7.6.
/// </summary>
/// <remarks>
/// Найважливіший тут — <c>A7</c>: **закритий період блокує запис усім,
/// включно з найвищим грантом**. Якщо він проходить — модель доступу зламана,
/// і жоден інший тест цього не покаже.
/// </remarks>
public sealed class AccessDecisionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A01_оператор_із_грантом_Write_редагує_відкритий_період()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A02_локальний_користувач_має_ті_самі_права_що_й_доменний()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A03_обчислена_колонка_недоступна_на_запис_із_причиною_CalculatedCell()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A04_переглядач_без_гранта_Write_отримує_причину_NoGrant()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A05_у_стані_Grace_запис_дозволений_і_позначається_як_пізній()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A06_закритий_період_блокує_запис_оператору()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A07_закритий_період_блокує_запис_НАВІТЬ_власнику_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A08_аркуш_поза_вікном_доступу_дає_причину_OutOfAccessWindow()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A09_поданий_документ_блокує_запис_навіть_у_відкритому_періоді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A10_заборона_на_одну_колонку_не_блокує_решту()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A11_подання_потребує_рівня_Submit_а_не_Write()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A12_затвердження_потребує_рівня_Approve_і_стану_Submitted()
        => Assert.Fail("not implemented");

    // ——— Додаткові з tz/07 §7.6 ———

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Заборона_виграє_над_дозволом_на_будь_якому_рівні_успадкування()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_на_колонку_перекриває_грант_на_таблицю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_проєкту_НЕ_впливає_на_рішення_про_доступ()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_будується_раз_а_не_на_кожну_комірку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пакетна_перевірка_зрізу_дає_ті_самі_рішення_що_й_поштучна()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відмова_повертає_ПРИЧИНУ_а_не_просто_заборону()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Application.Tests/Documents/PatchCellsTests.cs`
MODULE: tests-application | STAGE: 1
CONTRACT: 02-contracts.md#dto

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Пакетна зміна комірок — найгарячіший шлях запису.</summary>
public sealed class PatchCellsTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Значення_записується_і_повертається_нова_версія_рядка()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Рядок_без_базової_версії_трактується_як_створення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_рядка_з_наявним_ключем_відхиляється_ECR_ROW_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Конфлікт_в_одному_рядку_відхиляє_весь_батч_із_переліком_конфліктів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заборонена_комірка_відхиляє_батч_із_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірковий_Error_валідації_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядковий_Error_валідації_НЕ_блокує_запис_а_повертається_у_відповіді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Попередження_не_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Відсутнє_поле_в_запиті_не_змінює_комірку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Після_запису_залежні_комірки_ставляться_в_чергу_перерахунку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Перерахунок_ставиться_в_чергу_ПОЗА_транзакцією_запису()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Application.Tests/Templates/PublishTemplateVersionTests.cs`
MODULE: tests-application | STAGE: 1
CONTRACT: 02b-expressions.md#publish-checks

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Дванадцять перевірок публікації (02b §12).
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється **з переліком усіх
/// проблем**. Зупинка на першій помилці змусила б користувача виправляти їх
/// по одній, повторюючи публікацію десятки разів.
/// </remarks>
public sealed class PublishTemplateVersionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Коректна_версія_публікується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Синтаксична_помилка_у_виразі_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_неіснуючу_колонку_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_у_графі_відхиляє_публікацію_із_шляхом_циклу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Несумісні_одиниці_без_CONVERT_відхиляють_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відповідь_містить_УСІ_проблеми_а_не_лише_першу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Публікація_зберігає_розкриті_діапазони_і_порядок_обчислення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публікація_записує_подію_в_аудит()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Невдала_публікація_не_лишає_часткових_змін()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Application.Tests/Templates/PatchPresentationTests.cs`
MODULE: tests-application | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Презентаційна правка «на льоту» — те, заради чого існує
/// <c>PresentationRevision</c> (ФВ-7.2).
/// </summary>
public sealed class PatchPresentationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_підпису_опублікованої_версії_проходить()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_типу_даних_відхиляється_з_ECR_TMPL_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Змішаний_патч_із_однією_структурною_зміною_відхиляється_повністю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Успішний_патч_інкрементує_ревізію_і_записує_аудит()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_Ordinal_не_впливає_на_результати_формул_із_діапазонами()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Application.Tests/Workflow/SubmitApproveTests.cs`
MODULE: tests-application | STAGE: 3

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Робочий процес із гранулярністю **аркуш × період** (D-38) і правилом
/// «поданий документ не редагується» (D-67).
/// </summary>
public sealed class SubmitApproveTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_аркуша_за_період_не_зачіпає_інші_аркуші()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_з_незакритими_помилками_валідації_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_створює_іммутабельний_зріз_із_версіями_і_режимами()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поданий_аркуш_не_редагується_навіть_у_стані_Grace()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_документа_повертає_аркуш_у_Draft_із_обовязковою_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_документа_при_закритому_періоді_відхиляється_ECR_PRD_4223()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_Reopen_зміни_позначаються_як_пізні()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Старий_поданий_зріз_лишається_після_повторного_подання()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Application.Tests/Registries/RegistryResolverTests.cs`
MODULE: tests-application | STAGE: 4
CONTRACT: 02c-fixtures.md#registries

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Темпоральний резолвінг «станом на дату **періоду**», а не «на сьогодні» —
/// різниця стає видимою, коли звіт за минулий рік перераховують цього року.
/// </summary>
public sealed class RegistryResolverTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_чинний_на_дату_періоду_потрапляє_у_список()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_втратив_чинність_до_періоду_не_потрапляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_набуде_чинності_пізніше_не_потрапляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Резолвінг_робиться_на_дату_періоду_а_не_на_поточну_дату()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каскад_звужує_список_водних_обєктів_за_обраним_дозволом()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Запис_на_який_посилаються_дані_не_видаляється_ECR_REG_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Зміна_записів_інкрементує_ревізію_даних_реєстру()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перемикання_master_у_відкритому_періоді_відхиляється_ECR_REG_0422()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Calculations.Tests/GoldenCalculationTests.cs`
MODULE: tests-calculations | STAGE: 4
CONTRACT: 02c-fixtures.md#calc

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Звірка розрахунків із очікуваними числами фікстури — **до останнього
/// знака** (ФВ-9.9).
/// </summary>
/// <remarks>
/// Це прообраз задачі <c>golden-compare</c> Етапу 5, тільки на синтетичних
/// даних. Якщо тут числа не сходяться, на реальному еталоні вони не зійдуться
/// й поготів.
/// </remarks>
public sealed class GoldenCalculationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_для_ХСК_збігаються_з_очікуваним_значенням_фікстури()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_для_завислих_речовин_збігаються_з_очікуваним()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Грами_за_секунду_у_режимі_Actual_збігаються_з_очікуваним()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Грами_за_секунду_у_режимі_Fixed360_відрізняються_на_три_відсотки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розрахунок_виконується_для_кожної_речовини_методології()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Константа_резолвиться_за_речовиною_а_не_береться_перша_ліпша()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результати_пишуться_в_calc_а_не_в_doc_CellValue()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Трейс_у_режимі_Off_не_пишеться_взагалі()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Calculations.Tests/MethodologyPublishTests.cs`
MODULE: tests-calculations | STAGE: 4

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона тихо змінює числа у вже поданих формах (ФВ-9.6).
/// </summary>
public sealed class MethodologyPublishTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_автором_останньої_правки_відхиляється_ECR_CALC_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_причини_зміни_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_дати_набуття_чинності_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_формує_diff_РЕЗУЛЬТАТІВ_а_не_diff_коду()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Закриті_періоди_після_публікації_НЕ_перераховуються_автоматично()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перерахунок_закритого_періоду_потребує_окремого_погодження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Топологічний_порядок_формул_обчислюється_при_публікації()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Api.Tests/ErrorContractTests.cs`
MODULE: tests-api | STAGE: 1
CONTRACT: 02-contracts.md#error-codes

```csharp
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контракт помилок: код стабільний, клієнт розрізняє причини **за кодом**,
/// а не за текстом.
/// </summary>
public sealed class ErrorContractTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Помилка_повертається_у_форматі_problem_json()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Тіло_помилки_містить_код_і_ідентифікатор_кореляції()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Конфлікт_повертає_409_із_переліком_розбіжностей()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відмова_в_доступі_повертає_403_із_ПРИЧИНОЮ_у_розширеннях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Внутрішня_помилка_не_розкриває_стек_і_текст_винятку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ідентифікатор_кореляції_повертається_у_заголовку_відповіді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Усі_коди_з_каталогу_мають_унікальні_значення()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Api.Tests/AuthenticationTests.cs`
MODULE: tests-api | STAGE: 3

```csharp
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Два провайдери, одна cookie (ФВ-6.1) і негайна дія відкликання прав.
/// </summary>
public sealed class AuthenticationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Локальний_вхід_видає_ту_саму_cookie_що_й_доменний()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Невірний_пароль_і_неіснуючий_користувач_дають_однакову_відповідь()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_N_невдалих_спроб_обліковий_запис_блокується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_ролей_робить_поточну_сесію_недійсною_негайно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_зустрічається_у_логах_трасуванні_і_відповідях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Анонімний_запит_до_захищеного_ендпоінта_дає_401_а_не_редирект()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Api.Tests/ApiConventionTests.cs`
MODULE: tests-api | STAGE: 1
CONTRACT: 02-contracts.md#api-conventions

```csharp
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Наскрізні конвенції API.</summary>
public sealed class ApiConventionTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Списковий_ендпоінт_повертає_сторінку_а_не_весь_набір()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Розмір_сторінки_понад_максимум_відхиляється_400()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Курсор_наступної_сторінки_повертає_наступні_елементи_без_пропусків()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довга_операція_повертає_202_із_ідентифікатором_задачі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Числа_передаються_рядком_щоб_не_втратити_точність()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дати_передаються_в_UTC_за_ISO_8601()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Специфікація_OpenAPI_генерується_і_валідна()
        => Assert.Fail("not implemented");
}
```

---

## Решта тестів застосунку

| Файл | STAGE | Що доводить |
|---|---:|---|
| `Documents/GetTableSliceTests.cs` | 1 | порожні комірки не повертаються; права додаються одним викликом; немає N+1 |
| `Documents/CreateRowTests.cs` | 1 | ліміт `MaxDynamicRows`; заборона рядків у `Fixed`-таблиці |
| `Periods/BuildPeriodCalendarTests.cs` | 3 | 12 періодів для місячного, 4 для квартального; `PeriodKey` за формулою |
| `Recalculation/RecalculationServiceTests.cs` | 2 | перераховується лише залежне піддерево; rollup — відкладено; `IsSnapshot` не каскадує |
| `Validation/ValidationEngineTests.cs` | 2 | зламане правило дає `Warning` про правило, а не `Error` даних |
| `Calculations/CalculationOrchestratorTests.cs` | 4 | паралельність за рівнями; профіль по модулях заповнюється |
| `Api/DocumentsControllerTests.cs` | 1 | контролер не містить логіки; права перевіряються в хендлері |
| `Api/HealthTests.cs` | 1 | `/health/db` повідомляє режим редакції і обмеження |
| `Periods/TimeZoneImmutabilityTests.cs` | 3 | `TimeZoneId` змінюється у `Draft`; після відкриття першого періоду — `ECR-PRD-0409` (ФВ-1.1a) |
| `Periods/SequenceRangeTests.cs` | 3 | `Sequence` 13 відхиляється на рівні домену **і** `CHECK` бази (`ECR-PRD-4224`, D-108) |
| `Periods/ReopenRaceTests.cs` | 3 | одночасні `Reopen` і `PeriodStateJob` серіалізуються; програвший бачить актуальний стан, а не тихо застосовується |
| `Localization/UiStringCatalogTests.cs` | 3 | fallback на мову за замовчуванням; відсутній ключ повертається як ключ; `err.<код>` резолвиться з того самого каталогу (ФВ-14.9a) |
| `Localization/UiStringRevisionTests.cs` | 3 | будь-який запис інкрементує `Revision`; `If-None-Match` дає `304` |
| `Security/SimulationTests.cs` | 3 | **будь-який** запис під симуляцією → `SimulationReadOnly` навіть із `Manage`; сеанс потрапляє в `aud.SimulationSession` **до** видачі профілю; профіль **не кешується** і не витікає справжньому користувачу; симуляція себе — `ECR-SIM-0422` |
| `Security/BootstrapAdminTests.cs` | 3 | доки `MustChangePassword`, будь-який інший запит → `ECR-PWD-0428`; зміна пароля знімає прапорець і крутить `SecurityStamp`; поява доменного адміністратора вимикає bootstrap-запис, але **не видаляє** його |
| `Registries/OrphanScanTests.cs` | 4 | звуження `ValidTo` ставить `IsOrphaned` і блокує `Submit` (`ECR-SUB-4221`); розширення назад **знімає** ознаку; читання зрізу ознаку **не перераховує** (D-98) |

Усі шістнадцять наведені **повністю** нижче.

---

## Решта тестів застосунку — повний текст

### `tests/Ecr.Application.Tests/Documents/GetTableSliceTests.cs`

```csharp
// tests/Ecr.Application.Tests/Documents/GetTableSliceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Читання зрізу — бюджет **p95 400 мс** на ~5 000 комірок (tz/08 §8.2).
/// Саме тому права перевіряються одним викликом, а не покомірково.
/// </summary>
public sealed class GetTableSliceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожні_комірки_не_повертаються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Права_перевіряються_одним_викликом_на_зріз()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Немає_запиту_на_кожен_рядок()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_повертається_окремою_ознакою()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ознака_IsOrphaned_читається_а_не_перераховується()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Documents/CreateRowTests.cs`

```csharp
// tests/Ecr.Application.Tests/Documents/CreateRowTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Створення рядка динамічної таблиці через той самий batch-PATCH (R-B2).</summary>
public sealed class CreateRowTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void BaseVersion_null_трактується_як_створення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Перевищення_MaxDynamicRows_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_рядка_у_Fixed_таблиці_заборонене()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дублікат_RowKey_дає_ECR_ROW_0409()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Periods/BuildPeriodCalendarTests.cs`

```csharp
// tests/Ecr.Application.Tests/Periods/BuildPeriodCalendarTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>Календар періодів і формула `PeriodKey` (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("Monthly", 12)]
    [InlineData("Quarterly", 4)]
    [InlineData("Yearly", 1)]
    public void Кількість_періодів_відповідає_періодичності(string kind, int expected)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void PeriodKey_рахується_як_рік_на_сто_плюс_послідовність()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Для_квартального_періоду_PeriodKey_не_є_датою()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_виклик_не_створює_дублікатів()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Recalculation/RecalculationServiceTests.cs`

```csharp
// tests/Ecr.Application.Tests/Recalculation/RecalculationServiceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>Інкрементний перерахунок за dirty-set (ФВ-3.5, ФВ-12.1).</summary>
public sealed class RecalculationServiceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перераховується_лише_залежне_піддерево()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_аркушний_rollup_відкладається_а_не_рахується_синхронно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порядок_береться_з_публікації_а_не_будується_щоразу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Повторний_прогін_на_тих_самих_даних_дає_ті_самі_числа()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Validation/ValidationEngineTests.cs`

```csharp
// tests/Ecr.Application.Tests/Validation/ValidationEngineTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// Валідація. **Блокує збереження лише комірковий `Error`** (D-90): заборона
/// зберегти проміжний стан зробила б роботу з великою таблицею неможливою.
/// </summary>
public sealed class ValidationEngineTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірковий_Error_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Error_рівня_документа_блокує_Submit_але_не_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зламане_правило_дає_Warning_про_правило_а_не_Error_даних()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_не_залежить_від_порядку_правил()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_валідації_переживає_перезавантаження()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Calculations/CalculationOrchestratorTests.cs`

```csharp
// tests/Ecr.Application.Tests/Calculations/CalculationOrchestratorTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Оркестрація прогону. Профіль по модулях заповнюється **завжди**: без нього
/// невідомо, звідки брати різницю між 20 і 10 хвилинами (ПРД-13, `J-1`).
/// </summary>
public sealed class CalculationOrchestratorTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Незалежні_гілки_графа_рахуються_паралельно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Профіль_по_модулях_заповнюється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Закритий_період_не_перераховується_автоматично()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Поданий_зріз_не_перераховується_взагалі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void IsCurrent_перемикається_однією_транзакцією()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Api.Tests/DocumentsControllerTests.cs`

```csharp
// tests/Ecr.Api.Tests/DocumentsControllerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Контролер не містить логіки — вона в обробнику.</summary>
public sealed class DocumentsControllerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Контролер_лише_делегує_обробнику()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Права_перевіряються_в_обробнику_а_не_атрибутом_контролера()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Помилка_повертається_як_ProblemDetails_із_кодом()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Api.Tests/HealthTests.cs`

```csharp
// tests/Ecr.Api.Tests/HealthTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Health-endpoint-и (tz/08 §8.5).</summary>
public sealed class HealthTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_режим_редакції()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_стан_RCSI()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_запас_партицій()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_live_не_звертається_до_БД()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Periods/TimeZoneImmutabilityTests.cs`

```csharp
// tests/Ecr.Application.Tests/Periods/TimeZoneImmutabilityTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// `TimeZoneId` незмінний після відкриття першого періоду (ФВ-1.1a, D-110):
/// ретроактивна зміна зсунула б межі **закритих** періодів і переписала б
/// `IsLateEdit` на **поданих** формах.
/// </summary>
public sealed class TimeZoneImmutabilityTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_стані_Draft_пояс_змінюється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_відкриття_першого_періоду_зміна_дає_ECR_PRD_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_поясу_в_Draft_перераховує_межі_періодів()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Periods/SequenceRangeTests.cs`

```csharp
// tests/Ecr.Application.Tests/Periods/SequenceRangeTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// `Sequence` обмежений 1…12 (ФВ-1.5a, D-108). Межа не довільна: партиційна
/// функція перелічує `YYYY01…YYYY12`, і `Sequence = 13` мовчки ліг би в
/// грудневу партицію та поїхав в архів разом із груднем.
/// </summary>
public sealed class SequenceRangeTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(1)] [InlineData(12)]
    public void Значення_в_межах_приймаються(byte sequence)
        => Assert.Fail("not implemented");

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(0)] [InlineData(13)] [InlineData(99)]
    public void Значення_поза_межами_дають_ECR_PRD_4224(byte sequence)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Обмеження_діє_і_на_рівні_бази_а_не_лише_домену()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Custom_періодичність_теж_обмежена_дванадцятьма()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Periods/ReopenRaceTests.cs`

```csharp
// tests/Ecr.Application.Tests/Periods/ReopenRaceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// Гонка `Reopen` і `PeriodStateJob` (ФВ-1.10a). Обидві операції беруть рядок
/// періоду з `UPDLOCK`; програвший бачить актуальний стан, а не тихо
/// застосовується до вже закритого періоду.
/// </summary>
public sealed class ReopenRaceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Одночасні_Reopen_і_закриття_серіалізуються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Програвший_бачить_актуальний_стан_і_відмовляє_з_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Два_одночасні_Reopen_дають_один_результат()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Localization/UiStringCatalogTests.cs`

```csharp
// tests/Ecr.Application.Tests/Localization/UiStringCatalogTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// Каталог рядків інтерфейсу (ФВ-14.9). **Порожнеча не повертається ніколи**:
/// одна забута локалізація не має ламати екран.
/// </summary>
public sealed class UiStringCatalogTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відсутній_переклад_підмінюється_мовою_за_замовчуванням()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Ключ_якого_немає_ніде_повертається_як_сам_ключ()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Тексти_помилок_резолвляться_з_того_самого_каталогу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Публічна_область_не_містить_адміністративних_підписів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Анонімний_запит_приватної_області_відхиляється()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Localization/UiStringRevisionTests.cs`

```csharp
// tests/Ecr.Application.Tests/Localization/UiStringRevisionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>Версія каталогу як `ETag` (ФВ-14.9c).</summary>
public sealed class UiStringRevisionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Будь_який_запис_інкрементує_Revision()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Збіг_If_None_Match_дає_304()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Два_одночасні_записи_дають_різні_версії()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Різні_області_мають_незалежні_ETag()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Security/SimulationTests.cs`

```csharp
// tests/Ecr.Application.Tests/Security/SimulationTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Симуляція «очима користувача» — **лише читання** (ФВ-6.16a, D-96).
/// </summary>
/// <remarks>
/// Два тести тут захищають від різних видів провалу: перший — від того, що
/// симуляція стане способом щось зробити за іншого; третій — від того, що
/// профіль суб'єкта витече справжньому користувачеві через кеш.
/// </remarks>
public sealed class SimulationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Запис_під_симуляцією_відхиляється_навіть_із_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Сеанс_потрапляє_в_аудит_до_видачі_профілю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_симуляції_не_кешується_і_не_витікає_справжньому_користувачу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Автором_дій_лишається_той_хто_симулює()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Симуляція_самого_себе_дає_ECR_SIM_0422()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Завершити_можна_лише_власний_сеанс()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Security/BootstrapAdminTests.cs`

```csharp
// tests/Ecr.Application.Tests/Security/BootstrapAdminTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>Життєвий цикл bootstrap-адміністратора (ФВ-6.18, D-97, D-115).</summary>
public sealed class BootstrapAdminTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Створюється_лише_якщо_запису_ще_немає()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відсутня_змінна_середовища_дає_попередження_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Доки_MustChangePassword_інші_запити_дають_ECR_PWD_0428()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_пароля_знімає_прапорець_і_крутить_SecurityStamp()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поява_доменного_адміністратора_вимикає_запис_але_не_видаляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_потрапляє_в_лог()
        => Assert.Fail("not implemented");
}
```

### `tests/Ecr.Application.Tests/Registries/OrphanScanTests.cs`

```csharp
// tests/Ecr.Application.Tests/Registries/OrphanScanTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Ознака `IsOrphaned` (ФВ-8.13, D-98). Механізм **симетричний**: те, що
/// ставить ознаку, має її й знімати — інакше виправлення довідника не
/// розблокує `Submit`.
/// </summary>
public sealed class OrphanScanTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Звуження_ValidTo_ставить_IsOrphaned()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Осиротілий_рядок_блокує_Submit_із_ECR_SUB_4221()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Осиротілий_рядок_не_блокує_читання()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розширення_вікна_назад_знімає_ознаку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Читання_зрізу_ознаку_не_перераховує()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нічна_перевірка_не_чіпає_закриті_періоди()
        => Assert.Fail("not implemented");
}
```
