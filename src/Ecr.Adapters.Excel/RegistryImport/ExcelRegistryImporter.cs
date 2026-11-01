using System.Security.Cryptography;
using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;

namespace Ecr.Adapters.Excel.RegistryImport;

/// <summary>
/// Opens an <c>.xlsx</c> through the same safety gate as the document import and runs the generic registry import
/// engine (<see cref="RegistryImportService"/>) on its sheet.
/// </summary>
public sealed class ExcelRegistryImporter(RegistryImportService service) : IRegistryFileImporter
{
    /// <summary>Layout analysis: reads the file with the profile, no database.</summary>
    public async Task<ImportAnalysis> AnalyseAsync(Stream file, RegistryImportSpec spec, CancellationToken ct)
    {
        var opened = await OpenAsync(file, spec, ct).ConfigureAwait(false);
        using (opened.Owner)
        {
            var parsed = RegistryImportParser.Parse(opened.Grid, spec);
            return new ImportAnalysis(
                opened.Hash, opened.SheetName, opened.SheetNames, parsed.Header, parsed.Stats, parsed.Groups, parsed.Issues,
                parsed.DroppedIssues, parsed.Header is not null && !parsed.HasErrors);
        }
    }

    /// <summary>Preview: reads the file, nothing is written.</summary>
    public async Task<RegistryImportPreview> PreviewAsync(
        Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var opened = await OpenAsync(file, spec, ct).ConfigureAwait(false);
        using (opened.Owner)
        {
            return await service.PreviewAsync(opened.Grid, spec, request with { FileSha256 = opened.Hash }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Commit: the same plan as the preview, applied all or nothing.</summary>
    public async Task<ImportCommitResult> CommitAsync(
        Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var opened = await OpenAsync(file, spec, ct).ConfigureAwait(false);
        using (opened.Owner)
        {
            return await service.CommitAsync(opened.Grid, spec, request with { FileSha256 = opened.Hash }, ct).ConfigureAwait(false);
        }
    }

    private static async Task<Opened> OpenAsync(Stream file, RegistryImportSpec spec, CancellationToken ct)
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

                var names = workbook.Worksheets
                    .Where(w => w.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible)
                    .Select(w => w.Name)
                    .ToList();

                return new Opened(new ClosedXmlImportGrid(sheet), hash, owner, sheet.Name, names);
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

    private sealed record Opened(IImportGrid Grid, string Hash, IDisposable Owner, string SheetName, IReadOnlyList<string> SheetNames);

    private sealed class Owner(IDisposable workbook, IDisposable? buffer) : IDisposable
    {
        public void Dispose()
        {
            workbook.Dispose();
            buffer?.Dispose();
        }
    }
}
