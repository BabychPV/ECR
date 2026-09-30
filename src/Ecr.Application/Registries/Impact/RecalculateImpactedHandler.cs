// src/Ecr.Application/Registries/Impact/RecalculateImpactedHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Impact;

/// <summary>Тіло <c>POST /registries/{code}/recalculate-impacted</c>.</summary>
/// <param name="DocumentIds">Документи з переліку <c>impact</c>; <c>null</c> — усі, до яких є право.</param>
/// <param name="Reason">Причина — обов'язкова.</param>
public sealed record RecalculateImpactedRequest(IReadOnlyList<long>? DocumentIds, string? Reason);

/// <summary>
/// Ставить у чергу перерахунок документів, зачеплених правкою довідника (RT-25, §5.10).
/// </summary>
/// <remarks>
/// ⛔ Перерахунок ніколи не автоматичний (<c>R-14</c>): це дія людини над переліком, який вона щойно
/// бачила в <see cref="GetRegistryImpactHandler"/>. Закритий період у переліку неможливий — сервер
/// бере набір з того самого джерела, що й <c>GET …/impact</c>, тож документ поза ним відмовляється
/// <c>422</c> (<c>D-39</c>).
/// <para>
/// ⚠ Право двох рівнів: <c>Calculation.Recalculate</c> у проєкті КОЖНОГО документа (без
/// <c>documentIds</c> — лише документи, до яких воно є; з ними — відмова <c>403</c> на першому чужому)
/// і доступ до довідника.
/// </para>
/// <para>
/// ⚠ Постановка — ОДНА батьківська задача (<see cref="IRegistryImpactRecalculationJob"/>), а не
/// цикл <c>EnqueueCoalescedAsync</c> тут: один запит дає один <c>jobId</c>, а розкладання на
/// документи робить задача (злиття без витіснення, <c>EnqueueExclusive</c> заборонено).
/// </para>
/// </remarks>
public sealed class RecalculateImpactedHandler(
    IRegistryStore registries,
    IRegistryImpactStore impact,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на запуск перерахунку (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.Recalculate";

    /// <summary>Найдовша причина.</summary>
    public const int MaxReasonLength = 400;

    /// <summary>Ставить перерахунок; повертає ідентифікатор батьківської задачі.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Набір документів і причина.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає або він схований забороною — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Причини немає, документ не зачеплений або не лишилось що рахувати — 422.</exception>
    public async Task<string> HandleAsync(string code, RecalculateImpactedRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(request);

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Перерахунок зачеплених документів потребує причини.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.impactReasonRequired" });
        }

        if (reason.Length > MaxReasonLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Причина довша за {MaxReasonLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.impactReasonTooLong",
                    ["max"] = MaxReasonLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        await RegistryAccess
            .RequireAsync(access, currentUser, GetRegistryImpactHandler.RegistryPermission, GrantLevel.Read, new RegistryLookup(registries, code), ct)
            .ConfigureAwait(false);

        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw RegistryAccess.NotFound(code);

        var rows = await impact
            .ListImpactedAsync(definition.Id, IRegistryImpactStore.MaxRows, ct)
            .ConfigureAwait(false);

        var projectOf = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            projectOf[row.DocumentId] = row.ProjectId;
        }

        var documentIds = new List<long>();
        if (request.DocumentIds is null)
        {
            foreach (var (documentId, projectId) in projectOf)
            {
                if (PermissionCheck.IsGrantedIn(profile, Permission, projectId))
                {
                    documentIds.Add(documentId);
                }
            }
        }
        else
        {
            foreach (var documentId in request.DocumentIds.Distinct())
            {
                // ⛔ Документ поза переліком — і закритий період, і «не залежить від довідника»: сервер
                // не розкриває різниці (перелік `impact` — єдине, що людина вже бачила).
                if (!projectOf.TryGetValue(documentId, out var projectId))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Документ {documentId} не входить до зачеплених відкритих періодів довідника.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.impactDocumentNotAffected",
                            ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                        });
                }

                PermissionCheck.RequireIn(profile, Permission, projectId);
                documentIds.Add(documentId);
            }
        }

        if (documentIds.Count == 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Немає зачеплених документів відкритих періодів, які можна перерахувати.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.impactNothingToRecalculate" });
        }

        documentIds.Sort();

        return await jobs
            .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                RegistryImpactRecalculationTarget.Of(definition.Id, documentIds),
                new RegistryImpactRecalculationRequest(definition.Id, documentIds, reason),
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);
    }
}
