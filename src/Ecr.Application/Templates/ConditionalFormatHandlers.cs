using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Templates;

/// <summary>
/// Замінює набір правил умовного форматування версії-чернетки (ФВ-2.6/2.7).
/// PUT-семантика: клієнтський редактор тримає весь список, тож одна операція
/// покриває створення, зміну, перевпорядкування й видалення. Порядок
/// правил у колонці — порядок у запиті; перше спрацьоване правило виграє.
/// </summary>
public sealed class SaveConditionalFormatsHandler(
    IConditionalFormatStore rules,
    ITemplateVersionStore store,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії — те саме, що на стиль і колонку.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Стеля набору на версію: захист від випадкового чи зловмисного роздування.</summary>
    public const int MaxRules = 500;

    /// <summary>Записує набір правил.</summary>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — версія заморожена; <c>ECR-CFG-0422</c> — правило невалідне.
    /// </exception>
    public async Task<IReadOnlyList<ConditionalFormatRuleDto>> HandleAsync(
        int templateVersionId, IReadOnlyList<ConditionalFormatRuleDto> requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();

        if (requested.Count > MaxRules)
        {
            throw new DomainException(
                "ECR-CFG-0422",
                $"Занадто багато правил умовного форматування: {requested.Count} > {MaxRules}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CFG-0422.condFormatLimit",
                    ["max"] = MaxRules,
                });
        }

        var columns = version.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns)
            .Where(c => !c.IsDeleted).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var built = new List<ConditionalFormatRule>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var r = requested[i];
            if (!columns.Contains(r.ColumnCode ?? string.Empty))
            {
                throw new DomainException(
                    "ECR-CFG-0422",
                    $"Правило {i + 1}: колонки {r.ColumnCode} у версії немає.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-CFG-0422.condFormatColumn",
                        ["index"] = i + 1,
                        ["columnCode"] = r.ColumnCode,
                    });
            }

            var ordinal = ordinals.GetValueOrDefault(r.ColumnCode!) + 1;
            ordinals[r.ColumnCode!] = ordinal;
            built.Add(new ConditionalFormatRule(
                templateVersionId, r.ColumnCode!, ordinal, r.Operator, Blank(r.Value), Blank(r.ValueTo),
                Blank(r.BackgroundHex), Blank(r.ForegroundHex), r.IsBold));
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);
            await rules.ReplaceAsync(templateVersionId, built, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return [.. built.Select(ConditionalFormatMapper.Map)];
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Правила умовного форматування версії — для редактора й (далі) для сітки/експорту.</summary>
public sealed class GetConditionalFormatsHandler(
    IConditionalFormatStore rules,
    ITemplateVersionStore versions,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право читання структури — те саме, що на стилі.</summary>
    public const string Permission = "Template.View";

    /// <summary>Усі правила версії.</summary>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — версії немає.</exception>
    public async Task<IReadOnlyList<ConditionalFormatRuleDto>> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var all = await rules.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        if (all.Count == 0
            && await versions.FindTemplateOfVersionAsync(templateVersionId, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                Domain.Errors.ErrorCodes.TemplateNotFound,
                $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return [.. all.Select(ConditionalFormatMapper.Map)];
    }
}

internal static class ConditionalFormatMapper
{
    internal static ConditionalFormatRuleDto Map(ConditionalFormatRule r)
        => new(r.ColumnCode, r.Operator, r.Value, r.ValueTo, r.BackgroundHex, r.ForegroundHex, r.IsBold);
}

/// <summary>Правило умовного форматування (дзеркало клієнтського <c>ConditionalRule</c>).</summary>
/// <param name="ColumnCode">Код колонки версії.</param>
/// <param name="Operator">gt, ge, lt, le, eq, ne, between, empty, notEmpty.</param>
/// <param name="Value">Операнд (число); не потрібен для empty/notEmpty.</param>
/// <param name="ValueTo">Верхня межа — лише для between.</param>
/// <param name="BackgroundHex"><c>#rrggbb</c> або <c>null</c> — колір теми.</param>
/// <param name="ForegroundHex"><c>#rrggbb</c> або <c>null</c>.</param>
/// <param name="IsBold">Жирний.</param>
public sealed record ConditionalFormatRuleDto(
    string ColumnCode,
    string Operator,
    string? Value,
    string? ValueTo,
    string? BackgroundHex,
    string? ForegroundHex,
    bool IsBold);