// src/Ecr.Infrastructure/Reporting/ReportViewGenerator.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    ICorrelationIdAccessor? correlation = null,
    // ✎ Q-17.7: прапор `Collection:MonthColumnRouting` → процедура будує в'юхи MonthsInColumns із читанням власників;
    // `null` (тести без роутера) = вимкнено, текст в'юх як до Q-17.
    IMonthColumnRouter? monthRouter = null) : IReportViewGenerator
{
    /// <summary>Стеля однієї команди генерації з прапором <c>Collection:MonthColumnRouting</c>, секунди (QA3 D5).</summary>
    public const int RoutedGenerationTimeoutSeconds = 300;

    /// <inheritdoc />
    public async Task GenerateAsync(int? templateVersionId, CancellationToken ct)
    {
        // ✎ Q-17 (QA3 D5): холодна генерація З прапором (~600 в'юх з приєднаннями власників) на живій базі може тривати
        // понад 60 с (41 с у тихій системі, 60 с — стеля команди за конкуренції) → SqlException, `reportviews` = Degraded.
        // Тому з прапором команда отримує окрему стелю; прапор вимкнено — таймаут контексту НЕ змінюється (поведінка як була).
        // Контекст scoped і може жити далі (публікація версії), тож попереднє значення повертається.
        var routed = monthRouter is { Enabled: true };
        var previousTimeout = db.Database.GetCommandTimeout();
        if (routed)
        {
            db.Database.SetCommandTimeout(RoutedGenerationTimeoutSeconds);
        }

        try
        {
            // ✎ RPT-STABLE: спершу _vN (без змін), потім стабільні вʼюхи без версії. Збій першого не відміняє другого
            // (одна задовга таблиця не лишає без вʼюх решту), але перша помилка кидається викликачу.
            SqlException? first = null;
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId, @MonthRouting",
                    [
                        new SqlParameter("@TemplateVersionId", System.Data.SqlDbType.Int)
                        {
                            Value = (object?)templateVersionId ?? DBNull.Value,
                        },
                        new SqlParameter("@MonthRouting", System.Data.SqlDbType.Bit) { Value = routed },
                    ],
                    ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                first = ex;
            }

            try
            {
                await GenerateStableAsync(templateVersionId, routed, ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                first ??= ex;
            }

            if (first is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }
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
            var correlationId = correlation?.CorrelationId ?? SafeErrorText.NewCorrelationId();
            if (logger is not null)
            {
                LogGenerationFailed(logger, templateVersionId, ex.Number, correlationId, ex);
            }

            status?.Failed(new ReportViewFailure(templateVersionId, ex.Number, SafeMessage(ex, correlationId)));
            throw;
        }
        finally
        {
            if (routed)
            {
                db.Database.SetCommandTimeout(previousTimeout);
            }
        }

        status?.Succeeded(templateVersionId);
    }

    /// <summary>
    /// ✎ RPT-STABLE: <c>rpt.usp_GenerateStableViews</c> — вʼюхи без версії (шаблон, аркуш, таблиця) по всіх опублікованих версіях.
    /// Попередження процедури (конфлікт типу колонки між версіями → nvarchar) пишуться в журнал.
    /// </summary>
    private async Task GenerateStableAsync(int? templateVersionId, bool routed, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC rpt.usp_GenerateStableViews @TemplateVersionId, @MonthRouting";
            command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new SqlParameter("@TemplateVersionId", System.Data.SqlDbType.Int)
            {
                Value = (object?)templateVersionId ?? DBNull.Value,
            });
            command.Parameters.Add(new SqlParameter("@MonthRouting", System.Data.SqlDbType.Bit) { Value = routed });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(2) && logger is not null)
                {
                    LogStableWarning(logger, reader.GetString(0), reader.GetString(2));
                }
            }
        }
        finally
        {
            if (wasClosed)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ReportViewGenerator: стабільна вʼюха rpt.{ViewName}: {Warning}.")]
    private static partial void LogStableWarning(ILogger logger, string viewName, string warning);

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
            : $"SQL Server error {ex.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}; the details are in the server log (correlation {correlationId})."
              + (ex.Number == MissingProcedureError ? MissingProcedureHint : string.Empty);

    /// <summary>SQL Server 2812: «Could not find stored procedure».</summary>
    private const int MissingProcedureError = 2812;

    /// <summary>
    /// ✎ Upgrade gap (RPT-STABLE): <c>Sql/*.sql</c> reach a customer database only through step 2 of <c>deploy-ecr.ps1</c>
    /// (every script, idempotent, on each deploy/upgrade; never from the application or EF migrations). A database updated
    /// without that step (<c>-SkipSchema</c>, manual EF-only update) lacks the report-view procedures. The application
    /// has no right to create them (D-66: EXECUTE only), so the message tells the operator what to run. No object names (SEC TIER2).
    /// </summary>
    private const string MissingProcedureHint =
        " The report-view procedures are missing from this database: the Sql scripts of this release were not applied. "
        + "Run deploy-ecr.ps1 with the schema step (without -SkipSchema, runbook section 8) and restart the application.";
}