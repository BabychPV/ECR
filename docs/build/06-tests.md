# 06 — Скелет тестів

> Частина пакета. Формат — той самий, що в [`05-skeleton.md`](05-skeleton.md) §2.
>
> **Головне правило:** «зробити тест зеленим», а не «підігнати тест під код»
> (`08-workflow.md` §4). Якщо тест здається неправильним — це `questions.md`
> і зупинка, а не правка очікуваного значення.

## Частини

| Частина | Що містить | Проєкт |
|---|---|---|
| [`06a-tests-domain.md`](06a-tests-domain.md) | інваріанти домену, значеннєві типи, одиниці, періоди | `Ecr.Domain.Tests` |
| [`06b-tests-expressions.md`](06b-tests-expressions.md) | лексер, парсер, `null`-семантика, функції, діапазони, одиниці | `Ecr.Expressions.Tests` |
| [`06c-tests-infrastructure.md`](06c-tests-infrastructure.md) | EF Core, ключі, конкурентність, партиції, архівація | `Ecr.Infrastructure.Tests` |
| [`06d-tests-application.md`](06d-tests-application.md) | use-cases, доступ, workflow, розрахунки, API | `Ecr.Application.Tests`, `Ecr.Api.Tests`, `Ecr.Calculations.Tests` |
| [`06e-tests-architecture.md`](06e-tests-architecture.md) | вісім архітектурних правил, ліцензії, фронтенд | `Ecr.Architecture.Tests`, `Ecr.Web` |

---

## 1. Спільна інфраструктура тестів

### `tests/Ecr.TestKit/Ecr.TestKit.csproj`
MODULE: testkit | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.TestKit</RootNamespace>
    <IsPackable>false</IsPackable>
    <!-- XML-doc у тестових хелперах не вимагаємо -->
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Infrastructure\Ecr.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
  </ItemGroup>

  <ItemGroup>
    <None Update="Fixtures\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

---

### `tests/Ecr.TestKit/Fixtures/water-demo.json`
MODULE: testkit | STAGE: 0
CONTRACT: 02c-fixtures.md#json

