# 05j — Скелет: інструменти (`tools/`)

> Частина [`05-skeleton.md`](05-skeleton.md).
>
> Три консольні утиліти. **Дві з них — єдині місця, де відкриваються реальні
> дані** (`08-workflow.md` §8): `Ecr.Bootstrap.Excel` і `Ecr.Migration.PiAf`.
> Третя, `Ecr.DataGen`, працює лише з синтетикою і потрібна для гейта Етапу 0.

---

## 1. `tools/Ecr.DataGen`

### `tools/Ecr.DataGen/Ecr.DataGen.csproj`
MODULE: tools-datagen | STAGE: 1

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Ecr.DataGen</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\..\src\Ecr.Infrastructure\Ecr.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>

</Project>
```

---

### `tools/Ecr.DataGen/Program.cs`
MODULE: tools-datagen | STAGE: 1
CONTRACT: 02a-db-schema.md
SCOPE: генерація синтетичного обсягу для гейта BR-07.
NOT IN SCOPE: реальні дані — тут їх немає і бути не може.

```csharp
namespace Ecr.DataGen;

/// <summary>
/// Генератор синтетичного обсягу для гейта Етапу 0.
/// </summary>
/// <remarks>
/// Розподіл знімається **з реального шаблону**, а не рівномірний шум: ~90
/// таблиць на документ, медіана ~30 рядків, хвіст до 471, 7…60 колонок,
/// 12 періодів. Рівномірний розподіл дав би оптимістичні цифри, яких не буде
/// в проді (tz/04 §4.3).
/// </remarks>
internal static class Program
{
    /// <summary>Точка входу.</summary>
    /// <param name="args">
    /// <c>--documents 300 --fill 90 --year 2026 --connection "..."</c>
    /// </param>
    private static Task<int> Main(string[] args)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) розібрати аргументи: documents (100/300/500), fill (35/60/90), year, connection;\n" +
            "2) створити шаблон, проєкт і календар періодів;\n" +
            "3) генерувати документи батчами; Id брати з SEQUENCE одним викликом " +
            "   sp_sequence_get_range на батч;\n" +
            "4) вантажити через SqlBulkCopy — TableRow і CellValue ОДНИМ проходом;\n" +
            "5) заповненість: генерувати рівно fill% комірок, решту НЕ створювати " +
            "   (порожні комірки не матеріалізуються — і це має бути видно в замірі);\n" +
            "6) друкувати прогрес і підсумок: рядків, розмір таблиць після PAGE-стиснення.\n" +
            "⚠ Цільовий сценарій гейта — 300 документів при заповненості 90%: замовник називає " +
            "≥200 на рік, і саме на 300 мають виконуватися всі бюджети.");
}
```

---

### `tools/Ecr.DataGen/DistributionProfile.cs`
MODULE: tools-datagen | STAGE: 1

```csharp
namespace Ecr.DataGen;

/// <summary>
/// Профіль розподілу, знятий із чинного шаблону.
/// </summary>
/// <remarks>
/// Числа не вигадані: вони походять із аналізу реального `.xlsm`
/// (`docs/01-as-is-overview.md`). Змінювати їх без нового аналізу означає
/// міряти не ту систему.
/// </remarks>
public sealed record DistributionProfile
{
    /// <summary>Таблиць на документ.</summary>
    public int TablesPerDocument { get; init; } = 90;

    /// <summary>Медіана рядків у таблиці.</summary>
    public int MedianRowsPerTable { get; init; } = 30;

    /// <summary>Максимум рядків (найважча таблиця чинного шаблону).</summary>
    public int MaxRowsPerTable { get; init; } = 471;

    /// <summary>Мінімум колонок.</summary>
    public int MinColumns { get; init; } = 7;

    /// <summary>Максимум колонок.</summary>
    public int MaxColumns { get; init; } = 60;

    /// <summary>Періодів у році.</summary>
    public int PeriodsPerYear { get; init; } = 12;

    /// <summary>Частка заповнених комірок, %.</summary>
    public int FillPercent { get; init; } = 90;
}
```

---

### `tools/Ecr.DataGen/GateBenchmark.cs`
MODULE: tools-datagen | STAGE: 1

```csharp
namespace Ecr.DataGen;

/// <summary>
/// Заміри гейта BR-07 — шість критеріїв із <c>tz/04</c> §4.3.
/// </summary>
/// <remarks>
/// ⚠ Найважливіший і найлегший для пропуску — замір №6: **125 RPS в одну
/// партицію**. Пік у ECR не розподілений: усі користувачі в останні дні
/// періоду б'ють в один період. Рівномірне навантаження на 12 партицій
/// нічого не доводить.
/// </remarks>
public sealed class GateBenchmark
{
    /// <summary>Виконує всі заміри і друкує звіт.</summary>
    public Task<GateResult> RunAsync(string connectionString, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — шість замірів:\n" +
            "1) ReadSliceAsync 500×60        → ціль p95 < 600 мс\n" +
            "2) ApplyAsync 100 комірок       → ціль p95 < 150 мс\n" +
            "3) агрегація по періоду (rollup) → < 500 мс на вже матеріалізованих значеннях;\n" +
            "   ⛔ індексована вʼюха неприпустима (ФВ-0.3)\n" +
            "4) повний цикл архівації року   → без блокування робочих запитів\n" +
            "5) розмір doc.CellValue після PAGE → оцінка ГБ/рік для sizing\n" +
            "6) 125 RPS в ОДНУ партицію, 15 хв → без ескалації блокувань, p95 у бюджеті\n" +
            "Заміри виконувати в режимі EditionMode = Standard: бюджет має витримуватися " +
            "на базовій редакції, Enterprise дає запас, а не умову (АРХ-7).");
}

