using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Патчить презентаційний шар **опублікованої** версії «на льоту»: підписи,
/// стилі, <c>Ordinal</c>, формати (ФВ-7.2).
/// </summary>
/// <remarks>
/// Це та операція, заради якої існує <c>PresentationRevision</c>: користувач
/// може виправити підпис колонки без клонування версії і без міграції даних.
/// </remarks>
public sealed class PatchPresentationHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Застосовує презентаційні зміни.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="patchJson">Перелік змін у форматі <c>{entityType, entityId, field, value}</c>.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Нове значення <c>PresentationRevision</c>.</returns>
    /// <exception cref="BusinessRuleException">
    /// Серед змін є структурна — <c>ECR-TMPL-0409</c>.
    /// </exception>
    public async Task<int> PatchAsync(int templateVersionId, string patchJson, int userId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.Edit", ct)
            .ConfigureAwait(false);

        ArgumentException.ThrowIfNullOrWhiteSpace(patchJson);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var changes = Parse(patchJson);

        if (changes.Count == 0)
        {
            throw new BusinessRuleException("ECR-TMPL-0422", "Порожній патч: змінювати нічого.");
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Спершу класифікуємо ВСІ зміни і лише потім вирішуємо. Часткове
        // застосування неприпустиме: користувач надіслав патч як одне ціле, і
        // побачити половину застосованих правок гірше, ніж не побачити жодної.
        var violations = changes
            .Where(c => classifier.Classify(c.EntityType, c.Field, hasDocuments) != ChangeClass.Presentation)
            .Select(c => $"{c.EntityType}.{c.Field}")
            .ToList();

        if (violations.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0409",
                "Патч містить структурні зміни, які в опублікованій версії заборонені: " +
                string.Join(", ", violations) + ". Структурні зміни вносяться клонуванням версії (ФВ-7.1).",
                new Dictionary<string, object?> { ["structuralFields"] = violations });
        }

        // ⛔ ЗМІНА ЗАСТОСОВУЄТЬСЯ. Цього рядка тут не було: обробник розбирав
        // патч, класифікував кожну зміну, відхиляв структурні, піднімав
        // ревізію і писав аудит — і не міняв жодного поля. Підпис колонки
        // лишався старим, ключ кешу ставав новим, і всі клієнти перечитували
        // структуру, щоб побачити те саме. Аудит при цьому запевняв, що зміна
        // відбулася: у `NewJson` лежало значення, якого в базі не було.
        //
        // ⚠ ПЕРЕД інкрементом ревізії: якщо запис упаде, ключ кешу не має
        // змінитися. Новий ключ на стару структуру — це те саме розходження,
        // тільки навпаки.
        await store.ApplyPresentationAsync(templateVersionId, changes, ct).ConfigureAwait(false);

        // Інкремент — атомарний statement із OUTPUT (R-B7). Застосунок не
        // призначає нову ревізію, а дізнається її: інстансів ≥ 2.
        var newRevision = await store.IncrementPresentationRevisionAsync(templateVersionId, ct).ConfigureAwait(false);
        version.ApplyPresentationRevision(newRevision);

        var now = clock.UtcNow;
        foreach (var change in changes)
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    now, templateVersionId, change.EntityType, change.EntityId,
                    ChangeClass.Presentation, "Update",
                    OldJson: null, NewJson: change.Value, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return newRevision;
    }

    private static readonly JsonSerializerOptions PatchJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<PresentationChange> Parse(string patchJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PresentationChange>>(patchJson, PatchJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new BusinessRuleException("ECR-TMPL-0422", $"Патч не є коректним JSON: {ex.Message}");
        }
    }
}
