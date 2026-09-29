// src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>Створення і зміна запису довідника (ФВ-8.6, ФВ-8.7).</summary>
/// <remarks>
/// ⚠ Значення, ключі, ревізія й аудит — через <see cref="RegistryEntryWriter"/> (S6): тут лише
/// те, що належить саме інтерактивному запиту, — право, пошук чи створення запису за Id/кодом,
/// назва й батько.
/// </remarks>
public sealed class UpsertRegistryEntryHandler(
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    RegistryEntryWriter writer)
{
    /// <summary>
    /// Право на зміну ДАНИХ довідника (`02-contracts.md` §9).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Registry.EditData</c> і <c>Registry.EditDefinition</c> — різні
    /// права: змінювати значення і змінювати склад полів довідника може не
    /// той самий користувач.
    /// </remarks>
    public const string Permission = "Registry.EditData";

    /// <summary>Тип події журналу безпеки.</summary>
    /// <remarks>
    /// ⚠ Досі зміна ЗНАЧЕННЯ поля запису довідника не лишала жодного сліду —
    /// на відміну від зміни ОПИСУ довідника (<c>SaveRegistryDefinitionHandler</c>,
    /// <c>aud.StructureChange</c>) чи зміни комірки документа
    /// (<c>aud.CellChange</c>). Подія пишеться в <c>aud.SecurityEvent</c> —
    /// той самий журнал, що вже приймає довільні події через
    /// <c>EventType</c>/<c>DetailsJson</c> (<c>DocumentKeyChanged</c>,
    /// <c>UserLocked</c> тощо) — окрема таблиця історії значень не завелася б
    /// без міграції, а її тут свідомо нема (правило проєкту: одна міграція за
    /// раз, паралельно вже йде інша).
    /// </remarks>
    public const string ValueChangedEventType = RegistryEntryWriter.ValueChangedEventType;

    /// <summary>Створює або оновлює запис і повертає його ідентифікатор.</summary>
    /// <param name="dto">Опис запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника або запису немає.</exception>
    /// <exception cref="BusinessRuleException">Код зайнятий або невалідний.</exception>
    public async Task<long> HandleAsync(RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // ⚠ Глобальне право АБО ресурсний грант рівня Write на ЦЕЙ довідник
        // (A7-58): dto.RegistryDefId уже відомий з запиту, тож жодного
        // додаткового походу в базу перевірка не додає.
        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Write, dto.RegistryDefId, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var definition = await registries.FindDefinitionByIdAsync(dto.RegistryDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника {dto.RegistryDefId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryId",
                    ["registryDefId"] = dto.RegistryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Код валідується як EcrCode (D-89) — тим самим правилом, що коди
        // колонок і шаблонів. Окреме «майже таке саме» правило для довідників
        // розійшлося б із рештою системи на першому ж символі.
        //
        // ⛔ RT-12 (D-157): у довіднику з `CodeMode = Auto` код НОВОГО запису видає послідовність
        // — через writer, як і в CSV та пакеті. Код у запиті тоді має бути порожнім: чужа шкала
        // кодів у тому самому довіднику зробила б пошук за кодом неоднозначним. Для наявного
        // запису (`Id`) код не змінюється в жодному режимі, тож і не перевіряється тут.
        var auto = definition.CodeMode == RegistryCodeMode.Auto;
        EcrCode? code = auto ? null : EcrCode.Create(dto.Code);

        var entry = dto.Id is { } id
            ? await LoadAsync(id, definition.Id, ct).ConfigureAwait(false)
            : await CreateAsync(
                definition.Id,
                code ?? await AutoCodeAsync(definition, dto.Code, ct).ConfigureAwait(false),
                dto,
                userId,
                ct).ConfigureAwait(false);

        // ⛔ X-03: назва ЗЛИВАЄТЬСЯ з наявною, а не заміняється. Доти
        // `Rename(dto.Display)` писав рівно те, що приїхало, — а форма правки
        // (до фіксу клієнта) везла назву лише під `en`, і кожне збереження
        // запису мовчки стирало російський і казахський переклади. Мова, якої
        // в запиті немає, лишається як була; мова з порожнім текстом —
        // свідомо прибирається (так клієнт каже «цей переклад видалено»).
        entry.Rename(dto.Id is null ? WithoutEmpty(dto.Display) : Merge(entry.DisplayL10n, dto.Display));
        entry.SetParent(dto.ParentEntryId);

        var changes = await writer.ApplyValuesAsync(definition, entry, dto.Values, prefetch: null, ct).ConfigureAwait(false);

        // ⛔ Вікно дії сюди НЕ приймається, хоча воно є полем запису: його
        // зміна тягне перерахунок IsOrphaned (ФВ-8.13a), і зроблена мимохідь
        // тут вона лишила б рядки з ознакою, яку ніхто не перерахував. Для
        // цього є SetEntryValidityHandler.

        // Ревізія (завжди, навіть коли змінилася лише назва), складений ключ RT-10a у
        // транзакції збереження, і ОДНА подія аудиту на всі змінені поля — після коміту.
        await writer.SaveEntryAsync(definition, entry, changes, userId, ct).ConfigureAwait(false);

        return entry.Id;
    }

    /// <summary>Наявна назва, поверх якої лягли мови з запиту (X-03).</summary>
    /// <remarks>
    /// ⚠ Порожній або пробільний текст мови в запиті — видалення цього
    /// перекладу; мова, якої в запиті немає зовсім, — без змін.
    /// </remarks>
    internal static LocalizedText Merge(LocalizedText current, LocalizedText? incoming)
    {
        var merged = new Dictionary<string, string>(current.Values, StringComparer.OrdinalIgnoreCase);

        foreach (var (language, text) in incoming?.Values ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                merged.Remove(language);
            }
            else
            {
                merged[language] = text;
            }
        }

        return new LocalizedText(merged);
    }

    /// <summary>Назва нового запису без порожніх мов.</summary>
    private static LocalizedText WithoutEmpty(LocalizedText? display)
        => Merge(new LocalizedText(), display);

    /// <summary>Код нового запису довідника з <c>CodeMode = Auto</c> (<c>D-157</c>).</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, <c>entryCodeAutomatic</c>: запит назвав свій код.</exception>
    private async Task<EcrCode> AutoCodeAsync(RegistryDef definition, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Коди записів довідника «{definition.Code}» видає система: для нового запису код лишають порожнім.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.entryCodeAutomatic",
                    ["registryCode"] = definition.Code,
                    ["code"] = requested,
                });
        }

        return (await writer.ReserveAutoCodesAsync(definition, 1, ct).ConfigureAwait(false)).Dequeue();
    }

    private async Task<RegistryEntry> LoadAsync(long id, int registryDefId, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Запису довідника {id} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Переносити запис між довідниками не можна: у комірках лежить його Id,
        // а колонка оголошує LookupRegistryDefId — після переносу значення
        // лишилося б валідним числом і невалідним посиланням.
        if (entry.RegistryDefId != registryDefId)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Запис {id} належить довіднику {entry.RegistryDefId}, а не {registryDefId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.entryWrongRegistry",
                    ["entryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ownerRegistryDefId"] = entry.RegistryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["registryDefId"] = registryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return entry;
    }

    private async Task<RegistryEntry> CreateAsync(
        int registryDefId, EcrCode code, RegistryEntryUpsertDto dto, int userId, CancellationToken ct)
    {
        var duplicate = await registries.FindEntryByCodeAsync(registryDefId, code.Value, ct).ConfigureAwait(false);
        if (duplicate is not null)
        {
            // ⚠ Саме 409, а не «оновити знайдений»: тихе злиття з однойменним
            // записом підмінило б Id у нових комірках, і два різні об'єкти
            // стали б одним заднім числом.
            throw new BusinessRuleException(
                "ECR-REG-0409",
                $"Запис із кодом «{code.Value}» у цьому довіднику вже існує (Id {duplicate.Id}).",
                // ⛔ Q-30x: узагальнений шлях ExceptionHandlingMiddleware
                // (messageKey) — без нього подробиця доїжджала клієнту сирим
                // українським реченням незалежно від мови інтерфейсу.
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0409.entryCodeTaken",
                    ["code"] = code.Value,
                    ["id"] = duplicate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return writer.AddEntry(registryDefId, code, dto.Display, userId);
    }
}
