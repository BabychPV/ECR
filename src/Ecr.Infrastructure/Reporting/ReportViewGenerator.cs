// src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
public sealed partial class ReportViewGenerator(
    EcrDbContext db,
    IReportViewStatus? status = null,
    ILogger<ReportViewGenerator>? logger = null,
    ICorrelationIdAccessor? correlation = null) : IReportViewGenerator
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
            // ⛔ SEC (TIER2): та сама кореляція — у тексті для /health/ready і в рядку журналу
            // з повним винятком, інакше оператор не зв'яже одне з другим.
            // ⚠ Усередині HTTP-запиту (публікація версії) беремо ТОЙ САМИЙ id, що в заголовку
            // X-Correlation-Id і в scope логу (CorrelationIdMiddleware), а не TraceId з Activity:
            // інакше текст /health/ready називав би один id, а журнал із заголовком — інший.
            // Поза запитом (старт) accessor дає null — тоді власний id.
            // ⚠ Усередині HTTP-запиту (публікація версії) беремо ТОЙ САМИЙ id, що в заголовку
            // X-Correlation-Id і в scope логу (CorrelationIdMiddleware), а не TraceId з Activity:
            // інакше текст /health/ready називав би один id, а журнал із заголовком — інший.
            // Поза запитом (старт) accessor дає null — тоді власний id.
            var correlationId = correlation?.CorrelationId ?? SafeErrorText.NewCorrelationId();
            if (logger is not null)
            {
                LogGenerationFailed(logger, templateVersionId, ex.Number, correlationId, ex);
            }

            status?.Failed(new ReportViewFailure(templateVersionId, ex.Number, SafeMessage(ex, correlationId)));
            throw;
        }

        status?.Succeeded(templateVersionId);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ReportViewGenerator: генерація в'юх (версія {TemplateVersionId}) не вдалася, помилка SQL Server {ErrorNumber}; кореляція {CorrelationId}.")]
    private static partial void LogGenerationFailed(
        ILogger logger, int? templateVersionId, int errorNumber, string correlationId, Exception exception);

    /// <summary>Перший номер користувацьких повідомлень SQL Server (<c>RAISERROR</c>/<c>THROW</c>).</summary>
    private const int FirstUserDefinedError = 50_000;

    /// <summary>Текст збою для <c>/health/ready</c>: без тексту системної помилки SQL Server.</summary>
    /// <remarks>
    /// ⛔ SEC (TIER2): <c>SqlException.Message</c> системної помилки (номер &lt; 50000) —
    /// імена об'єктів і значення з запиту, ім'я сервера; у відповідь вони не йдуть
    /// (повний виняток лишається журналу сервера — викликач його кидає/логує). Власні
    /// повідомлення процедури (<c>50422</c>, <c>50409</c>: шаблон, версія, кількість
    /// колонок) пишемо ми самі, вони й призначені адміністратору — лишаються.
    /// </remarks>
    internal static string SafeMessage(SqlException ex, string correlationId)
        => ex.Number >= FirstUserDefinedError
            ? ex.Message
            : $"SQL Server error {ex.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}; the details are in the server log (correlation {correlationId}).";
}