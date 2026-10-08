// src/Ecr.Application/Registries/Impact/RegistryImpactHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries.Impact;

/// <summary>Документ відкритого періоду, зачеплений правкою довідника.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="BusinessKey">Бізнес-ключ документа.</param>
/// <param name="PeriodKey">Період результатів.</param>
/// <param name="PeriodState"><c>Open</c> або <c>Grace</c>.</param>
/// <param name="Via">Через що: <c>methodology:&lt;код&gt;</c>, впорядковано.</param>
public sealed record RegistryImpactItemDto(
    long DocumentId, string BusinessKey, int PeriodKey, string PeriodState, IReadOnlyList<string> Via);

/// <summary>Відповідь <c>GET /registries/{code}/impact</c>.</summary>
/// <param name="Items">Зачеплені документи, які бачить викликач.</param>
/// <param name="Total">Скільки їх усього (без обрізання переліку, але в межах <see cref="Truncated"/>).</param>
/// <param name="Truncated"><c>true</c> — вибірка вперлась у стелю, реальних документів більше.</param>
public sealed record RegistryImpactResponse(IReadOnlyList<RegistryImpactItemDto> Items, int Total, bool Truncated)
{
    /// <summary>Скільки елементів віддається в одній відповіді.</summary>
    public const int PageSize = 500;
}

/// <summary>
/// «Що зачепила правка довідника» (RT-25, §5.10): документи ВІДКРИТИХ періодів, чиї результати
/// пораховано методологією, що читає довідник.
/// </summary>
/// <remarks>
/// ⛔ Перерахунок тут НЕ ставиться (<c>R-14</c>): одна правка складу — це десятки збережень, а
/// бюджет прогону обмежений (<c>D-63</c>). Це читання; постановка — окрема дія людини.
/// <para>
/// ⚠ Право двох рівнів: <c>Registry.View</c> (довідник) і <c>Calculation.View</c> у ПРОЄКТІ документа —
/// без другого перелік розкрив би існування й бізнес-ключі чужих документів.
/// </para>
/// </remarks>
public sealed class GetRegistryImpactHandler(
    IRegistryStore registries,
    IRegistryImpactStore impact,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на довідник (`02-contracts.md` §9).</summary>
    public const string RegistryPermission = "Registry.View";

    /// <summary>Право на результати розрахунку в проєкті документа.</summary>
    public const string CalculationPermission = "Calculation.View";

    /// <summary>Повертає зачеплені документи довідника.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException">Довідника немає або він схований забороною — <c>ECR-REG-0404</c>.</exception>
    public async Task<RegistryImpactResponse> HandleAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        await RegistryAccess
            .RequireAsync(access, currentUser, RegistryPermission, GrantLevel.Read, new RegistryLookup(registries, code), ct)
            .ConfigureAwait(false);

        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, CalculationPermission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw RegistryAccess.NotFound(code);

        // ⛔ S18: лише документи, які викликач БАЧИТЬ і де має право на результати, — фільтр іде у
        // вибірку, до стелі. Роль без області дає право всюди, але без гранта на проєкт (чи з
        // забороною) документи проєкту невидимі: перелік розкривав би їх бізнес-ключі.
        var projects = Documents.ListDocumentsHandler.ReadableProjects(profile, CalculationPermission);

        var rows = await impact
            .ListImpactedAsync(definition.Id, projects, IRegistryImpactStore.MaxRows, ct)
            .ConfigureAwait(false);

        // ⛔ Читачу зі схованим аркушем/таблицею/колонкою/періодом перелік не розкриває документ і код методології
        // (виводяться з результатів усього документа). `Truncated` - за сирою вибіркою: вона лише каже «більше за стелю».
        var truncated = rows.Count >= IRegistryImpactStore.MaxRows;
        rows = await RegistryImpactVisibility.ForReaderAsync(access, profile, rows, ct).ConfigureAwait(false);

        // ⚠ Групування по «документ × період»: документ, зачеплений двома методологіями, — один
        // рядок із двома «via». Порядок першої появи зберігає впорядкування сховища.
        var byKey = new Dictionary<(long, int), (RegistryImpactRow Row, List<string> Via)>();
        var order = new List<(long, int)>();

        foreach (var row in rows)
        {
            // ⛔ Право саме в проєкті документа (ФВ-6.14), а не «десь», і видимість документа (S18) —
            // другий рубіж після фільтра вибірки.
            if (!profile.SeesDocumentsOf(row.ProjectId)
                || !PermissionCheck.IsGrantedIn(profile, CalculationPermission, row.ProjectId))
            {
                continue;
            }

            var key = (row.DocumentId, row.PeriodKey);
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (row, []);
                byKey[key] = entry;
                order.Add(key);
            }

            var via = "methodology:" + row.MethodologyCode;
            if (!entry.Via.Contains(via))
            {
                entry.Via.Add(via);
            }
        }

        var items = new List<RegistryImpactItemDto>();
        foreach (var key in order)
        {
            var (row, via) = byKey[key];
            via.Sort(StringComparer.Ordinal);
            items.Add(new RegistryImpactItemDto(
                row.DocumentId,
                row.BusinessKey,
                row.PeriodKey,
                row.PeriodState.ToString(),
                via));
        }

        return new RegistryImpactResponse(
            items.Count > RegistryImpactResponse.PageSize ? items.GetRange(0, RegistryImpactResponse.PageSize) : items,
            items.Count,
            Truncated: truncated);
    }
}
