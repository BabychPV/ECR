// src/Ecr.Application/Permits/Import/PermitImportWorkbookHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Import;

/// <summary>
/// «Import file…» of a permit table with an <c>.xlsx</c>: one sheet back as text (header + rows), so the dialog maps and checks it
/// exactly like a paste. Nothing is written. Right: <c>Registry.EditData</c> or grant <c>Write</c> on the table (who may add rows).
/// </summary>
public sealed class PermitImportWorkbookHandler(
    IRegistryStore registries,
    IPermitImportWorkbook workbook,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Right to import (or a grant <c>Write</c> on the table).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Largest workbook, MB (the same limit as a CSV in the dialog).</summary>
    public const int MaxFileMb = 5;

    /// <summary>Largest workbook, bytes.</summary>
    public const long MaxFileBytes = MaxFileMb * 1024L * 1024L;

    /// <summary>Rows returned: header + the paste limit (5000) + one, so the dialog names the overflow.</summary>
    public const int MaxRows = 5002;

    /// <summary>Reads the sheet.</summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="length">Its size, bytes.</param>
    /// <param name="sheet">Sheet name; empty — first visible.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PermitImportSheetDto> HandleAsync(string code, Stream file, long length, string? sheet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var lookup = new RegistryLookup(registries, code);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct).ConfigureAwait(false);
        PermitImportTables.RequireTable(await lookup.RequireAsync(ct).ConfigureAwait(false), code);

        if (length > MaxFileBytes)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"The workbook is larger than {MaxFileMb} MB.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-IMP-0422.permitImportFileTooLarge",
                    ["max"] = MaxFileMb.ToString(CultureInfo.InvariantCulture),
                });
        }

        return await workbook.ReadAsync(file, string.IsNullOrWhiteSpace(sheet) ? null : sheet.Trim(), MaxRows, ct).ConfigureAwait(false);
    }
}
