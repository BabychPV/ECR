# 05g — Скелет: адаптери

> Частина [`05-skeleton.md`](05-skeleton.md).
>
> Два адаптери: Excel (експорт/імпорт) і PI AF (**лише читання**).
> ⛔ Адаптера запису в PI AF **не існує** (`D-44`): система в AF нічого не пише
> — ні даних, ні довідників, ні на перехідний період.

---

## 1. `Ecr.Adapters.Excel`

### `src/Ecr.Adapters.Excel/ExcelExporter.cs`
MODULE: adapters-excel | STAGE: 5
CONTRACT: 02-contracts.md#ports

```csharp
using ClosedXML.Excel;
using Ecr.Application.Ports;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Експорт документа в <c>.xlsx</c> (ClosedXML, MIT).
/// </summary>
/// <remarks>
/// Бюджет — 10 с p95, тому операція фонова, з прогресом (tz/08 §8.2).
/// ⛔ EPPlus 5+ заборонений ліцензійно (noncommercial), тому альтернативи тут
/// немає і шукати її не треба.
/// </remarks>
public sealed class ExcelExporter(
    ICellStore cellStore,
    IMetadataCache metadata,
    StyleMapper styleMapper,
    FormulaTranslator formulaTranslator) : IExcelExporter
{
    /// <inheritdoc />
    public Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) метадані з кешу; дані — пакетно по таблицях, не по комірках;\n" +
            "2) на кожен SheetDef — аркуш книги; порядок за Ordinal;\n" +
            "3) стилі через styleMapper (шрифт, заливка, межі, формат, об'єднання, " +
            "   заморожені області);\n" +
            "4) якщо options.IncludeFormulas — записувати формулу через formulaTranslator, " +
            "   інакше лише значення;\n" +
            "5) порожні комірки не писати; IsEmpty → порожня комірка без формату помилки;\n" +
            "6) обчислені комірки позначати стилем «тільки читання», щоб при зворотному " +
            "   імпорті було видно, що їх правити не можна;\n" +
            "7) віддавати Stream, не байти: документ на 500×60×12 у пам'яті — це десятки МБ.");
}
```

---

### `src/Ecr.Adapters.Excel/FormulaTranslator.cs`
MODULE: adapters-excel | STAGE: 5

```csharp
namespace Ecr.Adapters.Excel;

/// <summary>
/// Транслює наші вирази в синтаксис Excel і назад.
/// </summary>
/// <remarks>
/// Це найпідступніша частина експорту: наша мова адресує рядки за
/// <c>RowKey</c>, Excel — за координатами. Тому потрібен **зворотний мапер
/// координат**, і будувати його треба на етапі експорту, а не «якось потім»
/// (ТЗ §13.5 п.6 — окрема пастка, яку легко недооцінити).
/// </remarks>
public sealed class FormulaTranslator
{
    /// <summary>Наш вираз → формула Excel.</summary>
    /// <param name="expression">Вираз у нашій граматиці.</param>
    /// <param name="coordinates">Мапа <c>(TableDefId, RowKey, ColumnDefId)</c> → адреса комірки Excel.</param>
    public string ToExcel(string expression, IReadOnlyDictionary<(int, string, int), string> coordinates)
        => throw new NotImplementedException(
            "TODO: розібрати вираз нашим парсером; замінити посилання на координати з мапи; " +
            "функції транслювати 1:1 (усі 11 мають Excel-відповідники); " +
            "CONVERT замінити множенням на константу — в Excel такої функції немає, " +
            "і саме тут експорт «з формулами» втрачає частину семантики: це треба сказати " +
            "користувачеві, а не приховати.");

    /// <summary>Формула Excel → наш вираз (для імпорту структури з довільного файлу).</summary>
    public string? FromExcel(string excelFormula, IReadOnlyDictionary<string, (int, string, int)> reverseCoordinates)
        => throw new NotImplementedException(
            "TODO: зворотна трансляція best-effort. Нерозпізнану формулу НЕ вгадувати: " +
            "повернути null і показати користувачеві — інакше імпорт мовчки створить " +
            "неправильне правило обчислення.");
}
```

---

### `src/Ecr.Adapters.Excel/ExcelImporter.cs` і `ImportDiffBuilder.cs`
MODULE: adapters-excel | STAGE: 5