/// <summary>Результат гейта.</summary>
/// <param name="Passed">Чи пройдено всі шість критеріїв.</param>
/// <param name="Measurements">Заміри: назва → значення.</param>
/// <param name="Failures">Критерії, які не пройдено, з фактичними числами.</param>
public sealed record GateResult(
    bool Passed,
    IReadOnlyDictionary<string, double> Measurements,
    IReadOnlyList<string> Failures);
```

---

## 2. `tools/Ecr.Bootstrap.Excel`

### `tools/Ecr.Bootstrap.Excel/Program.cs`
MODULE: tools-bootstrap | STAGE: 5
CONTRACT: 02a-db-schema.md#cfg
SCOPE: `.xlsm` + VBA → `cfg.*`. **Працює з реальними даними** — задача
`bootstrap-verify` Етапу 5.

```csharp
namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Заповнює <c>cfg.*</c> зі структури чинного Excel-шаблону і VBA.
/// </summary>
/// <remarks>
/// ⚠ Це **напівавтоматичний** процес, і закладати треба тижні, а не дні
/// (`tz/09` §9.4). Причина: <c>MapAttributes</c> у 19 модулях VBA містить
/// <c>Select Case templateRow</c> з різними наборами діапазонів, і семантичний
/// розбір VBA ненадійний. План: витягти автоматично максимум → **ручна звірка
/// кожної з ~90 таблиць** проти реальних Event Frames у AF → фіксація мапінгу
/// в <c>ext.Legacy*Mapping</c>.
///
/// Критерій приймання — **round-trip**: імпортована структура експортується
/// назад у <c>.xlsx</c> і порівнюється з оригіналом. Розбіжність — це або
/// дефект імпортера, або невиявлена особливість шаблону; обидва варіанти треба
/// закрити до Етапу 1.
/// </remarks>
internal static class Program
{
    /// <summary>Точка входу.</summary>
    /// <param name="args">
    /// <c>--workbook "path.xlsm" --vba "vba-files/" --out-report report.md --dry-run</c>
    /// </param>
    private static Task<int> Main(string[] args)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) прочитати книгу ClosedXML: аркуші → SheetDef, іменовані діапазони і шапки → " +
            "   TableDef/ColumnDef, рядки → RowDef;\n" +
            "2) розібрати VBA: Configuration.bas (словники таблиць), MapAttributes у 19 модулях " +
            "   (позиції полів у ;-рядку → ext.LegacyColumnMapping.LegacyFieldIndex);\n" +
            "3) формули Excel → наші вирази через FormulaTranslator; НЕРОЗПІЗНАНЕ не вгадувати — " +
            "   у звіт для ручного рішення;\n" +
            "4) одиниці витягти із заголовків ('т/рік', 'г/с', 'м³') і UOM атрибутів AF; " +
            "   кожну нерозпізнану — у звіт, а не заводити здогадкою (ФВ-16.12);\n" +
            "5) --dry-run: нічого не писати, лише звіт;\n" +
            "6) звіт обов'язково містить: що імпортовано автоматично, що потребує ручного " +
            "   рішення, і чек-лист ~90 таблиць для звірки.");
}
```

---

### `tools/Ecr.Bootstrap.Excel/VbaMapAttributesParser.cs`
MODULE: tools-bootstrap | STAGE: 5

```csharp
namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Витягує позиції полів у <c>;</c>-рядку з процедур <c>MapAttributes</c>.
/// </summary>
/// <remarks>
/// Порядок полів **індивідуальний для кожної з ~90 таблиць** — саме він
/// потрібен для валідації міграції історії. Парсер робить best-effort:
/// усе, що не розібралося однозначно, іде у звіт, а не в БД.
/// </remarks>
public sealed class VbaMapAttributesParser
{
    /// <summary>Розбирає модуль.</summary>
    /// <param name="vbaSource">Текст модуля <c>.bas</c>.</param>
    /// <returns>Мапінги і перелік місць, які треба звірити руками.</returns>
    public (IReadOnlyList<LegacyFieldMapping> Mappings, IReadOnlyList<string> ManualReview) Parse(string vbaSource)
        => throw new NotImplementedException(
            "TODO: знайти процедури MapAttributes; розібрати Select Case templateRow; " +
            "для кожної гілки зібрати послідовність полів. " +
            "Умовні гілки, обчислювані індекси і все, що не є простим переліком, — " +
            "у ManualReview. Вгадувати заборонено: помилка в порядку полів дає " +
            "правдоподібні, але неправильні дані при міграції історії.");
}

