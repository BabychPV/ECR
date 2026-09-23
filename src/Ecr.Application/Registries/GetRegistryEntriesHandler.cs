// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
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
        // (A7-58). Всередині `RegistryAccess.RequireAsync` резолвер довідника
        // з коду (переданий лямбдою) викликається ЛИШЕ тоді, коли глобального
        // Registry.View нема — власник глобального права не платить зайвим
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
            .RequireAsync(
                access, currentUser, Permission, GrantLevel.Read,
                async token => (await registries.FindDefinitionAsync(registryCode, token).ConfigureAwait(false))?.Id,
                ct)
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
        if (definition.IsTemporal && asOf == default)
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
