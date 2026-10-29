using System.Security.Cryptography;
using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;

namespace Ecr.Adapters.Excel.RegistryImport;

/// <summary>
/// Opens an <c>.xlsx</c> through the same safety gate as the document import and runs the generic registry import
/// engine (<see cref="RegistryImportService"/>) on its sheet.
/// </summary>
public sealed class ExcelRegistryImporter(RegistryImportService service)
{
    /// <summary>Preview: reads the file, nothing is written.</summary>
    public async Task<ImportPreview> PreviewAsync(
        Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var (grid, hash, owner) = await OpenAsync(file, spec, ct).ConfigureAwait(false);
        using (owner)
        {
            return await service.PreviewAsync(grid, spec, request with { FileSha256 = hash }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Commit: the same plan as the preview, applied all or nothing.</summary>
    public async Task<ImportCommitResult> CommitAsync(
        Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var (grid, hash, owner) = await OpenAsync(file, spec, ct).ConfigureAwait(false);
        using (owner)
        {
            return await service.CommitAsync(grid, spec, request with { FileSha256 = hash }, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(IImportGrid Grid, string Hash, IDisposable Owner)> OpenAsync(
        Stream file, RegistryImportSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(spec);

        var seekable = await XlsxSafetyGate.SeekableAsync(file, ct).ConfigureAwait(false)
                       ?? throw XlsxSafetyGate.Rejection("package is larger than the ceiling");

        var buffered = ReferenceEquals(seekable, file) ? null : seekable;
        var start = seekable.Position;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(seekable, ct).ConfigureAwait(false));
        seekable.Position = start;

        try
        {
            var workbook = ExcelImporter.Open(seekable);
            var owner = new Owner(workbook, buffered);

            try
            {
                var sheet = spec.Sheet is { Length: > 0 } name
                    ? workbook.Worksheets.TryGetWorksheet(name, out var named)
                        ? named
                        : throw new BusinessRuleException(
                            "ECR-IMP-0422",
                            "The sheet named in the import profile is missing in the file.",
                            new Dictionary<string, object?> { ["messageKey"] = ImportMessageKeys.SheetMissing })
                    : workbook.Worksheets.FirstOrDefault(w => w.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible)
                      ?? workbook.Worksheets.First();

                return (new ClosedXmlImportGrid(sheet), hash, owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }
        catch
        {
            buffered?.Dispose();
            throw;
        }
    }

    private sealed class Owner(IDisposable workbook, IDisposable? buffer) : IDisposable
    {
        public void Dispose()
        {
            workbook.Dispose();
            buffer?.Dispose();
        }
    }
}
