// src/Ecr.Adapters.Excel/RegistryWorkbookWriter.cs
using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.Excel;

/// <summary>Книга експорту довідника (RT-16): плаский аркуш на довідник, заголовок — коди колонок.</summary>
/// <remarks>
/// ⛔ Число лягає ЧИСЛОМ лише тоді, коли <c>double</c> Excel тримає його без втрат; інакше — текстом
/// рядка сервера (D-30). <c>decimal(34,16)</c> має до 34 значущих цифр, <c>double</c> — 15–17: число,
/// записане числом «завжди», тихо обрізалося б, і повторний імпорт змінив би довідник.
/// <para>
/// ⚠ Тимчасовий ФАЙЛ, а не <c>MemoryStream</c> — той самий прийом, що в
/// <see cref="SnapshotWorkbookWriter"/>.
/// </para>
/// </remarks>
public sealed class RegistryWorkbookWriter : IRegistryWorkbookWriter
{
    /// <summary>Формат дат у книзі: ISO, однаковий у будь-якій локалі.</summary>
    public const string DateFormat = "yyyy-mm-dd";

    /// <summary>Найдовша назва аркуша в Excel.</summary>
    private const int MaxSheetName = 31;

    /// <summary>Книга з одного аркуша.</summary>
    /// <param name="workbook">Колонки й рядки.</param>
    /// <param name="ct">Скасування.</param>
    public Task<Stream> WriteAsync(RegistryWorkbook workbook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        return WriteAsync([workbook], ct);
    }

    /// <inheritdoc/>
    public async Task<Stream> WriteAsync(IReadOnlyList<RegistryWorkbook> sheets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            throw new ArgumentException("Книга без аркушів.", nameof(sheets));
        }

        using var book = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workbook in sheets)
        {
            ArgumentNullException.ThrowIfNull(workbook);
            Fill(book.AddWorksheet(UniqueSheetName(workbook.SheetName, names)), workbook, ct);
        }

        var output = new FileStream(
            Path.Combine(Path.GetTempPath(), $"ecr-registry-{Guid.NewGuid():N}.xlsx"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            book.SaveAs(output);
            output.Position = 0;
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return output;
    }

    /// <summary>Заголовок і рядки одного аркуша.</summary>
    private static void Fill(IXLWorksheet sheet, RegistryWorkbook workbook, CancellationToken ct)
    {
        for (var c = 0; c < workbook.Columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = workbook.Columns[c].Header;
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        for (var r = 0; r < workbook.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = workbook.Rows[r];
            for (var c = 0; c < workbook.Columns.Count && c < row.Count; c++)
            {
                Write(sheet.Cell(r + 2, c + 1), row[c], workbook.Columns[c].Kind);
            }
        }

        if (workbook.Columns.Count > 0)
        {
            sheet.Columns(1, workbook.Columns.Count).AdjustToContents(1, 1);
        }
    }

    /// <summary>Комірка за типом колонки; нерозбірне значення лягає текстом, як є.</summary>
    private static void Write(IXLCell cell, string? value, CellDataType kind)
    {
        if (value is null)
        {
            return;
        }

        switch (kind)
        {
            case CellDataType.Int or CellDataType.Decimal
                when decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                     && IsExactInDouble(number):
                cell.Value = (double)number;
                break;

            case CellDataType.Date
                when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = DateFormat;
                break;

            case CellDataType.Bool when bool.TryParse(value, out var flag):
                cell.Value = flag;
                break;

            default:
                // ⚠ Рядок лягає ТЕКСТОМ (ClosedXML ≥ 0.100 не вгадує тип): «00123» чи «1E5» не стануть числом.
                cell.Value = value;
                break;
        }
    }

    /// <summary>Чи <c>double</c> повертає те саме <c>decimal</c> (разом із 15-значним записом Excel).</summary>
    internal static bool IsExactInDouble(decimal value)
    {
        var asDouble = (double)value;
        return decimal.TryParse(
                   asDouble.ToString("R", CultureInfo.InvariantCulture),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var back)
               && back == value
               && decimal.TryParse(
                   asDouble.ToString("G15", CultureInfo.InvariantCulture),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var shown)
               && shown == value;
    }

    /// <summary>
    /// Назва аркуша, якої ще немає в книзі (Excel не розрізняє регістр): два коди, однакові в перших
    /// 31 символі, отримують суфікс <c>~2</c>, <c>~3</c>… — інакше ClosedXML відмовив би всій книзі.
    /// </summary>
    private static string UniqueSheetName(string name, HashSet<string> taken)
    {
        var baseName = SheetName(name);
        var candidate = baseName;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            var suffix = "~" + n.ToString(CultureInfo.InvariantCulture);
            candidate = (baseName.Length + suffix.Length <= MaxSheetName ? baseName : baseName[..(MaxSheetName - suffix.Length)]) + suffix;
        }

        return candidate;
    }

    /// <summary>Назва аркуша: без заборонених Excel символів і не довша за 31.</summary>
    private static string SheetName(string name)
    {
        var clean = new string([.. (string.IsNullOrWhiteSpace(name) ? "Registry" : name)
            .Select(ch => ch is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : ch)]);
        return clean.Length <= MaxSheetName ? clean : clean[..MaxSheetName];
    }
}
