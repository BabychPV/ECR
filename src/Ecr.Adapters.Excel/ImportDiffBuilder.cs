using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Будує diff між книгою <c>.xlsx</c> і поточними даними документа.
/// </summary>
/// <remarks>
/// ⚠ Виділений з <see cref="ExcelImporter"/> навмисно: порівняння — це те, що
/// має бути перевірним без книги, без бази і без прав. Обчислення diff і його
/// застосування, змішані в одному методі, дають код, у якому неможливо
/// перевірити, що саме буде відхилено, не застосувавши це.
/// </remarks>
public sealed class ImportDiffBuilder
{
    /// <summary>
    /// Скільки змін має сенс показати в одному перегляді.
    /// </summary>
    /// <remarks>
    /// ⚠ Не оптимізація. Десять тисяч змін — це не імпорт правок, а підміна
    /// документа: переглянути такий diff людина не може, а «підтвердити не
    /// дивлячись» — саме те, від чого перегляд і захищає (ФВ-4.3).
    /// </remarks>
    public const int MaxChanges = 5_000;

    /// <summary>Порівнює блок книги з поточними даними.</summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="block">Блок таблиці з карти книги.</param>
    /// <param name="periodKey">Період книги; він же ключ партиції.</param>
    /// <param name="table">Опис таблиці зі знімка.</param>
    /// <param name="decisions">Рішення про доступ на комірки зрізу.</param>
    /// <param name="lookups">Коди записів довідників: <c>RegistryDefId</c> → код → <c>Id</c>.</param>
    /// <param name="rowIds">Ідентифікатори рядків цієї таблиці: <c>RowKey</c> → <c>TableRow.Id</c>.</param>
    /// <param name="versions">Версії рядків цієї таблиці: <c>RowKey</c> → hex <c>rowversion</c>.</param>
    /// <param name="current">Поточний зріз комірок цієї таблиці.</param>
    /// <remarks>
    /// ⛔ Q-168 (аудит фази 2, продуктивність). Метод БІЛЬШЕ НЕ ходить у базу
    /// сам — <paramref name="rowIds"/>, <paramref name="versions"/> і
    /// <paramref name="current"/> викликач читає ОДНИМ пакетним запитом на
    /// ВСІ таблиці книги (<c>ExcelImporter.PreviewAsync</c>), а не по одному
    /// на кожну з ~90. Це узгоджує клас із власним призначенням, названим у
    /// коментарі типу: порівняння перевірне БЕЗ бази.
    /// </remarks>
    public TableDiff Build(
        IXLWorksheet worksheet,
        ExcelTableBlock block,
        int periodKey,
        TableDef table,
        IReadOnlyDictionary<CellAddress, EditDecision> decisions,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> lookups,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<CellRecord> current)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decisions);

        var period = new PeriodKey(periodKey);

        var byRowId = rowIds.ToDictionary(p => p.Value, p => p.Key);

        // ⚠ Комірки рядків поза переліком ключів відкидаються, а не зводяться
        // до спільного ключа з порожнім RowKey: два такі рядки дали б
        // однаковий ключ і `ToDictionary` упав би на дублікаті — посеред
        // перегляду імпорту, на даних, які виглядають звичайними.
        var values = current
            .Where(c => byRowId.ContainsKey(c.Address.TableRowId))
            .ToDictionary(c => (byRowId[c.Address.TableRowId], c.Address.ColumnDefId));

        var columnsById = table.Columns.ToDictionary(c => c.Id);

        var changes = new List<ImportChange>();
        var rejected = new List<ImportRejection>();

        foreach (var row in block.Rows)
        {
            foreach (var column in block.Columns)
            {
                if (changes.Count >= MaxChanges)
                {
                    break;
                }

                if (!columnsById.TryGetValue(column.ColumnDefId, out var definition))
                {
                    continue;
                }

                var cell = worksheet.Cell(row.Number, column.Number);

                // ⛔ `V-10`. Формула в обчислюваній комірці — це те, що туди
                // поклав САМ експорт (`ExcelExporter.WriteFormulas`), а не
                // значення користувача: система однаково порахує комірку сама.
                // Порівнювати її кешований результат (а Excel перерахує його,
                // щойно користувач змінить вхідну комірку поруч) означало б
                // відхиляти кожну книгу, у якій змінили хоч одне вхідне число, —
                // і з «усе або нічого» Apply не ставав доступним ніколи.
                if (IsCalculated(definition) && cell.HasFormula)
                {
                    continue;
                }

                var incoming = Read(cell, definition, lookups);
                var existing = values.GetValueOrDefault((row.RowKey, column.ColumnDefId))?.Value;

                // ⛔ `V-10`. Обчислювані й read-only комірки порівнюються з
                // поточним значенням ТАК САМО, як вхідні, і відхиляються
                // (нижче) лише тоді, коли користувач їх ЗМІНИВ. Незмінена
                // обчислювана комірка експортованої книги пропускається мовчки:
                // вона не правка, а копія того, що система й так тримає.
                if (Same(incoming, existing, definition))
                {
                    continue;
                }

                // ⛔ Обчислена комірка відхиляється ЗАВЖДИ і першою — навіть
                // якщо права дозволяють. Записане поверх формули значення
                // зникне при найближчому перерахунку, і користувач вирішить,
                // що система «загубила» його правку (ECR-CELL-4221).
                // ⛔ Обчислюваність береться з ЖИВОГО `ColumnDef`, а не з карти
                // воркбука (аудит 2026-09-16, §8.1). `column.IsCalculated`
                // зафіксовано на момент ЕКСПОРТУ; між експортом і повторним
                // імпортом адмін міг перепублікувати шаблон і зробити раніше
                // редаговану колонку обчислюваною — і прев'ю показувало б зміну
                // як застосовну, а `ApplyAsync` падав би пізніше.
                if (IsCalculated(definition))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-CELL-4221",
                        "Комірка обчислюється системою: значення з файлу не застосовується."));

                    continue;
                }

                if (!rowIds.TryGetValue(row.RowKey, out var rowId))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ROW-0404",
                        "Рядка з таким ключем у документі немає: імпорт рядків не створює."));

                    continue;
                }

                var address = new CellAddress(period, rowId, column.ColumnDefId);

                // ⚠ Заборонені комірки НЕ застосовуються і показуються
                // переліком (ФВ-4.4). Мовчазне пропускання виглядало б як
                // успішний імпорт, після якого частина чисел не змінилася.
                if (decisions.TryGetValue(address, out var decision) && !decision.IsAllowed)
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ACCS-0403",
                        decision.Detail ?? $"Змінювати комірку не дозволено: {decision.Reason}."));

                    continue;
                }

                // ⛔ Ціла частина понад межу сховища (`decimal(34,16)`, 18
                // розрядів) — відмова в ПРЕВ'Ю, а не зміна. Інакше вона
                // доходила б до застосування, і там `CellValueReader` відхиляв
                // увесь пакет — користувач дізнавався б про одну комірку ціною
                // відмови всієї книги, вже після того, як погодився на прев'ю.
                // На відміну від хвоста після коми (нормалізується вище), тут
                // округлювати нема до чого: число просто не вміщується.
                if (incoming is decimal number && !CellValueReader.IntegerPartFits(number))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, CellValueReader.TypeMismatch,
                        $"Число має понад {CellValueReader.StorageIntegerDigits} розрядів до коми: сховище його не вмістить."));

                    continue;
                }

                changes.Add(new ImportChange(row.RowKey, column.Code, Display(existing, definition), incoming));
            }
        }

        return new TableDiff(block.TableInstanceId, periodKey, changes, rejected, versions);
    }

    /// <summary>Читає значення з книги у формі, придатній для <c>PatchCell</c>.</summary>
    /// <remarks>
    /// ⚠ Тип диктує <c>ColumnDef</c>, а не те, чим Excel вважає вміст комірки.
    /// Excel радо віддає число як текст і навпаки, і довіра до нього
    /// перетворює числову колонку на текстову мовчки.
    /// </remarks>
    private static object? Read(
        IXLCell cell,
        ColumnDef definition,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> lookups)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        var text = cell.GetString().Trim();

        switch (definition.DataType)
        {
            case CellDataType.Int:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : text;

            // ⛔ `V-10`: обчислювані колонки (`Formula`, `Calculated`) тримають
            // ЧИСЛО (`ValueNumeric`) і експортуються числом — і читаються так
            // само. Доти вони падали в `default` і читалися текстом, тож
            // `Same()` порівнював «10» з `ValueString`, якого в них немає, і
            // КОЖНА непорожня обчислювана комірка незміненої книги ставала
            // відмовою.
            case CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated:
                // ⛔ `U-23`: двійковий хвіст Excel нормалізується ТУТ, явно і
                // до прев'ю, а не мовчки в сховищі. `0.1 + 0.2` в аркуші — це
                // `0.30000000000000004` (17 знаків), а сховище тримає 16
                // (`CellValueReader.StorageScale`). Сервер ручне введення з
                // таким хвостом відхиляє, тож без цього кроку один такий
                // осередок валив би застосування всієї книги. Округлюється
                // лише до масштабу СХОВИЩА — тобто рівно те, що сховище однаково
                // відкинуло б; оголошений масштаб колонки тут не застосовується
                // (його порушення лишається відмовою). Округлене значення
                // видно в прев'ю як «нове», і воно ж — те, що буде записано й
                // потрапить у журнал.
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? decimal.Round(number, CellValueReader.StorageScale, MidpointRounding.AwayFromZero)
                    : text;

            case CellDataType.Bool:
                return bool.TryParse(text, out var flag) ? flag : text;

            case CellDataType.Date:
                // ⛔ Розбір тексту — через `CellDateParser`, а не голий
                // `DateTime.TryParse(…, InvariantCulture, …)` (аудит
                // 2026-09-16, §8.2): InvariantCulture читає `M.d.yyyy`, тож
                // `"1.4.2024"` ставало 4 СІЧНЯ, а не 1 квітня. Українець, що
                // вводить дату в природному порядку `d.MM.yyyy` (звичне при
                // копіюванні або ручному вводі в не-Excel-нативну Date-комірку),
                // отримував тихо неправильну дату без попередження — а це дата
                // виміру, що визначає період звітності. Те саме виправлено в
                // `CellValueReader`: один розбір на обидва шляхи введення.
                return cell.TryGetValue(out DateTime date)
                    ? date
                    : Ecr.Domain.Services.CellDateParser.TryParse(text, out var parsed)
                        ? parsed
                        : text;

            case CellDataType.Lookup:
                // Код повертається в ідентифікатор тут: далі по шляху запису
                // код нічого не означає, а нерозпізнаний код має лишитися
                // видимим, а не перетворитися на нуль.
                //
                // ⛔ Довідник береться з ЖИВОГО `ColumnDef`, не з карти воркбука
                // (аудит §8.1). Застарілий `column.LookupRegistryDefId` із
                // моменту експорту, що випадково збігся з ІНШИМ довідником у
                // знімку, тихо резолвив введений користувачем код у сутність
                // ЧУЖОГО довідника — без помилки, з неправильними даними в базі.
                return definition.LookupRegistryDefId is { } registryId
                       && lookups.TryGetValue(registryId, out var entries)
                       && entries.TryGetValue(text, out var entryId)
                    ? entryId
                    : text;

            case CellDataType.Unit:
                // ⛔ Явна гілка Unit (аудит 2026-09-16, §8.3). Unit-значення
                // живе в `ValueUnitId` — число, — а без цієї гілки воно падало в
                // `default` і читалося ТЕКСТОМ. Разом із такою ж прогалиною в
                // `Same()` це давало «змінено» на КОЖНІЙ непорожній Unit-комірці
                // кожного імпорту, навіть при повторному імпорті незмінного
                // експорту: прев'ю засмічувалося, і довіряти йому ставало
                // неможливо.
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitId)
                    ? unitId
                    : text;

            default:
                return text;
        }
    }

    /// <summary>Чи рахує комірки цієї колонки система — за ЖИВИМ визначенням.</summary>
    /// <remarks>
    /// ⚠ Те саме правило, що в <c>ExcelExporter.IsCalculated</c>, і навмисно те
    /// саме: прев'ю різниці й експорт мусять однаково відповідати на питання
    /// «цю комірку можна редагувати». Різниця між ними — це або відхилена
    /// правка, яку користувач вважав застосовною, або навпаки.
    /// </remarks>
    private static bool IsCalculated(ColumnDef column)
        => column.DataType is CellDataType.Formula or CellDataType.Calculated || column.IsReadOnly;

    /// <summary>Чи збігається значення з файлу з тим, що вже записано.</summary>
    /// <remarks>
    /// ⛔ `V-10`. Порівнюється з <see cref="Current"/> — тим самим значенням, яке
    /// експорт кладе в книгу (<c>ExcelExporter.WriteValue</c> бере поле ЗА ТИПОМ
    /// колонки), а не з усім <see cref="CellValueData"/>. Інакше комірка, що
    /// в базі непорожня, а в книзі порожня (порожній рядок <c>''</c>, або число в
    /// колонці дати), давала «зміну» на незміненій книзі — фантом
    /// <c>R4 C1 '' → —</c> на DOC-000001.
    /// </remarks>
    private static bool Same(object? incoming, CellValueData? existing, ColumnDef definition)
    {
        var current = Current(existing, definition);

        if (incoming is string { Length: 0 })
        {
            incoming = null;
        }

        if (current is null || incoming is null)
        {
            return current is null && incoming is null;
        }

        return definition.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated =>
                current is decimal number
                && (incoming is decimal d ? number == d : incoming is int i && number == i),
            CellDataType.Bool => incoming is bool b && current is bool flag && flag == b,
            CellDataType.Date => incoming is DateTime t && current is DateTime date && date == t,
            CellDataType.Lookup => incoming is long id && current is long entry && entry == id,

            // ⛔ Unit порівнюється за `ValueUnitId` (аудит §8.3). Без цієї гілки
            // порівняння йшло через `ValueString`, який для Unit-комірки
            // ЗАВЖДИ `null` — тож `Same()` повертав `false` для будь-якої
            // непорожньої Unit-комірки, і кожна з них позначалася зміненою.
            CellDataType.Unit => incoming is int unitId && current is int unit && unit == unitId,

            _ => string.Equals(current as string, incoming as string, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Поточне значення комірки в тій формі, у якій його бачить книга: поле за
    /// ТИПОМ колонки; порожній рядок і відсутнє поле — <c>null</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Порожній рядок і відсутність значення — одне й те саме для людини, і
    /// Excel не вміє їх розрізнити взагалі: порожня комірка книги повертається
    /// як «нічого», а не як <c>''</c>.
    /// </remarks>
    private static object? Current(CellValueData? value, ColumnDef definition)
    {
        if (value is null || value.IsEmpty)
        {
            return null;
        }

        return definition.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated
                => value.ValueNumeric,
            CellDataType.Bool => value.ValueBool,
            CellDataType.Date => value.ValueDate,
            CellDataType.Lookup => value.ValueRegistryEntryId,
            CellDataType.Unit => value.ValueUnitId,
            _ => string.IsNullOrEmpty(value.ValueString) ? null : value.ValueString,
        };
    }

    /// <summary>Поточне значення у вигляді, придатному для показу в переліку змін.</summary>
    private static object? Display(CellValueData? value, ColumnDef definition) => Current(value, definition);
}

/// <summary>Diff однієї таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Changes">Що зміниться.</param>
/// <param name="Rejected">Що відхилено і чому.</param>
/// <param name="RowVersions">
/// Версії рядків на момент перегляду — ними перевіряється, чи не змінив
/// хтось дані між переглядом і застосуванням.
/// </param>
public sealed record TableDiff(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ImportChange> Changes,
    IReadOnlyList<ImportRejection> Rejected,
    IReadOnlyDictionary<string, string> RowVersions);
