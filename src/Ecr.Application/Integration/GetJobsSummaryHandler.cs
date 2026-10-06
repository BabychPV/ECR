using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>
/// Лічильники черги фонових задач для смуги показників екрана «Jobs».
/// </summary>
/// <remarks>
/// ⛔ Авторизація ДЗЕРКАЛИТЬ <see cref="ListJobsHandler"/> слово в слово: без
/// <c>mine</c> потрібне <c>System.ViewHealth</c> (відмова — <c>403</c>, а НЕ
/// нулі: «задач немає» і «вам їх не показують» — різні відповіді); з
/// <c>mine=true</c> рахуються лише ВЛАСНІ задачі, і автор береться з
/// <c>ICurrentUser</c>. Параметра «чиї задачі» немає і не буде.
///
/// ⚠ Віддаються лише ЧИСЛА. Ні тексту провалу, ні коду помилки, ні
/// ідентифікаторів документів чи проєктів тут немає, тож приховані документи
/// не підтверджуються навіть опосередковано.
/// </remarks>
public sealed class GetJobsSummaryHandler(
    IJobProgressStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Повертає лічильники.</summary>
    /// <param name="mine">Лише власні задачі; не вимагає <c>System.ViewHealth</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або чужі задачі без права.</exception>
    public async Task<JobsSummary> HandleAsync(bool mine, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-AUTH-0401.anonymous",
                         });

        if (!mine)
        {
            await ListTemplatesHandler
                .RequireAsync(access, currentUser, GetJobStatusHandler.Permission, ct)
                .ConfigureAwait(false);
        }

        return await store
            .SummarizeAsync(mine ? userId : null, clock.UtcNow, ct)
            .ConfigureAwait(false);
    }
}
