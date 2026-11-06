// src/Ecr.Application/Ports/IPermitImportWorkbook.cs
using Ecr.Application.Permits.Import;

namespace Ecr.Application.Ports;

/// <summary>
/// Reads one sheet of an <c>.xlsx</c> as text for «Import file…» of a permit table (the client has no workbook library): behind the
/// same safety gate as the other workbook imports; nothing is stored.
/// </summary>
public interface IPermitImportWorkbook
{
    /// <summary>The named sheet (the first visible one when <paramref name="sheet"/> is empty), at most <paramref name="maxRows"/> rows.</summary>
    /// <param name="file">The workbook.</param>
    /// <param name="sheet">Sheet name; <c>null</c> — first visible.</param>
    /// <param name="maxRows">Most rows returned (<see cref="PermitImportSheetDto.Truncated"/> tells there were more).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>422 ECR-IMP-0422</c>: not a workbook, unsafe package or no such sheet.</exception>
    public Task<PermitImportSheetDto> ReadAsync(Stream file, string? sheet, int maxRows, CancellationToken ct);
}
