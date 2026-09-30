// src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Викликає <c>rpt.usp_GenerateTemplateViews</c> (<c>05-rpt-views.sql</c>).
/// </summary>
/// <remarks>
/// ⚠ Викликається ПРОЦЕДУРА, як і <c>arc.usp_ArchiveYear</c> в
/// <c>ArchiveJob</c>: DDL робить вона від імені власника (<c>D-66</c>), а
/// обліковому запису застосунку досить права EXECUTE. ⛔ Публікація версії
/// викликає це ПІСЛЯ коміту й глушить збій (рішення координатора 2026-09-30:
/// SSRS-шар другорядний, публікація — ядро); збій лишається видимим у журналі
/// і на <c>/health/ready</c> (<see cref="IReportViewStatus"/>).
/// </remarks>
public sealed class ReportViewGenerator(EcrDbContext db, IReportViewStatus? status = null) : IReportViewGenerator
{
    /// <inheritdoc />
    public async Task GenerateAsync(int? templateVersionId, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId",
                [new SqlParameter("@TemplateVersionId", System.Data.SqlDbType.Int)
                {
                    Value = (object?)templateVersionId ?? DBNull.Value,
                }],
                ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // ⚠ Збій фіксується для /health/ready (картка `reportviews`) і кидається
            // далі: вирішує викликач — публікація й старт глушать його (найкраще
            // зусилля), тести й DBA бачать помилку.
            status?.Failed(new ReportViewFailure(templateVersionId, ex.Number, ex.Message));
            throw;
        }

        status?.Succeeded(templateVersionId);
    }
}