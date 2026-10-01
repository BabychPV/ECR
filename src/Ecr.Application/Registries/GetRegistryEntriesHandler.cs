// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

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
        // ⚠ Глобальне право АБО ресурсний грант рівня Read на ЦЕЙ довідник
        // (A7-58). Всередині `RegistryAccess.RequireAsync` довідник з коду
        // (`RegistryLookup`) резолвиться ЛИШЕ тоді, коли глобального
        // Registry.View нема або в профілі є заборони на довідники (S18) —
        // власник глобального права без заборон не платить зайвим
        // FindDefinitionAsync ТУТ.
        //
        // ⚠ Явний виклик нижче — ОКРЕМИЙ похід у базу, потрібен незалежно від
        // результату перевірки права: гейт asOf-обов'язковості за
        // `definition.IsTemporal` (коментар нижче) фізично вимагає визначення
        // ДО перевірки asOf, тобто для КОЖНОГО користувача, а не лише для
        // того, хто йде через ресурсний грант. `RegistryEntriesAsOfValidationTests`
        // більше не тримає зворотної інваріанти («похід у базу не випереджає
        // asOf») — вона й була джерелом дефекту: Lookup нетемпорального
        // довідника не працював НІКОЛИ, бо перевірка asOf ішла раніше, ніж
        // хтось встигав дізнатися, що довідник нетемпоральний.
        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Read, new RegistryLookup(registries, registryCode), ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        // ⛔ Відсутній або зіпсований `asOf` — ВІДМОВА, а не `0001-01-01`
        // (аудит 2026-09-16, §9), АЛЕ лише для ТЕМПОРАЛЬНОГО довідника
        // (`RegistryDef.IsTemporal`). Довідник без вікна чинності (`ValidFrom`/
        // `ValidTo` завжди `null`, `ValidityWindow.Contains` тоді true для
        // будь-якої дати) не має «дати періоду», на яку залежав би перелік
        // записів — вимагати asOf для нього означало б вимагати параметр, який
        // нічого не змінює. До цього фіксу перевірка йшла БЕЗУМОВНО, ДО фетчу
        // визначення, і Lookup-піцкер нетемпорального довідника (наприклад,
        // Substance) був непрацездатним завжди: жоден клієнтський виклик
        // `GET …/entries` не передавав asOf, і кожен такий запит падав
        // `422 ECR-REQ-0422`, незалежно від прапорця.
        //
        // Для ТЕМПОРАЛЬНОГО довідника застереження лишається чинним: `[FromQuery]
        // DateOnly` без значення резолвиться у `default` — дату, на яку жоден
        // темпоральний запис не чинний, — і мовчазна підстановка збрехала б
        // про «довідник порожній» там, де насправді забули дату періоду.
        // ⛔ RT-11 (борг RT-12, D-155): частину композиції видно рівно тоді, коли видно батька —
        // рекурсивно вгору. Тому ланцюжок батьківських довідників читається ДО ключа кешу: їхні
        // ревізії входять у ключ, інакше видалення кейсу не сховало б його склад у пікері, доки
        // не зміниться сам довідник складу.
        var chain = await CompositionChainAsync(definition, ct).ConfigureAwait(false);

        // ⚠ Дата потрібна й тоді, коли темпоральний не сам довідник, а його батько композиції:
        // перелік частин залежить від того, чи чинний батько на дату періоду.
        if ((definition.IsTemporal || chain.Exists(link => link.Parent.IsTemporal)) && asOf == default)
        {
            throw new BusinessRuleException(
                Domain.Errors.ErrorCodes.RequestInvalid,
                "Параметр asOf обов'язковий: довідник темпоральний, і перелік записів "
                + "залежить від дати періоду, а не від «сьогодні».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.asOfRequired",
                    ["parameter"] = "asOf",
                });
        }

        // ⚠ DataRevision у ключі, а не час життя: та сама схема, що з
        // метаданими (D-16). Запис довідника змінили — ключ інший, старе
        // значення нікому не заважає і не потребує інвалідації між інстансами.
        //
        // asOf теж у ключі: той самий довідник на різні дати — різні списки, і
        // спільний запис віддавав би березневий перелік у жовтневому документі.
        var parents = string.Concat(chain.Select(link =>
            $":c{link.Parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"r{link.Parent.DataRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var key = $"reg:{definition.Code}:r{definition.DataRevision}{parents}:{asOf:yyyy-MM-dd}:p{parentEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";

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

                var own = resolver.Select(entries, links, asOf, parentEntryId);
                if (chain.Count == 0 || own.Count == 0)
                {
                    return own;
                }

                var visible = await CompositionVisibilityAsync(entries, chain, asOf, token).ConfigureAwait(false);
                return own.Where(e => visible(e.Id)).ToList();
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

    /// <summary>
    /// Ланцюжок композиції вгору: довідник-дитина → поле композиції → довідник-батько → … (<c>D-155</c>).
    /// Порожній — довідник не є частиною іншого.
    /// </summary>
    /// <remarks>
    /// ⚠ Цикл композицій забороняє опис (<c>RegistryCompositionRules</c>), але дані могли прийти
    /// повз нього — обхід зупиняється на вже баченому довіднику, а не зациклюється.
    /// </remarks>
    private async Task<List<CompositionLink>> CompositionChainAsync(RegistryDef definition, CancellationToken ct)
    {
        var chain = new List<CompositionLink>();
        var seen = new HashSet<int> { definition.Id };
        var child = definition;

        while (child.Fields.FirstOrDefault(f => f.RelationKind == RegistryRelationKind.Composition
                                                && f.RefRegistryDefId is not null) is { } field
               && seen.Add(field.RefRegistryDefId!.Value)
               && await registries.FindDefinitionByIdAsync(field.RefRegistryDefId.Value, ct).ConfigureAwait(false) is { } parent)
        {
            chain.Add(new CompositionLink(child, field, parent));
            child = parent;
        }

        return chain;
    }

    /// <summary>
    /// Предикат «запис видно разом із батьками композиції» — одне правило на всіх споживачів
    /// (<see cref="RegistryResolver.VisibleWithCompositionParents"/>, той самий, що в знімку для формул).
    /// </summary>
    private async Task<Func<long, bool>> CompositionVisibilityAsync(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyList<CompositionLink> chain,
        DateOnly asOf,
        CancellationToken ct)
    {
        var byId = new Dictionary<long, RegistryEntry>();
        var registryOf = new Dictionary<long, int>();
        var parentOf = new Dictionary<long, long?>();
        var children = chain.Select(link => link.Child.Id).ToHashSet();

        void Remember(IEnumerable<RegistryEntry> set)
        {
            foreach (var entry in set)
            {
                byId[entry.Id] = entry;
                registryOf[entry.Id] = entry.RegistryDefId;
            }
        }

        Remember(entries);
        IReadOnlyList<RegistryEntry> level = entries;

        foreach (var link in chain)
        {
            // Значення поля композиції кожного запису рівня — його батько.
            var values = await registries
                .ListValuesForEntriesAsync([.. level.Select(e => e.Id)], ct)
                .ConfigureAwait(false);
            foreach (var value in values.Where(v => v.RegistryFieldDefId == link.Field.Id))
            {
                parentOf[value.RegistryEntryId] = value.ValueRefEntryId;
            }

            level = await registries.ListEntriesAsync(link.Parent.Id, ct).ConfigureAwait(false);
            Remember(level);
        }

        return resolver.VisibleWithCompositionParents(
            id => byId.TryGetValue(id, out var entry) && resolver.IsSelectable(entry, asOf),
            id => registryOf.TryGetValue(id, out var registry) && children.Contains(registry)
                ? (true, parentOf.GetValueOrDefault(id))
                : (false, null));
    }

    /// <summary>Ланка композиції: довідник-дитина, його поле композиції і довідник-батько.</summary>
    private sealed record CompositionLink(RegistryDef Child, RegistryFieldDef Field, RegistryDef Parent);
}
