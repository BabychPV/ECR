// src/Ecr.Application/Calculations/MethodologyTraceStepsHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Errors;

namespace Ecr.Application.Calculations;

/// <summary>Прапор методології «трасувати проміжні кроки» (D-565).</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="TraceSteps"><c>true</c> — прогони методології пишуть кроки трейсу (<c>calc.CalculationStep</c>).</param>
public sealed record MethodologyTraceStepsDto(int MethodologyId, bool TraceSteps);

/// <summary>Читає прапор «трасувати проміжні кроки» методології (D-565).</summary>
/// <remarks>
/// ⚠ Право читання — <c>Calculation.View</c>, як у решти читань методології: саме за цим прапором екран, що пояснює
/// число, чесно каже «кроки не записано — трасування цієї методології вимкнено», а не показує порожнечу.
/// </remarks>
public sealed class GetMethodologyTraceStepsHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає прапор.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Поточне значення прапора.</returns>
    /// <exception cref="NotFoundException">Методології немає — <c>ECR-CALC-0404</c>.</exception>
    public async Task<MethodologyTraceStepsDto> HandleAsync(int methodologyId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var methodology = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
                          ?? throw MethodologyTraceSteps.NotFound(methodologyId);

        return new MethodologyTraceStepsDto(methodology.Id, methodology.TraceSteps);
    }
}

/// <summary>
/// Вмикає чи вимикає запис кроків трейсу методології (D-565, PERF-03).
/// </summary>
/// <remarks>
/// ⛔ Право — <c>Calculation.EditFormula</c> (те, що править методології), а не <c>Calculation.Publish</c>, як у фільтра
/// активності рядків (D-425): той змінює числа опублікованих версій, а цей прапор — ні. Результати й входи пишуться
/// однаково; змінюється лише те, чи лягають проміжні кроки в <c>calc.CalculationStep</c>. Перерахунок не запускається:
/// кроки з'являться в наступному прогоні документа.
/// <para>
/// ⚠ Зміна — у журнал структурних змін (<c>aud.StructureChange</c>, <c>calc.Methodology</c>/<see cref="Operation"/>) у
/// ТІЙ САМІЙ транзакції: хто й коли ввімкнув запис трейсу, видно в журналі, а обсяг журналу транзакцій SQL Server, який
/// прапор змінює в рази (≈ 49 МБ на прогін Land), можна зіставити з моментом зміни. Повтор того самого значення —
/// без запису.
/// </para>
/// </remarks>
public sealed class SetMethodologyTraceStepsHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на редагування методологій (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Тип запису журналу структурних змін.</summary>
    public const string EntityType = "calc.Methodology";

    /// <summary>Операція журналу структурних змін.</summary>
    public const string Operation = "SetTraceSteps";

    /// <summary>Ставить прапор.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="enabled">Нове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Методологію з її поточним прапором.</returns>
    /// <exception cref="NotFoundException">Методології немає — <c>ECR-CALC-0404</c>.</exception>
    public async Task<MethodologyTraceStepsDto> HandleAsync(int methodologyId, bool enabled, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        MethodologyTraceStepsDto? result = null;
        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                var methodology = await drafts.FindAsync(methodologyId, innerCt).ConfigureAwait(false)
                                  ?? throw MethodologyTraceSteps.NotFound(methodologyId);

                var before = methodology.TraceSteps;
                if (methodology.SetTraceSteps(enabled))
                {
                    await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

                    // ⚠ Нуль версії шаблону, як у прив'язки методології: прапор — властивість контейнера методології.
                    await audit.WriteStructureChangeAsync(
                        new StructureChangeRecord(
                            ChangedAt: clock.UtcNow,
                            TemplateVersionId: 0,
                            EntityType: EntityType,
                            EntityId: methodology.Id,
                            ChangeClass: Domain.Enums.ChangeClass.Safe,
                            Operation: Operation,
                            OldJson: JsonSerializer.Serialize(new { traceSteps = before }),
                            NewJson: JsonSerializer.Serialize(new
                            {
                                methodologyId = methodology.Id,
                                code = methodology.Code,
                                traceSteps = methodology.TraceSteps,
                            }),
                            ChangeReason: null,
                            ChangedByUserId: userId,
                            CorrelationId: currentUser.CorrelationId),
                        innerCt).ConfigureAwait(false);
                }

                result = new MethodologyTraceStepsDto(methodology.Id, methodology.TraceSteps);
            },
            ct).ConfigureAwait(false);

        return result!;
    }
}

/// <summary>Спільне для обробників прапора трейсу.</summary>
internal static class MethodologyTraceSteps
{
    /// <summary>404 методології з ключем повідомлення.</summary>
    /// <param name="methodologyId">Методологія, якої немає.</param>
    /// <returns>Виняток для кидка.</returns>
    public static NotFoundException NotFound(int methodologyId)
        => new(
            "ECR-CALC-0404",
            $"Методології {methodologyId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0404.methodology",
                ["methodologyId"] = methodologyId.ToString(CultureInfo.InvariantCulture),
            });
}
