// src/Ecr.Application/Ports/ReportViewQueueFailure.cs
using Ecr.Application.Errors;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис на картку <c>reportviews</c>, коли НЕ вдалося ПОСТАВИТИ перегенерацію вʼюх (публікація версії, перенос версії,
/// старт застосунку).
/// </summary>
/// <remarks>
/// ⛔ SEC (TIER2, F13 RC #872 delta P2): картку читає АНОНІМНИЙ <c>/health/ready</c> (редагується лише «db»), тож сирий
/// <c>Exception.Message</c> (SqlException: ім'я сервера, обʼєкти, значення) туди не йде. Як
/// <c>ReportViewGenerator.SafeMessage</c>: фіксований англійський текст + <see cref="SafeErrorText.For"/> із кореляцією;
/// повний виняток викликач пише в журнал (Warning) під тією самою кореляцією.
/// </remarks>
public static class ReportViewQueueFailure
{
    /// <summary>Кореляція запиту, а поза запитом (старт, фон) — нова (<see cref="SafeErrorText.NewCorrelationId"/>).</summary>
    /// <param name="requestCorrelationId">Кореляція запиту; <c>null</c> або порожня — немає.</param>
    public static string Correlation(string? requestCorrelationId)
        => string.IsNullOrWhiteSpace(requestCorrelationId) ? SafeErrorText.NewCorrelationId() : requestCorrelationId;

    /// <summary>Безпечний запис картки.</summary>
    /// <param name="templateVersionId">Версія; <c>null</c> — прогін по всіх шаблонах.</param>
    /// <param name="error">Виняток постановки (у журнал — повністю, сюди — лише код і кореляція).</param>
    /// <param name="headline">Фіксований англійський заголовок («Report view regeneration was not queued.»).</param>
    /// <param name="correlationId">Кореляція, під якою виняток лежить у журналі.</param>
    public static ReportViewFailure Of(int? templateVersionId, Exception error, string headline, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(headline);
        return new ReportViewFailure(
            templateVersionId,
            CodeOf(error),
            headline + " " + SafeErrorText.For(error, correlationId, "the queueing of the report view regeneration"));
    }

    /// <summary>
    /// Код картки (<c>[код]</c>), як у <c>ReportViewGenerator</c>: номер помилки SQL Server, коли в ланцюгу є помилка
    /// бази з числовим <c>Number</c> (<c>SqlException</c>); інакше <see cref="Exception.HResult"/> винятку — ніколи не <c>0</c>.
    /// </summary>
    public static int CodeOf(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Data.Common.DbException db
                && db.GetType().GetProperty("Number")?.GetValue(db) is int number and not 0)
            {
                return number;
            }
        }

        return error.HResult != 0 ? error.HResult : FallbackCode;
    }

    /// <summary>Код, коли ні номера SQL, ні HResult немає (HResult = 0 у власних нащадків Exception буває рідко).</summary>
    public const int FallbackCode = 50500;
}
