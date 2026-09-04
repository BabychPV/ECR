# 06e — Тести: архітектура, ліцензії, фронтенд

> Частина [`06-tests.md`](06-tests.md).
>
> Тести цього файлу — **головний захист від тихого дрейфу** (`D-82`). Вони не
> залежать від уваги рецензента: правило або виконується, або збірка червона.
> При швидкості генерації коду це важливіше, ніж було б для людської команди.

---

### `tests/Ecr.Architecture.Tests/Ecr.Architecture.Tests.csproj`
MODULE: tests-architecture | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Architecture.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Expressions\Ecr.Expressions.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Infrastructure\Ecr.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Calculations\Ecr.Calculations.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Adapters.Excel\Ecr.Adapters.Excel.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Adapters.PiAf\Ecr.Adapters.PiAf.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Api\Ecr.Api.csproj" />
    <ProjectReference Include="..\Ecr.TestKit\Ecr.TestKit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NetArchTest.Rules" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

</Project>
```

---

### `tests/Ecr.Architecture.Tests/LayerRulesTests.cs`
MODULE: tests-architecture | STAGE: 1
CONTRACT: 01-TOR.md §8

```csharp
using Ecr.TestKit;
using NetArchTest.Rules;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Вісім правил залежностей із <c>tz/03</c> §3.3.
/// </summary>
/// <remarks>
/// Ці тести — не формальність. Кожне правило захищає конкретну властивість
/// системи: тестованість без БД, замінність адаптерів, універсальність ядра.
/// Порушення жодного з них не проявиться як помилка — воно проявиться через
/// рік як «чому це неможливо змінити».
/// </remarks>
public sealed class LayerRulesTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_1_домен_не_залежить_ні_від_чого()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_2_застосунок_не_знає_про_інфраструктуру_і_EF_Core()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_3_ядро_не_знає_про_AF_Excel_і_екологію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_4_DbContext_не_зустрічається_у_контролерах()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_5_немає_блокувальних_викликів_Result_і_Wait()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_6_у_застосунку_немає_ToList_без_Take()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_7_перевірка_ролей_робиться_лише_через_IAccessDecisionService()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_8_у_результатних_типах_немає_float_і_double()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Architecture.Tests/ForbiddenApiTests.cs`
MODULE: tests-architecture | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Заборонені виклики, які ламають конкретні властивості системи.
/// </summary>
public sealed class ForbiddenApiTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void DateTime_Now_і_UtcNow_не_використовуються_поза_реалізацією_IClock()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void IExternalDataSink_не_існує_в_жодній_збірці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_коді_немає_DDL_окрім_міграцій_і_генератора_вьюх()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Публічні_асинхронні_методи_приймають_CancellationToken()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Немає_async_void_окрім_обробників_подій()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Секрети_не_читаються_з_конфігурації_напряму_а_лише_за_іменем()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Architecture.Tests/LicenseComplianceTests.cs`
MODULE: tests-architecture | STAGE: 1
CONTRACT: 04-environment.md §3.3

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ліцензійна дисципліна (D-12).
/// </summary>
/// <remarks>
/// Заборонені пакети перелічені явно, бо кожен із них уже одного разу
/// потрапив у чернетку архітектури і був знятий після перевірки першоджерела:
/// <c>FluentAssertions</c> 8+ (комерційна Xceed), <c>EFCore.BulkExtensions</c>
/// (платна), <c>EPPlus</c> 5+ (noncommercial), <c>HyperFormula</c> (GPLv3),
/// <c>NBomber</c> 5+ (комерційна), Redis-сервер (AGPL/SSPL).
/// </remarks>
public sealed class LicenseComplianceTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_проєкт_не_посилається_на_заборонений_пакет()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Версії_пакетів_оголошені_централізовано_а_не_в_csproj()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_прямий_пакет_має_запис_у_реєстрі_стека()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Домен_не_має_жодного_PackageReference()
        => Assert.Fail("not implemented");
}
```

---

### `tests/Ecr.Architecture.Tests/ContractIntegrityTests.cs`
MODULE: tests-architecture | STAGE: 1

```csharp
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Цілісність контрактів: те, що описано в <c>02-contracts.md</c>, має
/// існувати в коді і не розходитися з ним.
/// </summary>
/// <remarks>
/// Рев'ю Етапу N перевіряє, що <c>git diff</c> по файлах контрактів порожній.
/// Ці тести перевіряють інше: що контракт **реалізований повністю** — усі
/// enum-значення на місці, усі порти мають реалізацію (крім свідомо
/// відсутніх), усі коди помилок унікальні.
/// </remarks>
public sealed class ContractIntegrityTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_порт_має_рівно_одну_реалізацію_окрім_явно_множинних()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Коди_помилок_унікальні_і_відповідають_формату()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_значення_EditDenyReason_повертається_хоча_б_одним_шляхом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ендпоінт_із_таблиці_бюджету_має_метрику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Публічні_типи_мають_XML_документацію()
        => Assert.Fail("not implemented");
}
```

---

## Тести фронтенду (`Ecr.Web`, vitest)

### `src/Ecr.Web/src/features/grid/__tests__/clipboard.test.ts`
MODULE: tests-web | STAGE: 6

```typescript
import { describe, it, expect } from 'vitest';