**`COPY FROM`** [`02c-fixtures.md#json`](02c-fixtures.md#json) — дослівно,
увесь JSON із розділу 9.

> Це **золотий набір ПК-2**. Тести читають очікувані значення звідси, а не
> хардкодять їх. Якщо очікуване значення треба змінити — це або помилка у
> фікстурі (`questions.md` і зупинка), або зміна вимоги (спершу
> `tz/10-decisions.md`).

---

### `tests/Ecr.TestKit/FixtureLoader.cs`
MODULE: testkit | STAGE: 0

```csharp
using System.Text.Json;

namespace Ecr.TestKit;

/// <summary>
/// Завантажує фікстури з <c>water-demo.json</c>.
/// </summary>
/// <remarks>
/// Очікувані числа беруться **з файлу**, а не з коду тесту: інакше правка
/// очікування стає непомітною, а саме через це «зелені» тести перестають
/// щось означати.
/// </remarks>
public static class FixtureLoader
{
    /// <summary>Завантажений набір.</summary>
    public static WaterDemoFixture Load()
        => throw new NotImplementedException(
            "TODO: прочитати Fixtures/water-demo.json з каталогу виводу; " +
            "десеріалізувати в WaterDemoFixture; кешувати статично.");

    /// <summary>Очікуване значення за шляхом, напр. <c>\"Water_07.Main.Total\" → \"7001001\"</c>.</summary>
    public static decimal ExpectedDecimal(string path, string key)
        => throw new NotImplementedException(
            "TODO: дістати з розділу expected; парсити інваріантно як decimal. " +
            "Відсутній ключ — це помилка тесту, а не привід повернути 0.");
}

/// <summary>Модель фікстури.</summary>
public sealed class WaterDemoFixture
{
    /// <summary>Сирий JSON — для розділів, які тест читає динамічно.</summary>
    public required JsonDocument Raw { get; init; }
}
```

---

### `tests/Ecr.TestKit/TestClock.cs`
MODULE: testkit | STAGE: 0

```csharp
using Ecr.Domain.Abstractions;

namespace Ecr.TestKit;

/// <summary>
/// Керований годинник. Саме заради нього в системі заборонений
/// <c>DateTime.Now</c>: без фіксованого часу поведінка періодів, offsets і
/// <c>IsLateEdit</c> невідтворювана.
/// </summary>
public sealed class TestClock(DateTime utcNow) : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    /// <summary>Переводить годинник уперед.</summary>
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    /// <summary>Встановлює точний момент.</summary>
    public void Set(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}
```

---

### `tests/Ecr.TestKit/SqlServerFixture.cs`
MODULE: testkit | STAGE: 1

```csharp
using Testcontainers.MsSql;

namespace Ecr.TestKit;

/// <summary>
/// Реальний SQL Server у контейнері для інтеграційних тестів.
/// </summary>
/// <remarks>
/// SQLite тут не підходить: перевіряти треба саме те, чого в ньому немає —
/// партиціонування, складені FK, <c>TRUNCATE … WITH (PARTITIONS)</c>, RCSI,
/// тригери. Тести, що використовують цю фікстуру, позначені
/// <c>[Trait("Category","Integration")]</c> і не входять у прогін за
/// замовчуванням (`04-environment.md` §5).
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    /// <summary>Рядок підключення до тестової БД.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
        => throw new NotImplementedException(
            "TODO: якщо змінна ECR_TEST_SQL задана — використати локальний SQL Server і НЕ " +
            "піднімати контейнер (не на кожній машині є Docker); інакше MsSqlBuilder з " +
            "образом mcr.microsoft.com/mssql/server:2022-latest. " +
            "Після старту: створити БД, застосувати міграції, виконати SQL-скрипти 01–06 " +
            "(файлові групи, партиції, RCSI), виконати seed.");

    /// <inheritdoc />
    public Task DisposeAsync()
        => throw new NotImplementedException("TODO: зупинити контейнер, якщо він піднімався.");
}
```

---

### `tests/Ecr.TestKit/TestCategories.cs`
MODULE: testkit | STAGE: 0

```csharp
namespace Ecr.TestKit;

/// <summary>
/// Константи трейтів. Дозволяють ганяти тести окремого етапу і відділяти
/// інтеграційні від решти.
/// </summary>
public static class TestCategories
{
    /// <summary>Назва трейта етапу.</summary>
    public const string Stage = "Stage";

    /// <summary>Назва трейта категорії.</summary>
    public const string Category = "Category";

    public const string Stage1 = "Stage1";
    public const string Stage2 = "Stage2";
    public const string Stage3 = "Stage3";
    public const string Stage4 = "Stage4";
    public const string Stage5 = "Stage5";
    public const string Stage6 = "Stage6";

    /// <summary>Потребує реального SQL Server; не входить у прогін за замовчуванням.</summary>
    public const string Integration = "Integration";

    /// <summary>Архітектурні правила — блокуючі в CI.</summary>
    public const string Architecture = "Architecture";
}
```

---

## 2. Правила написання тестів

1. **Назва описує бізнес-правило з ТЗ**, а не метод.
   Погано: `PatchCells_Should_Work`.
   Добре: `Закритий_період_блокує_запис_навіть_власнику_Manage`.
2. **Кожен тест має трейт етапу**: `[Trait(TestCategories.Stage, TestCategories.Stage1)]`.
3. **Тести, що потребують SQL Server**, додатково мають
   `[Trait(TestCategories.Category, TestCategories.Integration)]`.
4. **Асерти — тільки вбудовані `Assert.*` з xUnit.** `FluentAssertions` 8+
   під комерційною ліцензією і заборонений (`D-12`).
5. **Очікувані числа — з фікстури**, не з коду тесту.
6. **Тіло на Етапі 0** — `Assert.Fail("not implemented")`. Тест має
   **запускатися і падати**; нуль знайдених тестів — провал Етапу 0.
7. **Один тест — одне правило.** Якщо в назві є «і», тестів має бути два.
8. **Заборонено** `[Skip]`, `[Ignore]`, закоментовані й видалені тести,
   послаблені асерти, `try/catch` заради зеленого результату.

---

## 3. Розподіл тестів за етапами

| Етап | Проєкти | Приблизно тестів | Ключове, що доводиться |
|---|---|---:|---|
| 1 | Domain, Infrastructure, Application, Api | ~90 | інваріанти публікації, три стани комірки, конкурентність, ключі й партиції |
| 2 | Expressions, Application | ~110 | граматика, `null`-семантика, діапазони, граф, валідація |
| 3 | Domain, Infrastructure, Api | ~70 | 15 сценаріїв доступу, періоди, workflow, `Reopen` |
| 4 | Domain, Calculations, Application | ~80 | реєстри, одиниці, конверсії, методології, `CalendarMode` |
| 5 | Adapters, Infrastructure, tools | ~60 | ідемпотентність збору, імпорт із diff, архівація, `rpt.*` |
| 6 | `Ecr.Web` (vitest) | ~40 | grid, вставка, undo, права по комірках |
| — | Architecture | 8 + 1 | вісім правил залежностей і перевірка ліцензій |

**Разом ≈ 460 тестів.** Це орієнтир, а не квота: тест, який не перевіряє
правила з ТЗ, не потрібен, навіть якщо він додає число.
