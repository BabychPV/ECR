// src/Ecr.Application/Documents/CalculationYear/CalculationYearExport.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>Що вивантажити з огляду року (їде в завданні експорту).</summary>
/// <param name="Year">Рік.</param>
/// <param name="Layout"><c>pivot</c> — місяці колонками; <c>long</c> — рядок на вихід × місяць.</param>
/// <param name="RowIds">Виходи «як відфільтровано» (<see cref="CalculationYearRowDto.Id"/>); <c>null</c> — увесь документ.</param>
public sealed record CalculationYearExportSpec(int Year, string Layout, IReadOnlyList<string>? RowIds);

/// <summary>
/// <c>POST /api/v1/documents/{id}/calculation-results/year/export</c>: ставить книгу огляду року в чергу тією самою
/// задачею <see cref="IExcelExportJob"/>, що й експорт документа (той самий прогрес, «My tasks» і
/// <c>GET …/export/{exportId}</c>).
/// </summary>
/// <remarks>
/// ⚠ Права — <c>Document.Export</c> (вивантаження) І <c>Calculation.View</c> (що вивантажується); межі читання
/// рахуються в задачі для профілю замовника тим самим <see cref="CalculationYearViewBuilder"/>, що й на екрані.
/// </remarks>
public sealed class ExportCalculationYearHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    Ecr.Domain.Abstractions.IClock clock)
{
    /// <summary>Право на вивантаження (`02-contracts.md` §9); що вивантажується — ще й <c>Calculation.View</c>.</summary>
    public const string Permission = "Document.Export";

    /// <summary>Розкладки книги.</summary>
    public const string Pivot = "pivot", Long = "long";

    /// <summary>Стеля переліку «як відфільтровано».</summary>
    public const int MaxRowIds = 200_000;

    /// <summary>Ставить експорт у чергу; повертає ідентифікатор задачі.</summary>
    public async Task<string> HandleAsync(
        long documentId, int year, string? layout, IReadOnlyList<string>? rowIds, string? language, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, GetCalculationYearHandler.Permission, ct).ConfigureAwait(false);
        GetCalculationYearHandler.RequireYear(year);

        var shape = string.IsNullOrWhiteSpace(layout) ? Pivot : layout.Trim().ToLowerInvariant();
        if (shape is not (Pivot or Long) || rowIds is { Count: > MaxRowIds })
        {
            throw new Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Розкладка «{layout}» або перелік виходів ({rowIds?.Count}) не підходять для експорту.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.calcResultsExportInvalid",
                    ["layout"] = layout ?? "",
                });
        }

        var exportId = Guid.NewGuid().ToString("N");
        var requestedBy = currentUser.UserId ?? profile.UserId;
        var options = new ExcelExportOptions(
            IncludeFormulas: false, IncludeStyles: false, string.IsNullOrWhiteSpace(language) ? "en" : language,
            PeriodKey.Create(year, 1).Value, HiddenTableDefIds: [], HiddenColumnDefIds: []);

        var jobId = await jobs
            .EnqueueAsync<IExcelExportJob>(
                new ExcelExportTask(documentId, options, exportId, DocumentExportFormat.Xlsx, requestedBy)
                {
                    CalcResults = new CalculationYearExportSpec(year, shape, rowIds),
                },
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                ExportDocumentHandler.EventType,
                TargetUserId: null,
                TargetRoleId: null,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    documentId,
                    format = "calc-results-xlsx",
                    year,
                    layout = shape,
                    rowCount = rowIds?.Count,
                    jobId,
                }),
                requestedBy,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return jobId;
    }
}

/// <summary>Книга огляду року: аркуш значень (місяці колонками або довгий перелік) і аркуш місяців.</summary>
public static class CalculationYearWorkbook
{
    private static readonly string[] MonthNames =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Аркуші книги з огляду.</summary>
    /// <param name="view">Огляд (уже в межах читання замовника).</param>
    /// <param name="spec">Що вивантажити.</param>
    /// <param name="language">Мова підписів (<c>en</c>, <c>ru</c>, <c>kz</c>).</param>
    public static IReadOnlyList<RegistryWorkbook> Sheets(CalculationYearViewDto view, CalculationYearExportSpec spec, string language)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(spec);

