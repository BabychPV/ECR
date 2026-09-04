// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Registries;

/// <summary>
/// Записи довідника **станом на дату періоду**, а не «активні зараз»
/// (ФВ-8.5).
/// </summary>
/// <remarks>
/// Різниця принципова: документ за березень має бачити дозволи, чинні в
/// березні, навіть якщо сьогодні жовтень і половина з них уже недійсна.
/// </remarks>
public sealed class GetRegistryEntriesHandler(
    IRegistryStore registries,
    RegistryResolver resolver,
    IRegistryEntryCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає записи довідника на дату.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <param name="parentEntryId">Обраний батьківський запис для каскаду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<IReadOnlyList<RegistryEntryDto>> HandleAsync(
        string registryCode, DateOnly asOf, long? parentEntryId, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{registryCode}» не існує.");

        // ⚠ DataRevision у ключі, а не час життя: та сама схема, що з
        // метаданими (D-16). Запис довідника змінили — ключ інший, старе
        // значення нікому не заважає і не потребує інвалідації між інстансами.
        //
        // asOf теж у ключі: той самий довідник на різні дати — різні списки, і
        // спільний запис віддавав би березневий перелік у жовтневому документі.
        var key = $"reg:{definition.Code}:r{definition.DataRevision}:{asOf:yyyy-MM-dd}:p{parentEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";

        var selected = await cache.GetOrAddAsync(
            key,
            async token =>
            {
                var entries = await registries.ListEntriesAsync(definition.Id, token).ConfigureAwait(false);

                // Зв'язки читаються лише коли є що звужувати: без каскаду це
                // зайвий запит на кожне відкриття випадного списку.
                IReadOnlyList<RegistryEntryLink> links = parentEntryId is null
                    ? []
                    : await registries.ListInboundLinksAsync(definition.Id, token).ConfigureAwait(false);

                return resolver.Select(entries, links, asOf, parentEntryId);
            },
            ct).ConfigureAwait(false);

        // Id і Display: у комірці зберігається Id (ФВ-8.8), Display лише
        // показується. Тому перейменування не змінює історичних даних.
        return selected
            .Select(e => new RegistryEntryDto(
                e.Id,
                e.Code,
                e.DisplayL10n.Get(currentUser.Language) ?? e.Code,
                e.ParentEntryId,
                e.ValidFrom,
                e.ValidTo))
            .ToList();
    }
}
