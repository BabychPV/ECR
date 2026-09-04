# 05f — Скелет: `Ecr.Calculations`

> Частина [`05-skeleton.md`](05-skeleton.md).
> Модель розрахунків — [`02a-db-schema.md#calc`](02a-db-schema.md#calc),
> фікстури з очікуваними числами — [`02c-fixtures.md#calc`](02c-fixtures.md#calc).
>
> **Два різні місця обчислених значень, які не можна плутати** (`D-69`):
> формули шаблону матеріалізуються в `doc.CellValue` з `IsCalculated = 1`;
> результати методологій живуть у `calc.CalculationResult` і потрапляють у
> документ **посиланням** через `cfg.CalculationBinding`.

---

### `src/Ecr.Calculations/GenericCalculationModule.cs`
MODULE: calculations | STAGE: 4
CONTRACT: 02-contracts.md#ports
SCOPE: рівень 1 драбини — методологія як конфігурація.
NOT IN SCOPE: скрипти рівня 2 і модулі рівня 3.

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Generic-модуль: виконує методологію, задану **даними** — формулами,
/// константами і речовинами (ФВ-9.1).
/// </summary>
/// <remarks>
/// Мета — «generic плюс явний список винятків», а не доведення універсальності
/// (ФВ-9.3). Якщо після класифікації 44 методологій рівень 2 потрібен більш ніж
/// для 5 — проблема не в методологіях, а в граматиці рівня 1: дешевше
/// розширити граматику, ніж плодити скрипти.
/// </remarks>
public sealed class GenericCalculationModule(
    IFormulaEngine formulaEngine,
    ConstantResolver constants,
    CalendarContext calendar,
    NumericPolicy numeric) : ICalculationModule
{
    /// <inheritdoc />
    public string Code => "generic";

    /// <inheritdoc />
    public CalculationLevel Level => CalculationLevel.Configuration;

    /// <inheritdoc />
    public bool CanHandle(MethodologyDescriptor methodology)
        => throw new NotImplementedException(
            "TODO: повертати true, якщо methodology.Level == Configuration і всі її формули " +
            "розібралися в діалекті Methodology без діагностик.");

    /// <inheritdoc />
    public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — порядок обчислення:\n" +
            "1) побудувати IEvaluationContext: аргументи з input, константи через constants " +
            "   (резолвлені за категорією, речовиною і ДАТОЮ ПЕРІОДУ), календар через calendar;\n" +
            "2) обчислити формули за EvaluationOrder — він уже топологічний із Publish, " +
            "   сортувати граф тут не треба;\n" +
            "3) для КОЖНОЇ речовини методології порахувати виходи (tons, gsec);\n" +
            "4) округлення і арифметику застосовувати через numeric — режим Legacy має " +
            "   відтворювати числа чинної системи побітово (ФВ-9.9);\n" +
            "5) писати трейс лише згідно з TraceLevel версії (Off/ErrorsOnly/Full) — " +
            "   керуємо тим, ЩО пишемо, а не скільки зберігаємо (ЗБР-3);\n" +
            "6) НЕ писати в БД: повернути CalculationOutput, запис робить CalculationOutputWriter.");
}
```

---

### `src/Ecr.Calculations/CalculationOrchestrator.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Оркеструє прогін розрахунку: підбирає методології, будує порядок, виконує
/// модулі, записує результати.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, тому «не гірше» тут не працює: потрібне
/// щонайменше дворазове прискорення. Це і диктує архітектуру нижче.
/// </remarks>
public sealed class CalculationOrchestrator(
    MethodologyResolver resolver,
    IEnumerable<ICalculationModule> modules,
    CalculationInputBuilder inputBuilder,
    CalculationOutputWriter outputWriter,
    IFormulaEngine formulaEngine)
{
    /// <summary>Виконує прогін.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <param name="triggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<long> RunAsync(int projectId, PeriodKey? periodKey, int? triggeredByUserId,
                               IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — усе нижче випливає з бюджету 10 хвилин:\n" +
            "1) створити calc.CalculationRun;\n" +
            "2) підібрати методології ПРАВИЛАМИ (MethodologyRule.MatchJson), не жорстким списком;\n" +
            "3) побудувати граф залежностей МІЖ методологіями (MethodologyDependency бере участь " +
            "   у топологічному порядку нарівні з формулами) і розбити на РІВНІ;\n" +
            "4) виконувати рівень за рівнем, усередині рівня — ПАРАЛЕЛЬНО " +
            "   (Parallel.ForEachAsync з обмеженням): послідовний прогін у 10 хвилин не вкладеться;\n" +
            "5) входи читати ПАКЕТНО — один запит на методологію × період, не N запитів на рядок;\n" +
            "6) результати писати SqlBulkCopy через outputWriter; SaveChanges у циклі заборонений;\n" +
            "7) проміжні значення тримати в пам'яті воркера;\n" +
            "8) заповнити ModulesProfileJson — профіль по модулях: очікується, що топ-5 дають " +
            "   ~80% часу, і оптимізувати треба саме їх (питання J-1);\n" +
            "9) ⚠ ЗАКРИТІ ПЕРІОДИ автоматично не перераховувати НІКОЛИ (ФВ-9.7): це окрема " +
            "   операція з власним погодженням, інакше публікація методології заднім числом " +
            "   змінює подану звітність.");
}
```

---

### `src/Ecr.Calculations/MethodologyResolver.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Підбирає версію методології, чинну на дату періоду, і зіставляє її з
/// рядками документа за правилами (ФВ-13.3).
/// </summary>
public sealed class MethodologyResolver(Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Знаходить чинну версію методології на дату.</summary>
    public Task<MethodologyDescriptor?> ResolveVersionAsync(int methodologyId, DateOnly onDate, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: вибрати Published-версію з максимальним EffectiveFrom <= onDate. " +
            "Якщо жодної — це не порожній результат, а помилка конфігурації: " +
            "методологія прив'язана, але не має чинної версії.");

    /// <summary>Визначає, які рядки документа обробляє методологія.</summary>
    public Task<IReadOnlyList<string>> MatchRowsAsync(
        int methodologyVersionId, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: застосувати MethodologyRule.MatchJson (предикат по колонках рядка) у порядку " +
            "Priority; перший збіг виграє. Матриця покриття (ФВ-13.4) будується на тому самому " +
            "механізмі — вона показує, які рядки не закрилися жодним правилом.");
}
```

---

### `src/Ecr.Calculations/ConstantResolver.cs`
MODULE: calculations | STAGE: 4

```csharp
namespace Ecr.Calculations;

