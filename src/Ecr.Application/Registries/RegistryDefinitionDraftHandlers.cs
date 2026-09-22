// src/Ecr.Application/Registries/RegistryDefinitionDraftHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Registries;

/// <summary>Спільне для чернетки опису довідника (<c>BE-24</c> крок 2).</summary>
internal static class RegistryDraft
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static RegistryDefinitionDraftDto ToDto(RegistryDefinitionDraft draft)
    {
        var content = JsonSerializer.Deserialize<RegistryDefinitionDraftContent>(draft.ContentJson, Json)
                      ?? new RegistryDefinitionDraftContent([], []);

        return new RegistryDefinitionDraftDto(
            draft.BaseDefinitionVersion, content.Fields, content.Rules, draft.Reason,
            draft.UpdatedAt, draft.UpdatedByUserId, Convert.ToBase64String(draft.RowVersion));
    }

    /// <summary>
    /// ⛔ Версію перевіряємо самі, а не токеном EF: токен ловить лише збіг між
    /// читанням і записом цього запиту, а правку втрачають між двома вкладками.
    /// </summary>
    internal static void RequireVersion(RegistryDefinitionDraft? draft, string? expected, string code)
    {
        var actual = draft is null ? null : Convert.ToBase64String(draft.RowVersion);

        if (!string.Equals(actual, string.IsNullOrEmpty(expected) ? null : expected, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(
                "ECR-REG-0409",
                $"Чернетку опису довідника «{code}» змінили або опублікували після того, як її прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0409.definitionDraftChanged",
                    ["registryCode"] = code,
                    ["rowVersion"] = actual,
                });
        }
    }
}

/// <summary>Вміст чернетки: те саме, що приймає пряме збереження опису.</summary>
internal sealed record RegistryDefinitionDraftContent(
    IReadOnlyList<RegistryFieldSaveDto> Fields,
    IReadOnlyList<RegistryRuleSaveDto> Rules);

/// <summary>Чернетка опису довідника, якщо вона є. Право <c>Registry.View</c>.</summary>
public sealed class GetRegistryDefinitionDraftHandler(
    IRegistryStore registries, IRegistryDraftStore drafts,
    Security.IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає чернетку; <c>Draft = null</c> — чернетки немає.</summary>
    public async Task<RegistryDefinitionDraftStateResponse> HandleAsync(string code, CancellationToken ct)
    {
        await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw SaveRegistryDefinitionHandler.RegistryNotFound(code);

        var draft = await drafts.FindAsync(definition.Id, ct).ConfigureAwait(false);

        return new RegistryDefinitionDraftStateResponse(
            definition.DefinitionVersion, draft is null ? null : RegistryDraft.ToDto(draft));
    }
}

/// <summary>
/// Зберігає чернетку опису. Право <c>Registry.EditDefinition</c>. Опублікований
/// опис не змінюється.
/// </summary>
public sealed class SaveRegistryDefinitionDraftHandler(
    IRegistryStore registries,
    IRegistryDraftStore drafts,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну опису довідника.</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Створює або замінює чернетку.</summary>
    /// <exception cref="ConcurrencyConflictException">Версія чернетки чужа — <c>409 ECR-REG-0409</c>.</exception>
    public async Task<RegistryDefinitionDraftDto> HandleAsync(
        string code, SaveRegistryDefinitionDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);
        SaveRegistryDefinitionHandler.RequireReason(request.Reason);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw SaveRegistryDefinitionHandler.RegistryNotFound(code);

        var draft = await drafts.FindAsync(definition.Id, ct).ConfigureAwait(false);
        RegistryDraft.RequireVersion(draft, request.RowVersion, definition.Code);

        var content = JsonSerializer.Serialize(
            new RegistryDefinitionDraftContent(request.Fields ?? [], request.Rules ?? []), RegistryDraft.Json);

        // Збереження чернетки перебазовує її на поточну версію: клієнт надсилає
        // ПОВНИЙ стан, прочитаний разом з опублікованим описом.
        if (draft is null)
        {
            draft = new RegistryDefinitionDraft(
                definition.Id, definition.DefinitionVersion, content, request.Reason, userId, clock.UtcNow);
            drafts.Add(draft);
        }
        else
        {
            draft.Replace(definition.DefinitionVersion, content, request.Reason, userId, clock.UtcNow);
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "cfg.RegistryDefinitionDraft",
                    EntityId: definition.Id,
                    ChangeClass: Domain.Enums.ChangeClass.Guarded,
                    Operation: "SaveDefinitionDraft",
                    OldJson: null,
                    NewJson: content,
                    ChangeReason: request.Reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return RegistryDraft.ToDto(draft);
    }
}

/// <summary>
/// Публікує чернетку опису: застосовує її до довідника тим самим кодом, що й
/// пряме збереження, і прибирає чернетку. Право <c>Registry.Publish</c>.
/// </summary>
public sealed class PublishRegistryDefinitionHandler(
    IRegistryStore registries,
    IRegistryDraftStore drafts,
    SaveRegistryDefinitionHandler apply,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на публікацію опису довідника.</summary>
    public const string Permission = "Registry.Publish";

    /// <summary>Публікує чернетку.</summary>
    /// <returns>Нова версія опублікованого опису.</returns>
    /// <exception cref="NotFoundException">Чернетки немає — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// Чернетку змінили, або опис опублікували повз неї — <c>409 ECR-REG-0409</c>.
    /// </exception>
    public async Task<int> HandleAsync(string code, PublishRegistryDefinitionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw SaveRegistryDefinitionHandler.RegistryNotFound(code);

        var draft = await drafts.FindAsync(definition.Id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"У довідника «{definition.Code}» немає чернетки опису.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.definitionDraft",
                    ["registryCode"] = definition.Code,
                });

        RegistryDraft.RequireVersion(draft, request.RowVersion, definition.Code);

        // ⛔ Опис змінили повз чернетку після її останнього збереження:
        // публікація мовчки затерла б ту зміну повним станом чернетки.
        if (draft.BaseDefinitionVersion != definition.DefinitionVersion)
        {
            throw new ConcurrencyConflictException(
                "ECR-REG-0409",
                $"Опис довідника «{definition.Code}» змінився після останнього збереження чернетки.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0409.definitionDraftStale",
                    ["registryCode"] = definition.Code,
                    ["baseVersion"] = draft.BaseDefinitionVersion.ToString(CultureInfo.InvariantCulture),
                    ["currentVersion"] = definition.DefinitionVersion.ToString(CultureInfo.InvariantCulture),
                });
        }

        var content = RegistryDraft.ToDto(draft);

        // Видалення — у тій самій транзакції, що й застосування: відмова
        // валідації (422) кидається до SaveChanges і лишає чернетку цілою.
        drafts.Remove(draft);

        return await apply
            .ApplyAsync(
                definition,
                new SaveRegistryDefinitionDto(content.Fields, content.Rules, draft.Reason),
                "PublishDefinition",
                userId,
                ct)
            .ConfigureAwait(false);
    }
}