```csharp
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Імпорт із <c>.xlsx</c> — **завжди** через попередній перегляд diff (ФВ-4.3).
/// </summary>
/// <remarks>
/// Імпорт без перегляду — це спосіб непомітно перезаписати чужу роботу.
/// Тому застосування розділене на два кроки, і між ними користувач бачить,
/// що саме зміниться, що конфліктує і що буде відхилено.
/// </remarks>
public sealed class ExcelImporter(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ImportDiffBuilder diffBuilder) : IExcelImporter
{
    /// <inheritdoc />
    public Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) відкрити книгу; зіставити аркуші й колонки з шаблоном за кодами, " +
            "   не за позиціями — інакше зсув колонки в файлі зіпсує дані;\n" +
            "2) структура не відповідає шаблону → ECR-IMP-0422 з переліком розбіжностей;\n" +
            "3) прочитати поточні значення і побудувати diff;\n" +
            "4) прогнати кожну комірку через access — заборонені НЕ застосовувати " +
            "   і показати перелік (ФВ-4.4);\n" +
            "5) обчислені комірки завжди відхиляти (ECR-CELL-4221);\n" +
            "6) зберегти diff під previewToken з обмеженим часом життя.");

    /// <inheritdoc />
    public Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: підняти збережений diff; ПЕРЕВІРИТИ ЗАНОВО версії рядків — між переглядом " +
            "і застосуванням могла статися чужа правка; застосувати через той самий шлях, " +
            "що й batch-PATCH (Origin = 'Import'), щоб аудит і перерахунок працювали однаково.");
}
```

---

## 2. `Ecr.Adapters.PiAf`

### `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs`
MODULE: adapters-piaf | STAGE: 5
CONTRACT: 02-contracts.md#ports

```csharp
using System.Data.Odbc;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI SQL Client (RTQP) — ODBC.
/// </summary>
/// <remarks>
/// Підтверджені факти: RTQP **у продуктиві** (63 land-процедури читають через
/// нього), він **тільки для читання**, **не потребує Kerberos-делегування** і
/// працює через ODBC, не OLE DB (`D-46`). Це основний транспорт для масового
/// читання історії й довідників.
/// </remarks>
public sealed class PiSqlClientDataSource : IExternalDataSource
{
    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiSqlClient;

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прочитати каталог елементів і атрибутів AF через RTQP; повернути дескриптори " +
            "з іменами, типами і UOM. Каталог потрібен конфігуратору, щоб користувач обирав " +
            "зі списку, а не вводив імена руками (ФВ-13.6).");

    /// <inheritdoc />
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: побудувати запит із фільтром по часу; читати через OdbcDataReader ПОТОКОВО, " +
            "не матеріалізуючи все в пам'ять; підключення під СЕРВІСНИМ обліковим записом " +
            "(D-34); секрет брати за іменем із SecretName, ніколи не з конфігу. " +
            "⚠ Адаптер не створює жодних артефактів у чужій БД — жодних вʼюх і таблиць " +
            "в AF-базі: вони не їдуть із застосунком і стають джерелом поломок при " +
            "переїзді середовища (ER-I-01).");
}
```

---

### `src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs`
MODULE: adapters-piaf | STAGE: 5

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI Web API (HTTP).
/// </summary>
/// <remarks>
/// Використовується для того, чого не вміє RTQP. **Методів запису тут немає
/// навмисно** (`D-44`): Web API їх підтримує, але система в AF не пише нічого.
/// </remarks>
public sealed class PiWebApiDataSource(HttpClient http) : IExternalDataSource
{
    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiWebApi;

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: обхід ієрархії елементів через /assetdatabases/{id}/elements; " +
            "збирати атрибути з їхнім UOM.");

    /// <inheritdoc />
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: /streamsets/recorded або /value з фільтром часу; батчі обмеженого розміру; " +
            "ретраї з експоненційним відступом на 5xx і таймаутах; " +
            "часткова відмова батча — це НЕ загальний провал: успішні точки зберегти, " +
            "невдалі повернути в catch-up.");
}
```

---

### `src/Ecr.Adapters.PiAf/CollectionRunner.cs`
MODULE: adapters-piaf | STAGE: 5

```csharp
using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Виконує збір: ідемпотентно, з catch-up і журналом покриття (ФВ-11.3).
/// </summary>
/// <remarks>
/// Обслуговування AF відбуватиметься незалежно від нашої згоди, тому простій
/// джерела має бути **затримкою, а не втратою**. Ознака здоров'я — журнал
/// покриття, а не тиша: система, яка «нічого не повідомляє», і система, яка
/// «нічого не зібрала», ззовні виглядають однаково.
/// </remarks>
public sealed class CollectionRunner(
    IEnumerable<IExternalDataSource> sources,
    SourceUnitConverter unitConverter,
    CatchUpPlanner catchUp,
    Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Виконує збір для сутності джерела.</summary>
    public Task RunAsync(int sourceEntityId, DateTime from, DateTime to, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) створити itg.CollectionRun;\n" +
            "2) обрати адаптер за Transport джерела — вибір транспорту це НАЛАШТУВАННЯ, " +
            "   не гілка коду (ФВ-11.2);\n" +
            "3) читати діапазон; upsert у ext.RawDataPoint за природним ключем " +
            "   (SourceEntityId, SourcePath, Timestamp) — повторний запуск не дублює;\n" +
            "4) ⚠ сирі дані зберігати В ОДИНИЦІ ДЖЕРЕЛА; конверсія — на межі, через " +
            "   unitConverter, із записом у журнал (ФВ-16.9). Інакше повторний перерахунок " +
            "   з архіву дасть інший результат;\n" +
            "5) записати itg.CollectionCoverage — за які інтервали дані є;\n" +
            "6) при відмові джерела: зафіксувати ECR-INT-0503, поставити діапазон у catch-up " +
            "   і ЗАВЕРШИТИСЯ успішно — це затримка, не збій.");
}
```

---

### `src/Ecr.Adapters.PiAf/SourceUnitConverter.cs`
MODULE: adapters-piaf | STAGE: 5

```csharp
namespace Ecr.Adapters.PiAf;

