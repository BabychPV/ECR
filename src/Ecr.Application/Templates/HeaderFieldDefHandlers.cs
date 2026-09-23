// src/Ecr.Application/Templates/HeaderFieldDefHandlers.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Поля шапки документа версії шаблону: рівень усього документа, не таблиці
/// (за зразком <see cref="SaveColumnDefHandler"/>, але без прив'язки до
/// <c>TableDefId</c>).
/// </summary>
public sealed class GetHeaderFieldDefsHandler(
    IMetadataCache metadata,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Повертає поля шапки версії, з кешу структури (той самий шлях, що <c>GET …/structure</c>).</summary>
    public async Task<IReadOnlyList<HeaderFieldDefDto>> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        return [.. snapshot.HeaderFields.OrderBy(f => f.Ordinal).Select(Map)];
    }

    internal static HeaderFieldDefDto Map(HeaderFieldDef field)
        => new(
            field.Id, field.Code, field.LabelL10n, field.Ordinal, field.DataType,
            field.IsRequired, field.LookupRegistryDefId);
}

/// <summary>
/// Записує (створює або змінює) поле шапки версії-чернетки — той самий
/// draft→publish шлях, що <see cref="SaveColumnDefHandler"/>: жодного
/// окремого механізму чернетки для шапки не заводиться.
/// </summary>
public sealed class SaveHeaderFieldDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює поле шапки.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код поля.</param>
    /// <param name="command">Налаштування поля.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>), або повторний
    /// запис змінює <c>DataType</c> наявного поля (<c>ECR-TMPL-0422</c>).
    /// </exception>
    public async Task<HeaderFieldDefDto> HandleAsync(
        int templateVersionId, string code, SaveHeaderFieldDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var ecrCode = EcrCode.Create(code);
        var label = new LocalizedText(new Dictionary<string, string>(command.LabelL10n, StringComparer.OrdinalIgnoreCase));

        // ⛔ Той самий випадок, що ColumnDef (аудит 2026-09-16, §4.4): код
        // зайнятий м'яко видаленим полем читається окремо від живого, щоб
        // повідомити причину, а не голе «тип незмінний».
        var existing = version.HeaderFields.FirstOrDefault(
            f => !f.IsDeleted && string.Equals(f.Code, ecrCode.Value, StringComparison.Ordinal));

        if (existing is null && version.HeaderFields.Any(
                f => f.IsDeleted && string.Equals(f.Code, ecrCode.Value, StringComparison.Ordinal)))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Код поля шапки «{code}» зайнятий видаленим полем цієї версії. Код — це ідентичність: " +
                "значення документів посилаються саме на нього, тому повторно використати його в цій " +
                "версії не можна. Заведіть поле з іншим кодом або клонуйте версію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.headerFieldCodeTakenByDeleted",
                    ["templateVersionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["headerFieldCode"] = code,
                });
        }

        if (existing is not null && existing.DataType != command.DataType)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Тип поля шапки «{code}» незмінний після створення " +
                $"({existing.DataType} → {command.DataType}). Заведіть нове поле або клонуйте версію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.headerFieldDataTypeImmutable",
                    ["headerFieldCode"] = code,
                    ["oldDataType"] = existing.DataType.ToString(),
                    ["newDataType"] = command.DataType.ToString(),
                });
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        var change = existing is null
            ? classifier.ClassifyAddition(nameof(HeaderFieldDef))
            : classifier.Classify(nameof(HeaderFieldDef), nameof(HeaderFieldDef.IsRequired), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (existing is null)
            {
                var ordinal = command.Ordinal
                    ?? (version.HeaderFields.Count == 0 ? 0 : version.HeaderFields.Max(f => f.Ordinal) + 1);

                existing = new HeaderFieldDef(templateVersionId, ecrCode, label, ordinal, command.DataType);
                ApplyOptionalFields(existing, command);

                version.AddHeaderField(existing);

                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                existing.Relabel(label);
                ApplyOptionalFields(existing, command);

                if (command.Ordinal is { } ordinal)
                {
                    existing.Reorder(ordinal);
                }
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(HeaderFieldDef), existing.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(existing), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return GetHeaderFieldDefsHandler.Map(existing!);
    }

    private static void ApplyOptionalFields(HeaderFieldDef field, SaveHeaderFieldDefCommand command)
    {
        field.SetRequired(command.IsRequired);

        if (command.LookupRegistryDefId is { } lookupId)
        {
            field.SetLookup(lookupId);
        }
    }

    private static string Describe(HeaderFieldDef field)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            field.Code,
            LabelL10n = field.LabelL10n.Values,
            field.Ordinal,
            field.DataType,
            field.IsRequired,
            field.LookupRegistryDefId,
        });
}

/// <summary>Налаштування поля шапки, що приходять із форми.</summary>
/// <param name="LabelL10n">Підпис поля мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — нове поле стає останнім за порядком.</param>
/// <param name="DataType">Тип даних; незмінний після створення.</param>
/// <param name="IsRequired">Обов'язковість заповнення.</param>
/// <param name="LookupRegistryDefId">Довідник; лише для <see cref="CellDataType.Lookup"/>.</param>
public sealed record SaveHeaderFieldDefCommand(
    IReadOnlyDictionary<string, string> LabelL10n,
    int? Ordinal,
    CellDataType DataType,
    bool IsRequired,
    int? LookupRegistryDefId);

/// <summary>Поле шапки у відповіді на читання/запис через ці обробники.</summary>
public sealed record HeaderFieldDefDto(
    int Id,
    string Code,
    LocalizedText LabelL10n,
    int Ordinal,
    CellDataType DataType,
    bool IsRequired,
    int? LookupRegistryDefId);