/// <summary>
/// Резолвить константи методології за категорією, речовиною і датою.
/// </summary>
/// <remarks>
/// Саме тут живуть **контекстні коефіцієнти** — щільність, теплотворність,
/// молярна маса. Вони залежать від речовини й умов і змінюються з часом, тому
/// не є конверсіями одиниць і в <c>uom.Conversion</c> потрапити не можуть
/// (ФВ-16.5). Це розмежування — головне, що не дає числам «попливти» глобально.
/// </remarks>
public sealed class ConstantResolver(Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Значення константи з одиницею.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="category">Категорія; <c>null</c> — без категорії.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна константа.</param>
    /// <param name="onDate">Дата періоду для темпорального вибору.</param>
    public Task<(decimal Value, int UnitId)?> ResolveAsync(
        int methodologyVersionId, string code, string? category, int? substanceEntryId,
        DateOnly onDate, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: фільтр за версією і кодом; звузити за категорією і речовиною (точний збіг " +
            "виграє над загальним); з темпоральних вибрати той, чий інтервал ValidFrom..ValidTo " +
            "містить onDate. Кілька кандидатів на одну дату — помилка конфігурації, а не " +
            "привід узяти перший.");
}
```

---

### `src/Ecr.Calculations/CalendarContext.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Domain.Enums;
using Ecr.Expressions;

namespace Ecr.Calculations;

/// <summary>
/// Будує календарний контекст періоду згідно з <see cref="CalendarMode"/>
/// версії методології.
/// </summary>
/// <remarks>
/// Це не косметика. Перерахунок у <c>г/с</c> ділить на кількість секунд у
/// періоді: для січня 2026 в режимі <c>Actual</c> це 2 678 400 с, у
/// <c>Fixed360</c> — 2 592 000 с. Різниця на тих самих даних — **3.3 %**, і
/// виглядає вона як помилка формули, а не як різниця конвенції (D-78,
/// перевіряється фікстурою 02c §7).
/// </remarks>
public sealed class CalendarContext
{
    /// <summary>Створює контекст для періоду.</summary>
    public PeriodContext Build(DateOnly start, DateOnly end, CalendarMode mode, int year, byte sequence)
        => new(start, end, mode, year, sequence);
}
```

---

### `src/Ecr.Calculations/NumericPolicy.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Арифметична політика версії методології.
/// </summary>
/// <remarks>
/// Режим <see cref="NumericMode.Legacy"/> існує **виключно** заради побітової
/// сумісності з числами чинної системи (ФВ-9.9): порядок операцій, момент
/// округлення і кількість знаків мають збігатися. Це не «гірший» режим —
/// це умова того, що звірка з еталоном узагалі можлива.
/// </remarks>
public sealed class NumericPolicy(NumericMode mode)
{
    /// <summary>Режим.</summary>
    public NumericMode Mode { get; } = mode;

    /// <summary>Округлення згідно з режимом.</summary>
    public decimal Round(decimal value, int digits)
        => throw new NotImplementedException(
            "TODO: MidpointRounding.AwayFromZero в обох режимах — банківське округлення дало б " +
            "інші числа. Різниця Legacy/Strict — у МОМЕНТІ округлення: Legacy округлює після " +
            "кожного кроку так само, як чинна система, Strict — лише на виході.");

    /// <summary>Скільки знаків зберігати для виходу.</summary>
    public int OutputScale => 6;
}
```

---

### `src/Ecr.Calculations/CalculationInputBuilder.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>Готує аргументи для методології з даних документа.</summary>
public sealed class CalculationInputBuilder(ICellStore cellStore, IMetadataCache metadata)
{
    /// <summary>Будує входи для набору рядків одним пакетом.</summary>
    /// <remarks>
    /// Пакетність принципова: читання по рядку не вкладається в бюджет
    /// 10 хвилин на річний перерахунок.
    /// </remarks>
    public Task<IReadOnlyList<CalculationInput>> BuildAsync(
        long tableInstanceId, IReadOnlyList<string> rowKeys, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: один ReadSliceAsync на таблицю; зіставити колонки з іменами аргументів " +
            "методології; для кожного рядка зібрати CalculationInput. " +
            "Зберегти входи в calc.CalculationInput — це частина доказової бази " +
            "відтворюваності (B-4).");
}
```

---

### `src/Ecr.Calculations/CalculationOutputWriter.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>Записує результати прогону.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>. У <c>doc.CellValue</c>
/// результати методологій не потрапляють ніколи (D-69): інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>.
/// </remarks>
public sealed class CalculationOutputWriter(
    Ecr.Infrastructure.Persistence.BulkCellLoader bulk,
    Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Записує результати і трейс пакетно.</summary>
    public Task WriteAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: Id узяти з calc.CalculationResultSeq одним викликом sp_sequence_get_range; " +
            "писати SqlBulkCopy у calc.CalculationResult; трейс — у calc.CalculationStep, " +
            "але лише якщо TraceLevel це передбачає. " +
            "Після завершення прогону — інвалідувати залежні зрізи rpt.*.");
}
```

---

### `src/Ecr.Calculations/TraceRecorder.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Накопичує трейс обчислення згідно з <see cref="TraceLevel"/>.
/// </summary>
/// <remarks>
/// Політика зберігання — «нічого не затирається» (ЗБР-1), тому обсяг керується
/// **тим, що пишемо**, а не строком: записане живе назавжди, непотрібне просто
/// не пишеться (ЗБР-3). Повний трейс кожного кроку кожної методології кожного
/// періоду перевищив би обсяг самих даних.
/// </remarks>
public sealed class TraceRecorder(TraceLevel level)
{
    /// <summary>Записує крок, якщо рівень це передбачає.</summary>
    public void Step(string code, string? expression, decimal? value)
        => throw new NotImplementedException(
            "TODO: Off → нічого; ErrorsOnly → лише кроки з помилкою; Full → усі. " +
            "Накопичувати в пам'яті, писати пакетно наприкінці прогону.");