/// <summary>
/// Конверсія одиниць на межі інтеграції.
/// </summary>
/// <remarks>
/// Атрибути PI AF мають власний UOM, і це **найчастіше джерело мовчазних
/// розбіжностей у числах**. Тому мапінг зберігає <c>SourceUnitId</c> і
/// <c>TargetUnitId</c> явно, а несподівана зміна одиниці в джерелі **зупиняє
/// збір**, а не конвертує «як здається» (ФВ-16.9).
/// </remarks>
public sealed class SourceUnitConverter(Ecr.Domain.Services.UnitConverter converter)
{
    /// <summary>Конвертує значення на межі.</summary>
    /// <param name="value">Значення в одиниці джерела.</param>
    /// <param name="declaredSourceUnitId">Одиниця, оголошена в мапінгу.</param>
    /// <param name="actualSourceUnitCode">Одиниця, яку фактично повернуло джерело.</param>
    /// <param name="targetUnitId">Цільова одиниця.</param>
    /// <exception cref="Ecr.Application.Errors.BusinessRuleException">
    /// Фактична одиниця не збігається з оголошеною — <c>ECR-INT-0422</c>.
    /// </exception>
    public decimal Convert(decimal value, int declaredSourceUnitId, string? actualSourceUnitCode, int targetUnitId)
        => throw new NotImplementedException(
            "TODO: якщо actualSourceUnitCode заданий і не збігається з declaredSourceUnitId — " +
            "кинути BusinessRuleException('ECR-INT-0422') і ЗУПИНИТИ збір. " +
            "Мовчазна конверсія «як здається» тут гірша за зупинку: вона дає правдоподібні " +
            "числа, помилку в яких знайдуть через місяць на звірці.");
}
```

---

### `src/Ecr.Adapters.PiAf/CatchUpPlanner.cs`
MODULE: adapters-piaf | STAGE: 5

```csharp
namespace Ecr.Adapters.PiAf;

/// <summary>
/// Планує дозбір пропущених інтервалів за журналом покриття.
/// </summary>
/// <remarks>
/// <c>Watermark</c> у розкладі — **оптимізація, а не стан** (ER-I-03): його
/// втрата не має коштувати даних, тому справжнім джерелом істини є
/// <c>itg.CollectionCoverage</c>.
/// </remarks>
public sealed class CatchUpPlanner(Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Знаходить непокриті інтервали за період lookback.</summary>
    public Task<IReadOnlyList<(DateTime From, DateTime To)>> PlanAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти інтервали з itg.CollectionCoverage, злити суміжні, знайти прогалини " +
            "від notBefore до тепер. Повертати впорядковано від найстаршої прогалини: " +
            "спершу закриваємо давнє, бо саме воно потрібне для звітності.");
}
```

---

### `src/Ecr.Adapters.Excel/DependencyInjection.cs` і `Ecr.Adapters.PiAf/DependencyInjection.cs`
MODULE: adapters | STAGE: 5

```csharp
using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.PiAf;

/// <summary>Реєстрація адаптерів PI AF.</summary>
public static class DependencyInjection
{
    /// <summary>Додає обидва транспорти читання.</summary>
    public static IServiceCollection AddPiAfAdapters(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddScoped<IExternalDataSource, PiSqlClientDataSource>();\n" +
            "AddScoped<IExternalDataSource, PiWebApiDataSource>();  // резолвиться як колекція\n" +
            "AddScoped<CollectionRunner>();\n" +
            "AddScoped<CatchUpPlanner>();\n" +
            "AddScoped<SourceUnitConverter>();\n" +
            "AddHttpClient<PiWebApiDataSource>(...) з таймаутом і politikою ретраїв;\n" +
            "⚠ IExternalDataSink не реєструється — його не існує (D-44).");
}
```