/**
 * Вставка з буфера Excel — критерій FQ-1 №3 і найчастіша причина, з якої
 * grid-бібліотека не підходить.
 */
describe('Вставка з буфера Excel', () => {
  it('розбирає багатоклітинний буфер із табуляціями і переносами рядків', () => {
    expect.fail('not implemented');
  });

  it('розпізнає десяткову кому в локалі користувача', () => {
    expect.fail('not implemented');
  });

  it('вставка 500×60 не блокує UI довше за 200 мс', () => {
    expect.fail('not implemented');
  });

  it('вставка у read-only комірки відхиляє ВЕСЬ батч і показує перелік заборонених', () => {
    expect.fail('not implemented');
  });

  it('копіювання у буфер дає формат, який приймає Excel', () => {
    expect.fail('not implemented');
  });
});
```

---

### `src/Ecr.Web/src/features/grid/__tests__/undo.test.ts`
MODULE: tests-web | STAGE: 6

```typescript
import { describe, it, expect } from 'vitest';

/**
 * Undo/Redo ≥50 кроків — критерій FQ-1 №6. Потребує **власної моделі
 * команд**: не всі grid-бібліотеки дозволяють її вбудувати без форку, і саме
 * це перевіряється прототипом на Етапі 0.
 */
describe('Undo/Redo', () => {
  it('скасовує останню зміну', () => {
    expect.fail('not implemented');
  });

  it('тримає щонайменше 50 кроків історії', () => {
    expect.fail('not implemented');
  });

  it('повторює скасовану зміну', () => {
    expect.fail('not implemented');
  });

  it('скидає історію при переході на іншу таблицю', () => {
    expect.fail('not implemented');
  });

  it('вставка діапазону — це ОДИН крок історії, а не сотні', () => {
    expect.fail('not implemented');
  });
});
```

---

### `src/Ecr.Web/src/features/grid/__tests__/permissions.test.ts`
MODULE: tests-web | STAGE: 6

```typescript
import { describe, it, expect } from 'vitest';

/** Права по комірках приходять із сервера і показуються, а не вгадуються. */
describe('Права по комірках', () => {
  it('read-only комірки візуально відрізняються', () => {
    expect.fail('not implemented');
  });

  it('підказка показує причину заборони, а не просто «недоступно»', () => {
    expect.fail('not implemented');
  });

  it('редагування забороненої комірки не надсилає запит на сервер', () => {
    expect.fail('not implemented');
  });

  it('обчислені комірки не редагуються', () => {
    expect.fail('not implemented');
  });
});
```

---

### `src/Ecr.Web/src/api/__tests__/errors.test.ts`
MODULE: tests-web | STAGE: 6

```typescript
import { describe, it, expect } from 'vitest';

/**
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
describe('Обробка помилок API', () => {
  it('розбирає EcrProblemDetails і зберігає код помилки', () => {
    expect.fail('not implemented');
  });

  it('конфлікт розпізнається за кодом і містить перелік розбіжностей', () => {
    expect.fail('not implemented');
  });

  it('401 веде на сторінку входу без спроби мовчазного повторного входу', () => {
    expect.fail('not implemented');
  });

  it('4xx не повторюється автоматично', () => {
    expect.fail('not implemented');
  });

  it('користувачеві показується текст сервера, а не власний узагальнений', () => {
    expect.fail('not implemented');
  });
});
```

---

### `src/Ecr.Web/src/shared/__tests__/formula-equivalence.test.ts`
MODULE: tests-web | STAGE: 6

```typescript
import { describe, it, expect } from 'vitest';

/**
 * Клієнтський обчислювач — **лише підказка**; збережене значення завжди рахує
 * сервер (D-20). Але якщо підказка систематично розходиться з результатом,
 * користувач перестає їй вірити — тому спільний набір випадків має збігатися.
 *
 * Набір читається з того самого JSON, що й серверний тест еквівалентності
 * (`06b`).
 */
describe('Еквівалентність клієнт/сервер', () => {
  it('спільний набір виразів дає ті самі результати, що й сервер', () => {
    expect.fail('not implemented');
  });

  it('SUM порожньої множини дає 0, як на сервері', () => {
    expect.fail('not implemented');
  });

  it('AVERAGE порожньої множини дає null, як на сервері', () => {
    expect.fail('not implemented');
  });

  it('ROUND округлює від нуля, як на сервері', () => {
    expect.fail('not implemented');
  });
});
```

---

## Що ці тести доводять разом

| Група | Захищає |
|---|---|
| Правила шарів | тестованість без БД, замінність адаптерів, універсальність ядра |
| Заборонені виклики | відтворюваність часу, відсутність DDL із коду, коректність async |
| Ліцензії | що збірка не стане юридично непридатною через один `PackageReference` |
| Цілісність контрактів | що описане в `02-contracts.md` існує в коді і повне |
| Grid (vitest) | що найдорожчий компонент справді вміє те, заради чого обрано бібліотеку |
| Еквівалентність формул | що підказка на клієнті не суперечить збереженому значенню |

**Разом із рештою частин — близько 460 тестів.** Це орієнтир, а не квота:
тест, який не перевіряє правила з ТЗ, не потрібен, навіть якщо він додає число.