    /// <summary>Зібрані кроки.</summary>
    public IReadOnlyList<TraceStep> Steps => throw new NotImplementedException("TODO");
}

/// <summary>Крок трейсу.</summary>
public sealed record TraceStep(int Order, string Code, string? Expression, decimal? Value, string? Error);
```

---

### `src/Ecr.Calculations/DependencyInjection.cs`
MODULE: calculations | STAGE: 4

```csharp
using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>Реєстрація рушія розрахунків.</summary>
public static class DependencyInjection
{
    /// <summary>Додає модулі і оркестратор.</summary>
    public static IServiceCollection AddEcrCalculations(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddScoped<ICalculationModule, GenericCalculationModule>();  // рівень 1\n" +
            "AddScoped<CalculationOrchestrator>();\n" +
            "AddScoped<MethodologyResolver>();\n" +
            "AddScoped<ConstantResolver>();\n" +
            "AddSingleton<CalendarContext>();\n" +
            "AddScoped<CalculationInputBuilder>();\n" +
            "AddScoped<CalculationOutputWriter>();\n" +
            "⚠ Модуль рівня 2 (скрипти Roslyn) НЕ реєструвати: він вмикається лише після " +
            "дозволу ІБ (K-1), і його відсутність не має ламати систему — саме тому " +
            "ICalculationModule резолвиться як колекція, а не як одиничний сервіс.");
}
```