/// <summary>Позиція поля в legacy-рядку.</summary>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="FieldIndex">Позиція в <c>;</c>-рядку, 0-based.</param>
public sealed record LegacyFieldMapping(string TableCode, string ColumnCode, int FieldIndex);
```

---

## 3. `tools/Ecr.Migration.PiAf`

### `tools/Ecr.Migration.PiAf/Program.cs`
MODULE: tools-migration | STAGE: 5
SCOPE: історія з PI AF → `doc.*`. **Працює з реальними даними** — задача
`history-migrate` Етапу 5.

```csharp
namespace Ecr.Migration.PiAf;

/// <summary>
/// Переносить історичні дані з PI AF у <c>doc.*</c>.
/// </summary>
/// <remarks>
/// Критерій приймання — **побайтна валідація**: відтворений із наших даних
/// <c>;</c>-рядок має збігатися з тим, що лежить в AF. Без цього твердження
/// «історію перенесено» нічим не підтвердити.
/// </remarks>
internal static class Program
{
    /// <summary>Точка входу.</summary>
    /// <param name="args"><c>--source-id 1 --year 2025 --validate-only</c></param>
    private static Task<int> Main(string[] args)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) читати Event Frames через IExternalDataSource (RTQP для масового читання);\n" +
            "2) розібрати ;-рядки за ext.LegacyColumnMapping.LegacyFieldIndex;\n" +
            "3) зіставити рядки за RowKey через ext.LegacyRowMapping.AfAttributeName;\n" +
            "4) зіставлення записів реєстрів — ЗА БІЗНЕС-КЛЮЧЕМ; зовнішній GUID зберігати " +
            "   ПІСЛЯ зіставлення в dic.RegistryExternalKey, а не замість нього: " +
            "   GUID не переживає перенесення між AF-серверами (ER-I-05);\n" +
            "5) вантажити SqlBulkCopy батчами;\n" +
            "6) ВАЛІДАЦІЯ: для кожного перенесеного рядка відтворити ;-рядок через " +
            "   LegacyRowSerializer і порівняти побайтно з оригіналом; розбіжності — у звіт;\n" +
            "7) --validate-only: не писати, лише звірити вже перенесене.");
}
```

---

### `tools/Ecr.Migration.PiAf/LegacyRowSerializer.cs`
MODULE: tools-migration | STAGE: 5

```csharp
namespace Ecr.Migration.PiAf;

/// <summary>
/// Відтворює legacy-формат <c>Attribute_XXXX = "1;7001001;ITEM;…"</c>.
/// </summary>
/// <remarks>
/// ⚠ Це **утиліта валідації міграції**, а не інтеграційний адаптер
/// (`R-A5`, `D-44`). Запису в PI AF немає взагалі, і цей клас не має
/// перетворитися на нього: єдиний його споживач — звірка перенесених даних.
///
/// Форматування значень має збігатися з VBA **побайтно**: інакше валідація
/// покаже розбіжності там, де даних не зіпсовано, і час піде на пошук
/// неіснуючої проблеми.
/// </remarks>
public sealed class LegacyRowSerializer
{
    /// <summary>Складає <c>;</c>-рядок із комірок рядка.</summary>
    /// <param name="cells">Значення за кодом колонки.</param>
    /// <param name="fieldOrder">Порядок полів із <c>ext.LegacyColumnMapping</c>.</param>
    public string Serialize(IReadOnlyDictionary<string, object?> cells, IReadOnlyList<string> fieldOrder)
        => throw new NotImplementedException(
            "TODO: скласти значення через ';' у порядку fieldOrder; форматувати ТАК САМО, " +
            "як VBA: числа — інваріантно з крапкою і без розділювачів тисяч, порожнє значення — " +
            "'NULL' (саме рядок, не порожньо), булеве — '1'/'0', дати — у чинному форматі. " +
            "Завершальний ';' зберігати, якщо він є в оригіналі.");

    /// <summary>Порівнює відтворений рядок з оригіналом.</summary>
    /// <returns>Опис розбіжності або <c>null</c>, якщо збігається.</returns>
    public string? Compare(string reconstructed, string original)
        => throw new NotImplementedException(
            "TODO: порівняти посимвольно; при розбіжності повернути позицію, очікуване і " +
            "фактичне значення поля — саме поля, а не символу: інакше звіт нечитабельний.");
}
```

---

## 4. Спільні вимоги до інструментів

1. **Консольні застосунки**, не бібліотеки: запускаються вручну, за чек-листом,
   а не з коду системи.
2. **`--dry-run` обов'язковий** у `Bootstrap` і `Migration`: перший запуск
   завжди має бути безпечним.
3. **Звіт у markdown** — щоб результат можна було вкласти в `progress.md`
   і показати людині.
4. **Логування в консоль і у файл**; секрети не виводяться.
5. **Ідемпотентність**: повторний запуск на тих самих даних не створює
   дублікатів.
6. **`Ecr.DataGen` не має доступу до реальних даних** узагалі — він працює
   лише з синтетикою.
