// src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Викликає <c>rpt.usp_GenerateReportViews</c> (<c>05-rpt-views.sql</c>).
/// </summary>
/// <remarks>
/// ⚠ Викликається ПРОЦЕДУРА, як і <c>arc.usp_ArchiveYear</c> в
/// <c>ArchiveJob</c>: DDL робить вона від імені власника (<c>D-66</c>), а
/// обліковому запису застосунку досить права EXECUTE. Той самий контекст —
/// отже та сама транзакція, якщо викликач її відкрив: публікація версії і її
/// вʼюха комітяться разом або не комітяться зовсім.
/// </remarks>
public sealed class ReportViewGenerator(EcrDbContext db) : IReportViewGenerator
{
    /// <inheritdoc />
    public async Task GenerateAsync(int? reportDefId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "EXEC rpt.usp_GenerateReportViews @ReportDefId",
            [new SqlParameter("@ReportDefId", System.Data.SqlDbType.Int) { Value = (object?)reportDefId ?? DBNull.Value }],
            ct).ConfigureAwait(false);
    }
}
