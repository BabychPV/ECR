using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Localization;
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
    /// <param name="canReadColumn">
    /// Чи бачить той, хто імпортує, колонку (<c>DocumentReadScope.CanReadColumn</c>, S6);
    /// <c>null</c> — бачить усі.
    /// </param>
    /// <param name="culture">
    /// Культура користувача для числа, набраного в книзі ТЕКСТОМ (рішення
    /// 2026-09-29, <see cref="NumberCulture"/>); <c>null</c> — Invariant.
    /// Числові комірки Excel читаються числом і від культури не залежать.
    /// </param>
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
        IReadOnlyList<CellRecord> current,
        Func<int, bool>? canReadColumn = null,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(worksheet);

        culture ??= CultureInfo.InvariantCulture;
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

                // ⚠ P3: адреса комірки книги — у КОЖНІЙ відмові, не лише поза
                // рядками (`V-10`). Ключ рядка `R17` людина в Excel не знайде,
                // адресу `F23` — одним переходом.
                var excelCell = cell.Address.ToString();

                // ⛔ S6 (ФВ-6.6): колонка, якої користувач не бачить, — ПЕРШОЮ і
                // без жодного погляду на її поточне значення. Далі йде
                // порівняння з ним (`Same`), і «незмінена — пропуск, змінена —
                // відмова» відповідало на питання «чи дорівнює приховане число
                // тому, що я вписав у книгу». Тепер відповідь залежить лише від
                // книги: порожньо — нічого, щось вписано — відмова правами
                // (записати в приховану колонку однаково не можна).
                if (canReadColumn is not null && !canReadColumn(definition.Id))
                {
                    if (!cell.IsEmpty() && cell.GetString().Trim().Length > 0)
                    {
                        rejected.Add(new ImportRejection(
                            row.RowKey, column.Code, "ECR-ACCS-0403",
                            $"Editing the cell is not allowed: {EditDenyReason.NoGrant}.",
                            table.Code, table.NameL10n, ImportMessageKeys.Denied(EditDenyReason.NoGrant), excelCell));
                    }

                    continue;
                }

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

                var incoming = Read(cell, definition, lookups, culture);
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

                // ⚠ P1: значення оверлея — decimal до 16 знаків, а Excel зберігає
                // double (~15 значущих цифр): незмінена книга дала б «зміну» на
                // останніх розрядах. Для обчислюваної колонки рівність — з
                // відносним допуском 1e-12; справжня правка (навіть 1 одиниця
                // останнього видимого розряду) значно більша.
                if (IsCalculated(definition)
                    && incoming is decimal inNumber
                    && Current(existing, definition) is decimal calcNumber
                    && Math.Abs(inNumber - calcNumber) <= 1e-12m * Math.Max(1m, Math.Abs(calcNumber)))
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
                //
                // ✎ P3: причин розбіжності дві, і людині вони кажуть протилежне.
                // Комірка книги збігається з відбитком експорту — її не чіпали,
                // число змінила система (перерахунок після вивантаження): книга
                // застаріла, правити в ній нічого. Інакше — правку вніс користувач.
                if (IsCalculated(definition))
                {
                    var stale = CalculatedCellFingerprint.Unchanged(row, block.Columns, column, cell);

                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-CELL-4221",
                        stale
                            ? "The cell is calculated by the system and was recalculated after export: the workbook is out of date."
                            : "The cell is calculated by the system: the value from the file is not applied.",
                        table.Code, table.NameL10n,
                        stale ? ImportMessageKeys.CalculatedStale : ImportMessageKeys.Calculated,
                        excelCell));

                    continue;
                }

                // ✎ ФВ-9.16b (D-109): імпорт `.xlsx` ОКРУГЛЯЄ до `ColumnDef.Scale`
                // (`AwayFromZero`), а не відмовляє, як ручне введення й `PATCH`.
                // Округлюється в ПРЕВ'Ю: застосування бере `NewValue` з плану, тож
                // «показано» і «записано» — те саме число, і `PATCH` приймає його
                // (`ColumnDef.ValidateValue`, п. 7, бачить уже кругле). Порівняння
                // з наявним — двічі: до округлення (незмінена книга з історичним
                // значенням понад Scale не стає «зміною») і після (`1.234` → `1.23`
                // при наявному `1.23` — не зміна). Обчислювані відсіяні вище.
                var fromFile = incoming;
                incoming = RoundToColumnScale(incoming, definition);

                // ✎ ФВ-9.16b: округлення видно в перегляді — людина має знати, що
                // записано не те число, яке стоїть у її книзі. `1.230` → `1.23`
                // числом не змінюється, тож позначки не дає.
                var roundedFrom = fromFile is decimal before && incoming is decimal after && before != after
                    ? fromFile
                    : null;

                if (Same(incoming, existing, definition))
                {
                    continue;
                }

                if (!rowIds.TryGetValue(row.RowKey, out var rowId))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ROW-0404",
                        "The document has no row with this key: import does not create rows.",
                        table.Code, table.NameL10n, ImportMessageKeys.NoRow, excelCell));

                    continue;
                }

                var address = new CellAddress(period, rowId, column.ColumnDefId);

                // ⚠ Заборонені комірки НЕ застосовуються і показуються
                // переліком (ФВ-4.4). Мовчазне пропускання виглядало б як
                // успішний імпорт, після якого частина чисел не змінилася.
                //
                // ⛔ F-30: діагностика — англійською і з причиною-кодом, без
                // `decision.Detail`. Та буває готовим українським реченням
                // («Сеанс симуляції користувача …»), і саме воно їхало в
                // `message` відповіді поруч із ключем. Людині текст дає ключ
                // (`deny.<причина>` — той самий, що в підказці сітки).
                if (decisions.TryGetValue(address, out var decision) && !decision.IsAllowed)
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ACCS-0403",
                        $"Editing the cell is not allowed: {decision.Reason}.",
                        table.Code, table.NameL10n, ImportMessageKeys.Denied(decision.Reason), excelCell));

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
                        $"The number has more than {CellValueReader.StorageIntegerDigits} digits before the decimal point: storage cannot hold it.",
                        table.Code, table.NameL10n, ImportMessageKeys.IntegerDigits, excelCell));

                    continue;
                }

                // ⛔ ФВ-9.16b: точність — ПІСЛЯ округлення до `Scale`, тим самим
                // правилом, що п. 7 `ColumnDef.ValidateValue` на застосуванні.
                // Округлення може додати розряд (`99.999` → `100.00` при `(4,2)`),
                // і таке число доходило б до `PATCH`, що відхиляв усю книгу вже
                // після погодженого перегляду. Відмова — тут, однією коміркою.
                if (incoming is decimal fitted
                    && definition.DataType == CellDataType.Decimal
                    && !definition.FitsPrecision(fitted))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, CellValueReader.TypeMismatch,
                        $"The number does not fit the column precision ({definition.Precision}, scale {definition.Scale}) after rounding to the column scale.",
                        table.Code, table.NameL10n, ImportMessageKeys.Precision, excelCell));

                    continue;
                }

                // ⛔ F-06: тип перевіряє ТОЙ САМИЙ читач, що й запис
                // (`CellValueReader.Read` у `PatchCellsHandler`). Доти `abc` у
                // числовій колонці ставав звичайною зміною, Apply був активний, а
                // застосування відповідало 422 «expects a number» — на всю
                // книгу, вже після того, як людина погодилася на перегляд.
                //
                // ⛔ `C1`: рядок у числовій колонці — це те, що `ReadNumber`
                // свідомо НЕ прочитав числом (напр. «1,234» у en-US чи
                // «1,23,4»). Відмова з діагностикою ставиться тут, тим самим
                // ключем; прийняте число (напр. «1,234.5» у en-US, «1 234,5» у
                // ru) іде далі вже `decimal`, тож застосування (зокрема у фоновій
                // задачі) культури не потребує.
                if (incoming is string raw && IsNumeric(definition))
                {
                    var ambiguous = CultureNumberReader.Read(raw, culture).Kind == NumberTextKind.Ambiguous;

                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, CellValueReader.TypeMismatch,
                        ambiguous
                            ? "The value does not match the column type: ambiguous separator, the comma may be thousands or decimal."
                            : "The value does not match the column type.",
                        table.Code, table.NameL10n, ImportMessageKeys.ExpectsNumber, excelCell));

                    continue;
                }

                if (TypeMismatch(incoming, definition, culture) is { } mismatch)
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, CellValueReader.TypeMismatch, mismatch.Message,
                        table.Code, table.NameL10n, mismatch.MessageKey, excelCell));

                    continue;
                }

                changes.Add(new ImportChange(
                    row.RowKey, column.Code, Display(existing, definition), incoming, table.Code, table.NameL10n,
                    roundedFrom));
            }
        }

        return new TableDiff(block.TableInstanceId, periodKey, changes, rejected, versions);
    }

    /// <summary>
    /// Відмова читача запису для значення з книги; <c>null</c> — значення
    /// ляже в комірку.
    /// </summary>
    /// <remarks>
    /// ⚠ Ключ відмови — ІМПОРТНИЙ (<see cref="ImportMessageKeys.TypeMismatch"/>):
    /// ключ читача несе в тексті «Column "{columnCode}"», а рядок переліку
    /// перегляду вже має колонку окремим стовпцем і підстановок не передає.
    /// </remarks>
    private static (string Message, string? MessageKey)? TypeMismatch(
        object? incoming, ColumnDef definition, CultureInfo culture)
    {
        try
        {
            _ = CellValueReader.Read(incoming, definition, culture);

            return null;
        }
        catch (Ecr.Application.Errors.BusinessRuleException error)
            when (string.Equals(error.ErrorCode, CellValueReader.TypeMismatch, StringComparison.Ordinal))
        {
            var readerKey = error.Details?.GetValueOrDefault("messageKey") as string;

            // T2-13: ключ каталогу не вживається в людський текст — його несе окреме поле messageKey.
            return ("The value does not match the column type.",
                    ImportMessageKeys.TypeMismatch(readerKey));
        }
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
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> lookups,
        CultureInfo culture)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        var text = cell.GetString().Trim();

        switch (definition.DataType)
        {
            // ✎ 2026-09-29: ціле, набране текстом із розрядами («1 234»,
            // «1,234» у en-US), — за культурою користувача, і далі вже `int`:
            // застосування (можливо, у фоновій задачі) культури не потребує.
            case CellDataType.Int:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : CultureNumberReader.Read(text, culture) is { Kind: NumberTextKind.Number, Value: var whole }
                      && decimal.Truncate(whole) == whole
                      && whole is >= int.MinValue and <= int.MaxValue
                        ? (int)whole
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
                // осередок валив би застосування всієї книги. Тут округлюється
                // лише до масштабу СХОВИЩА — тобто рівно те, що сховище однаково
                // відкинуло б; до `ColumnDef.Scale` колонки округлює `Build`
                // (`RoundToColumnScale`, ФВ-9.16b). Округлене значення
                // видно в прев'ю як «нове», і воно ж — те, що буде записано й
                // потрапить у журнал.
                return ReadNumber(cell, text, culture) is { } number
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
                //
                // ⛔ `C1`: код, що в книзі лежить ЧИСЛОМ (1.5), береться
                // інваріантним записом, а не `GetString()`: той форматує
                // `double` поточною культурою СЕРВЕРА, і на uk-UA «1.5» ставало
                // «1,5» — коду, якого в довіднику немає.
                var code = LookupCode(cell, text);

                return definition.LookupRegistryDefId is { } registryId
                       && lookups.TryGetValue(registryId, out var entries)
                       && entries.TryGetValue(code, out var entryId)
                    ? entryId
                    : code;

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

    /// <summary>Число з комірки дробової колонки; <c>null</c> — не число.</summary>
    /// <remarks>
    /// ⛔ Аудит `C1`. Числова комірка береться ЧИСЛОМ, не через
    /// <c>cell.GetString()</c>: той форматує <c>double</c> ПОТОЧНОЮ культурою, і
    /// на сервері з uk-UA 12.5 ставало «12,5», а розбір під Invariant з
    /// <c>AllowThousands</c> читав кому як роздільник тисяч — у базу йшло 125.
    ///
    /// ⚠ Не <c>(decimal)double</c>: той округлює до 15 значущих цифр, а Excel
    /// зберігає 17. Круговий <c>"R"</c> під Invariant дає найкоротший рядок, що
    /// повертає рівно той самий <c>double</c>, — тобто те саме, що доти давав
    /// текстовий шлях на сервері з крапкою, лише без залежності від культури.
    /// Для нього потрібен <c>AllowExponent</c>: 0.00001 — це «1E-05».
    ///
    /// ⚠ Текстова комірка лишається на розборі тексту, і до
    /// <c>NumberStyles.Number</c> додано <c>AllowExponent</c>: «1E-05», набране
    /// текстом, — однозначне число, а відмова на ньому — хибна.
    /// </remarks>
    private static decimal? ReadNumber(IXLCell cell, string text, CultureInfo culture)
    {
        if (cell.DataType == XLDataType.Number)
        {
            var raw = cell.GetDouble();

            return double.IsFinite(raw)
                   && decimal.TryParse(
                       raw.ToString("R", CultureInfo.InvariantCulture),
                       NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                ? exact
                : null;
        }

        // ✎ 2026-09-29 (рішення людини): текстова комірка читається за
        // культурою КОРИСТУВАЧА, а не за правилом «у книзі немає локалі» —
        // «1,234» у ru — це 1.234, у en-US — неоднозначно (відмова);
        // «1.234,5» у en-US — відмова. Правила — `CultureNumberReader`.
        return CultureNumberReader.Read(text, culture) is { Kind: NumberTextKind.Number, Value: var parsed }
            ? parsed
            : null;
    }

    /// <summary>Код запису довідника з комірки: число — інваріантним записом, текст — як є.</summary>
    private static string LookupCode(IXLCell cell, string text)
    {
        if (cell.DataType != XLDataType.Number)
        {
            return text;
        }

        var raw = cell.GetDouble();

        return double.IsFinite(raw) ? raw.ToString("R", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>Колонка тримає число (<c>ValueNumeric</c>).</summary>
    private static bool IsNumeric(ColumnDef definition)
        => definition.DataType is CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated;

    /// <summary>
    /// Округлює число з книги до <see cref="ColumnDef.Scale"/> колонки
    /// (ФВ-9.16b); усе інше повертає як є.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише <see cref="CellDataType.Decimal"/> — так само, як п. 7
    /// <c>ColumnDef.ValidateValue</c>, що відхиляє ручне введення. Правило
    /// те саме (<c>AwayFromZero</c>), тож результат гарантовано проходить цю
    /// перевірку. <c>Scale</c> ≥ масштабу сховища нічого не округляє (число вже
    /// нормалізоване в <c>Read</c>) і не передається в <c>decimal.Round</c> —
    /// той не приймає понад 28. Округлення від уже нормалізованого до 16 знаків
    /// числа дає інше лише на хвості з 17+ знаків, якого в книзі не буває.
    /// </remarks>
    private static object? RoundToColumnScale(object? value, ColumnDef definition)
        => value is decimal number
           && definition.DataType == CellDataType.Decimal
           && definition.Scale is { } scale
           && scale < CellValueReader.StorageScale
            ? decimal.Round(number, scale, MidpointRounding.AwayFromZero)
            : value;

    /// <summary>Чи рахує комірки цієї колонки система — за ЖИВИМ визначенням.</summary>
    /// <remarks>
    /// ⚠ Те саме правило, що в <c>ExcelExporter.IsCalculated</c>, і навмисно те
    /// саме: прев'ю різниці й експорт мусять однаково відповідати на питання
    /// «цю комірку можна редагувати». Різниця між ними — це або відхилена
    /// правка, яку користувач вважав застосовною, або навпаки.
    /// </remarks>
    private static bool IsCalculated(ColumnDef column)
        => column.DataType is CellDataType.Formula or CellDataType.Calculated || column.IsReadOnly;

    /// <summary>Округлення до 15 значущих цифр — точності double Excel.</summary>
    internal static decimal RoundSignificant(decimal value)
    {
        if (value == 0m)
        {
            return 0m;
        }

        var magnitude = (int)Math.Floor(Math.Log10((double)Math.Abs(value))) + 1;
        var scale = Math.Clamp(15 - magnitude, 0, 28);

        return decimal.Round(value, scale, MidpointRounding.AwayFromZero);
    }

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
            // ⚠ Int — точно. Decimal/Formula/Calculated — рівність також за 15
            // значущими цифрами: Excel тримає double, тож 16-значний хвіст БД
            // (`8.1234567890123440`) у книзі стає `8.12345678901234` (P2).
            // Правка в 15-й значущій цифрі й вище лишається зміною.
            CellDataType.Int =>
                current is decimal intNumber
                && (incoming is decimal di ? intNumber == di : incoming is int ii && intNumber == ii),
            CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated =>
                current is decimal number
                && (incoming is decimal d ? number == d || RoundSignificant(number) == RoundSignificant(d)
                    : incoming is int i && number == i),
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

/// <summary>
/// Ключі текстів відмов прев'ю імпорту в каталозі (D-95, `V-10`).
/// </summary>
/// <remarks>
/// ⚠ Одне місце для обох класів адаптера (<see cref="ImportDiffBuilder"/> і
/// <see cref="ExcelImporter"/>): рядок кожного ключа лежить у <c>09-seed.sql</c>,
/// а клієнт перелічує їх літералами (<c>ImportPanel.rejectionText</c>).
/// </remarks>
public static class ImportMessageKeys
{
    /// <summary>Комірку рахує система, і користувач змінив її значення.</summary>
    public const string Calculated = "err.ECR-CELL-4221.importCalculated";

    /// <summary>
    /// Комірку рахує система, книгу не змінювали, але систему перерахували після
    /// експорту — книга застаріла (P3).
    /// </summary>
    public const string CalculatedStale = "err.ECR-CELL-4221.importCalculatedStale";

    /// <summary>Рядка з ключем із файлу в документі немає.</summary>
    public const string NoRow = "err.ECR-ROW-0404.importNoRow";

    /// <summary>Значення стоїть поза рядками таблиці (під нею чи в таблиці без рядків).</summary>
    public const string OutsideRows = "err.ECR-ROW-0404.importOutsideRows";

    /// <summary>Ціла частина числа не вміщується в сховище.</summary>
    public const string IntegerDigits = "err.ECR-CELL-0422.importIntegerDigits";

    /// <summary>Число після округлення до <c>Scale</c> не вміщується в <c>Precision</c> колонки (ФВ-9.16b).</summary>
    public const string Precision = "err.ECR-CELL-0422.importPrecision";

    /// <summary>Екземпляра таблиці з файлу немає в документі за цей період.</summary>
    public const string InstanceMissing = "err.ECR-IMP-0422.importInstanceMissing";

    /// <summary>Таблиці з файлу немає в чинній версії шаблону.</summary>
    public const string TableMissing = "err.ECR-IMP-0422.importTableMissing";

    /// <summary>Правка заборонена правами або станом — той самий текст, що в підказці сітки.</summary>
    public static string Denied(EditDenyReason reason) => $"deny.{reason}";

    /// <summary>Значення з книги не читається як число (F-06).</summary>
    public const string ExpectsNumber = "err.ECR-CELL-0422.importExpectsNumber";

    /// <summary>Значення з книги не читається як true/false.</summary>
    public const string ExpectsBoolean = "err.ECR-CELL-0422.importExpectsBoolean";

    /// <summary>Значення з книги не читається як дата.</summary>
    public const string ExpectsDate = "err.ECR-CELL-0422.importExpectsDate";

    /// <summary>Код із книги не знайдено серед записів довідника (або одиниць) колонки.</summary>
    public const string ExpectsIdentifier = "err.ECR-CELL-0422.importExpectsIdentifier";

    /// <summary>Код із книги не знайдено серед одиниць виміру (колонка Unit).</summary>
    public const string ExpectsUnit = "err.ECR-CELL-0422.importExpectsUnit";

    /// <summary>Імпортний ключ для відмови читача запису за ключем самого читача.</summary>
    /// <param name="readerKey"><c>messageKey</c> відмови <c>CellValueReader</c>.</param>
    /// <remarks>
    /// ⚠ Невідомий ключ читача (нова перевірка там) лишається як є — клієнт
    /// покаже загальну «комірку відхилено»: краще менш точний текст, ніж
    /// мовчазна зміна, на якій Apply впаде на всю книгу.
    /// </remarks>
    public static string? TypeMismatch(string? readerKey) => readerKey switch
    {
        "err.ECR-CELL-0422.expectsNumber" => ExpectsNumber,
        "err.ECR-CELL-0422.expectsBoolean" => ExpectsBoolean,
        "err.ECR-CELL-0422.expectsDate" => ExpectsDate,
        "err.ECR-CELL-0422.expectsIdentifier" => ExpectsIdentifier,
        "err.ECR-CELL-0422.expectsUnitIdentifier" => ExpectsUnit,
        "err.ECR-CELL-0422.tooManyIntegerDigits" => IntegerDigits,
        _ => readerKey,
    };
}
