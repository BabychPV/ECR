using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Import;
using Ecr.Application.Ports;
using Ecr.Application.RegistryImport;

namespace Ecr.Adapters.Excel.RegistryImport;

/// <summary>
/// Excel side of «Import file…» of a permit table: one sheet as text, behind <see cref="XlsxSafetyGate"/> (the same gate as the
/// document, registry and NAE tree imports).
/// </summary>
public sealed class PermitImportWorkbook : IPermitImportWorkbook
{
    /// <summary>Most columns read.</summary>
    public const int MaxColumns = 128;

    /// <inheritdoc />
    public async Task<PermitImportSheetDto> ReadAsync(Stream file, string? sheet, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var seekable = await XlsxSafetyGate.SeekableAsync(file, ct).ConfigureAwait(false)
                       ?? throw XlsxSafetyGate.Rejection("package is larger than the ceiling");
        var buffered = ReferenceEquals(seekable, file) ? null : seekable;

        try
        {
            using var workbook = ExcelImporter.Open(seekable);
            var visible = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList();
            var chosen = sheet is null
                ? visible.FirstOrDefault() ?? workbook.Worksheets.First()
                : visible.FirstOrDefault(w => string.Equals(w.Name, sheet, StringComparison.OrdinalIgnoreCase))
                  ?? throw new BusinessRuleException(
                      "ECR-IMP-0422",
                      "The chosen sheet is missing in the file.",
                      new Dictionary<string, object?> { ["messageKey"] = ImportMessageKeys.SheetMissing });

            var grid = new ClosedXmlImportGrid(chosen);
            var width = Math.Min(grid.ColumnCount, MaxColumns);
            var count = Math.Min(grid.RowCount, maxRows);
            var rows = new List<IReadOnlyList<string>>(count);
            for (var r = 1; r <= count; r++)
            {
                var row = new string[width];
                for (var c = 1; c <= width; c++)
                {
                    row[c - 1] = Text(grid.Get(r, c).Value);
                }

                // Trailing empty cells are dropped; a fully empty row stays (the dialog skips it like an empty pasted line).
                var last = Array.FindLastIndex(row, v => v.Length > 0);
                rows.Add(row[..(last + 1)]);
            }

            return new PermitImportSheetDto(chosen.Name, [.. visible.Select(w => w.Name)], rows, grid.RowCount > maxRows);
        }
        finally
        {
            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Cell as the dialog reads pasted text: numbers invariant, dates ISO, logical <c>true</c>/<c>false</c>.</summary>
    internal static string Text(object? raw)
    {
        var text = raw switch
        {
            null => string.Empty,
            string s => s,
            double d => d.ToString("0.###############", CultureInfo.InvariantCulture),
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        // Tabs and line breaks inside a cell would split it in the dialog's row model.
        return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
