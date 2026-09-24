// src/Ecr.Application/Templates/ListValidationRulesHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Templates;

/// <summary>
/// Правила валідації однієї таблиці версії (X-15, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Доти перелік правил не віддавав жоден маршрут: правила жили лише в
/// кешованому знімку для рушія валідації, і діалог «Validation rules» видаляв
/// правило ВВЕДЕНИМ З ПАМ'ЯТІ кодом — людина мала пам'ятати код, якого екран
/// ніде не показував. Тепер діалог показує перелік і видаляє вибране з нього.
///
/// ⚠ Право ПЕРЕГЛЯДУ: перелік — читання, законне для будь-кого, хто бачить
/// структуру версії.
/// </remarks>
public sealed class ListValidationRulesHandler(
    ITemplateVersionStore store,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Читає правила таблиці в порядку коду.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="tableDefId">Таблиця.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException">Версії або таблиці немає.</exception>
    public async Task<IReadOnlyList<ValidationRuleDto>> HandleAsync(
        int templateVersionId, int tableDefId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        var table = SaveColumnDefHandler.FindTable(version, tableDefId);

        return [.. table.ValidationRules
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .Select(SaveValidationRuleHandler.Map)];
    }
}
