// src/Ecr.Application/Calculations/MethodologyCoverageHandler.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Calculations;

/// <summary>
/// Матриця покриття версії: вихід → колонки шаблонів (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// ⚠ Прив'язки живуть на МЕТОДОЛОГІЇ, виходи — на ВЕРСІЇ. Тому покриття рахується
/// для конкретної версії: нова чернетка, що прибрала вихід, показує колонку, яка
/// після публікації лишиться порожньою, — ще до публікації.
/// </remarks>
public sealed class MethodologyCoverageHandler(
    IMethodologyDraftStore drafts,
    IMethodologyStore methodologies,
    ICalculationBindingStore bindings,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Будує покриття версії.</summary>
    /// <param name="methodologyId">Методологія з адреси.</param>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Покриття.</returns>
    /// <exception cref="NotFoundException"><c>ECR-CALC-0404</c> — версії немає або вона чужа.</exception>
    public async Task<MethodologyCoverageDto> HandleAsync(
        int methodologyId, int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        await MethodologyVersionGuard
            .RequireOwnAsync(drafts, methodologyId, [methodologyVersionId], ct)
            .ConfigureAwait(false);

        var outputs = await methodologies.GetOutputsAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var bound = await bindings.ListAsync(methodologyId, ct).ConfigureAwait(false);

        return Build(methodologyVersionId, outputs, bound);
    }

    /// <summary>Зіставляє виходи версії з прив'язками методології.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="outputs">Оголошені виходи версії.</param>
    /// <param name="bindings">Усі прив'язки методології, включно з вимкненими.</param>
    /// <returns>Покриття.</returns>
    /// <remarks>
    /// ⛔ Вимкнена прив'язка не рахується ні покриттям, ні очікуванням: у прогоні її
    /// немає (<c>RecalculationJob</c> бере лише <c>IsActive</c>). Коди — без
    /// урахування регістру, як у <c>CalculationBindingStore.ListOutputScalesAsync</c>.
    /// </remarks>
    public static MethodologyCoverageDto Build(
        int methodologyVersionId,
        IReadOnlyList<MethodologyOutput> outputs,
        IReadOnlyList<CalculationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(bindings);

        var active = bindings.Where(b => b.IsActive).ToList();
        var declared = outputs.Select(o => o.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = outputs
            .OrderBy(o => o.Ordinal)
            .Select(o => new MethodologyOutputCoverageDto(
                o.Code,
                [.. active
                    .Where(b => string.Equals(b.OutputCode, o.Code, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(b => b.ColumnDefId)
                    .Select(MethodologyAuthoringMap.Binding)]))
            .ToList();

        var waiting = active
            .Where(b => !declared.Contains(b.OutputCode))
            .OrderBy(b => b.ColumnDefId)
            .Select(MethodologyAuthoringMap.Binding)
            .ToList();

        return new MethodologyCoverageDto(methodologyVersionId, covered, waiting);
    }
}

/// <summary>Звірка «версія належить методології з адреси».</summary>
internal static class MethodologyVersionGuard
{
    /// <summary>Кидає 404, якщо хоч одна з версій не належить методології.</summary>
    /// <param name="drafts">Сховище версій.</param>
    /// <param name="methodologyId">Методологія з адреси.</param>
    /// <param name="versionIds">Версії з адреси й запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Без неї адреса <c>/methodologies/1/versions/{версія методології 2}</c> читала б
    /// чужу версію під чужими прив'язками — і відповідь виглядала б правдоподібно.
    /// </remarks>
    public static async Task RequireOwnAsync(
        IMethodologyDraftStore drafts, int methodologyId, IReadOnlyList<int> versionIds, CancellationToken ct)
    {
        var own = (await drafts.GetAllVersionsAsync(methodologyId, ct).ConfigureAwait(false))
            .Select(v => v.Id)
            .ToHashSet();

        foreach (var id in versionIds.Where(id => !own.Contains(id)))
        {
            throw DeleteMethodologyVersionHandler.VersionNotFound(id);
        }
    }
}
