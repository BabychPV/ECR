// src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>Створення і зміна запису довідника (ФВ-8.6, ФВ-8.7).</summary>
public sealed class UpsertRegistryEntryHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
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

    /// <summary>Створює або оновлює запис і повертає його ідентифікатор.</summary>
    /// <param name="dto">Опис запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника або запису немає.</exception>
    /// <exception cref="BusinessRuleException">Код зайнятий або невалідний.</exception>
    public async Task<long> HandleAsync(RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        var definition = await registries.FindDefinitionByIdAsync(dto.RegistryDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника {dto.RegistryDefId} не існує.");

        // Код валідується як EcrCode (D-89) — тим самим правилом, що коди
        // колонок і шаблонів. Окреме «майже таке саме» правило для довідників
        // розійшлося б із рештою системи на першому ж символі.
        var code = EcrCode.Create(dto.Code);

        var entry = dto.Id is { } id
            ? await LoadAsync(id, definition.Id, ct).ConfigureAwait(false)
            : await CreateAsync(definition.Id, code, dto, userId, ct).ConfigureAwait(false);

        entry.Rename(dto.Display);
        entry.SetParent(dto.ParentEntryId);

        await ApplyValuesAsync(definition, entry, dto.Values, ct).ConfigureAwait(false);

        // ⛔ Вікно дії сюди НЕ приймається, хоча воно є полем запису: його
        // зміна тягне перерахунок IsOrphaned (ФВ-8.13a), і зроблена мимохідь
        // тут вона лишила б рядки з ознакою, яку ніхто не перерахував. Для
        // цього є SetEntryValidityHandler.

        // ⚠ Ревізія рухається ЗАВЖДИ, навіть коли змінилася лише назва: у
        // ключі кешу списків лежить саме вона, і без інкременту grid показував
        // би старий підпис, поки хтось не перезапустить процес.
        definition.BumpDataRevision();

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return entry.Id;
    }

    /// <summary>Записує значення полів типізовано за <c>RegistryFieldDef.DataType</c>.</summary>
    /// <remarks>
    /// ⚠ Тип береться з опису поля, а не з типу переданого об'єкта. Інакше
    /// число, що прийшло рядком із JSON, лягло б у <c>ValueString</c> — і поле
    /// «ліміт» перестало б порівнюватися й сумуватися, не давши жодної помилки.
    /// </remarks>
    private async Task ApplyValuesAsync(
        Domain.Entities.Configuration.RegistryDef definition,
        RegistryEntry entry,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var fields = definition.Fields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        var unknown = values.Keys.Where(code => !fields.ContainsKey(code)).ToList();
        if (unknown.Count > 0)
        {
            // Невідоме поле — це або друкарська помилка, або клієнт іншої
            // версії. Обидва випадки треба показати: мовчки відкинуте значення
            // виглядає як збережене.
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» не має полів: {string.Join(", ", unknown)}.");
        }

        var existing = entry.IsPersisted
            ? (await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false))
                .ToDictionary(v => v.RegistryFieldDefId)
            : [];

        foreach (var (code, raw) in values)
        {
            var field = fields[code];

            if (!existing.TryGetValue(field.Id, out var value))
            {
                value = new RegistryValue(entry, field.Id);
                registries.AddValue(value);
            }

            value.Set(field.DataType, raw, field.UnitId);
        }

        var missing = definition.Fields
            .Where(f => f.IsRequired)
            .Where(f => !values.TryGetValue(f.Code, out var v) || v is null)
            .Where(f => !existing.ContainsKey(f.Id))
            .Select(f => f.Code)
            .ToList();

        if (missing.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Не заповнені обов'язкові поля довідника «{definition.Code}»: {string.Join(", ", missing)}.");
        }
    }

    private async Task<RegistryEntry> LoadAsync(long id, int registryDefId, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Запису довідника {id} не існує.");

        // Переносити запис між довідниками не можна: у комірках лежить його Id,
        // а колонка оголошує LookupRegistryDefId — після переносу значення
        // лишилося б валідним числом і невалідним посиланням.
        if (entry.RegistryDefId != registryDefId)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Запис {id} належить довіднику {entry.RegistryDefId}, а не {registryDefId}.");
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
                $"Запис із кодом «{code.Value}» у цьому довіднику вже існує (Id {duplicate.Id}).");
        }

        var entry = new RegistryEntry(registryDefId, code, dto.Display, userId, clock.UtcNow);
        registries.Add(entry);
        return entry;
    }
}
