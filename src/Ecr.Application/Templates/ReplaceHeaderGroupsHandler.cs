using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>Група у запиті заміни дерева.</summary>
/// <param name="Code">Код групи, унікальний у таблиці; стабільний при клонуванні версії.</param>
/// <param name="ParentCode">Код батьківської групи тієї ж таблиці; <c>null</c> — корінь.</param>
/// <param name="Ordinal">Порядок серед сусідів.</param>
/// <param name="LabelL10n">Підпис мовами каталогу (uk/en/ru/kz); потрібна хоч одна мова.</param>
public sealed record HeaderGroupInputDto(
    string Code, string? ParentCode, int Ordinal, IReadOnlyDictionary<string, string> LabelL10n);

/// <summary>Результат заміни дерева груп.</summary>
/// <param name="PresentationRevision">Нова ревізія презентаційного шару (ключ кешу).</param>
/// <param name="Groups">Дерево після заміни (плоский список).</param>
public sealed record ReplaceHeaderGroupsResult(int PresentationRevision, IReadOnlyList<Dto.TemplateHeaderGroupDto> Groups);

/// <summary>
/// Замінює ВСЕ дерево груп заголовків колонок таблиці одним атомарним запитом (презентаційний шар: доступно й в
/// опублікованій версії, піднімає <c>PresentationRevision</c>). Право <c>Template.Edit</c>.
/// </summary>
public sealed class ReplaceHeaderGroupsHandler(
    ITemplateVersionStore store,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions)
{
    private const int MaxGroups = 200;

    /// <summary>Замінює дерево груп таблиці.</summary>
    public async Task<ReplaceHeaderGroupsResult> HandleAsync(
        int templateVersionId, int tableDefId, IReadOnlyList<HeaderGroupInputDto>? groups, int userId, CancellationToken ct)
    {
        await Security.PermissionCheck.RequireAsync(access, currentUser, "Template.Edit", ct).ConfigureAwait(false);

        var inputs = Normalize(groups);
        _ = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        IReadOnlyList<HeaderGroupRow> rows = [];
        var revision = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.LockVersionForUpdateAsync(templateVersionId, innerCt).ConfigureAwait(false);
            rows = await store.ReplaceHeaderGroupsAsync(templateVersionId, tableDefId, inputs, innerCt).ConfigureAwait(false);
            revision = await store.IncrementPresentationRevisionAsync(templateVersionId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, "TableDef", tableDefId,
                    Domain.Enums.ChangeClass.Presentation, "Update",
                    OldJson: null,
                    NewJson: System.Text.Json.JsonSerializer.Serialize(
                        rows.Select(r => new { r.Code, r.ParentGroupId, r.Ordinal })),
                    ChangeReason: null, ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return new ReplaceHeaderGroupsResult(
            revision,
            [.. rows.OrderBy(r => r.Ordinal).ThenBy(r => r.Id)
                .Select(r => new Dto.TemplateHeaderGroupDto(r.Id, r.Code, r.ParentGroupId, r.Ordinal, r.HeaderL10n))]);
    }

    private static List<HeaderGroupInput> Normalize(IReadOnlyList<HeaderGroupInputDto>? groups)
    {
        groups ??= [];
        if (groups.Count > MaxGroups)
        {
            throw Invalid("Забагато груп заголовків.");
        }

        var result = new List<HeaderGroupInput>(groups.Count);
        foreach (var g in groups)
        {
            if (g is null || string.IsNullOrWhiteSpace(g.Code) || g.Ordinal < 0 || g.Ordinal > 1_000_000
                || g.LabelL10n is null || g.LabelL10n.Count == 0
                || g.LabelL10n.Values.Any(v => v is null)
                || g.LabelL10n.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != g.LabelL10n.Count)
            {
                throw Invalid("Група заголовків має неприйнятну форму.");
            }

            EcrCode code;
            try
            {
                code = EcrCode.Create(g.Code);
            }
            catch (DomainException)
            {
                throw Invalid($"Код групи {g.Code} некоректний.");
            }

            result.Add(new HeaderGroupInput(code.Value, g.ParentCode, g.Ordinal, new LocalizedText(g.LabelL10n)));
        }

        return result;
    }

    private static BusinessRuleException Invalid(string message)
        => new(
            "ECR-TMPL-0422",
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0422.presentationValueInvalid",
                ["entityType"] = "TableDef",
                ["field"] = "HeaderGroups",
            });
}