        var keep = spec.RowIds is null ? null : new HashSet<string>(spec.RowIds, StringComparer.Ordinal);
        var rows = view.Rows.Where(r => keep is null || keep.Contains(r.Id)).ToList();
        string L(LocalizedText? text) => text?.Get(language) ?? "";

        string[] Describe(CalculationYearRowDto r)
        {
            var eq = view.Equipment[r.Equipment];
            var table = view.Tables[eq.Table];
            var meth = r.Methodology is { } mi ? view.Methodologies[mi] : null;
            var output = r.Column is { } ci ? L(view.Columns[ci].HeaderL10n) : r.OutputCode;
            var substance = r.Substance is { } si ? view.Substances[si] : null;
            var unit = r.Unit is { } ui ? view.Units[ui].Code : "";
            var equipmentName = eq.Name ?? (eq.LabelL10n is null ? eq.RowKey ?? "" : L(eq.LabelL10n));
            return
            [
                L(table.SheetNameL10n), table.TableCode + " " + L(table.TableNameL10n), equipmentName, eq.IdCode ?? "",
                meth is null ? "Template formula" : L(meth.NameL10n), meth?.Version ?? "", output,
                substance is null ? "" : substance.Code, unit,
            ];
        }

        string[] head = ["Sheet", "Table", "Equipment", "ID code", "Methodology", "Version", "Output", "Substance", "Unit"];
        var text = head.Select(h => new RegistryWorkbookColumn(h, CellDataType.String)).ToList();
        static string? N(decimal? v) => v?.ToString(CultureInfo.InvariantCulture);

        RegistryWorkbook values;
        if (spec.Layout == ExportCalculationYearHandler.Long)
        {
            var columns = text.Concat([
                new RegistryWorkbookColumn("Month", CellDataType.String),
                new RegistryWorkbookColumn("Month state", CellDataType.String),
                new RegistryWorkbookColumn("Value", CellDataType.Decimal)]).ToList();
            var lines = new List<IReadOnlyList<string?>>();
            foreach (var r in rows)
            {
                var d = Describe(r);
                for (var m = 0; m < 12; m++)
                {
                    if (r.Values[m] is { } v)
                    {
                        lines.Add([.. d, $"{view.Year}-{m + 1:00}", view.Months[m].RunState, N(v)]);
                    }
                }
            }

            values = new RegistryWorkbook("Results", columns, lines);
        }
        else
        {
            var columns = text
                .Concat(MonthNames.Select((n, m) => new RegistryWorkbookColumn(
                    $"{n} {view.Year}" + (view.Months[m].RunState == "outOfDate" ? " (out of date)" : ""), CellDataType.Decimal)))
                .Concat([
                    new RegistryWorkbookColumn("Year total", CellDataType.Decimal),
                    new RegistryWorkbookColumn("Months included", CellDataType.Int)])
                .ToList();
            values = new RegistryWorkbook(
                "Results",
                columns,
                [.. rows.Select(r => (IReadOnlyList<string?>)[
                    .. Describe(r), .. r.Values.Select(N), N(r.YearTotal),
                    r.YearTotal is null ? null : r.MonthsIncluded.ToString(CultureInfo.InvariantCulture)])]);
        }

        var months = new RegistryWorkbook(
            "Months",
            [
                new RegistryWorkbookColumn("Month", CellDataType.String),
                new RegistryWorkbookColumn("Period state", CellDataType.String),
                new RegistryWorkbookColumn("Run state", CellDataType.String),
                new RegistryWorkbookColumn("Calculated at (UTC)", CellDataType.String),
            ],
            [.. view.Months.Select(m => (IReadOnlyList<string?>)[
                $"{view.Year}-{m.Month:00}", m.PeriodState, m.RunState,
                m.CalculatedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)])]);

        return [values, months];
    }
}
