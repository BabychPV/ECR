using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>Публікаційна перевірка ВИРАЗІВ правил валідації (V-19).</summary>
/// <remarks>
/// ⛔ До V-19 публікація дивилася на правила лише як на структуру
/// (<see cref="PublishChecks.CheckRules"/>: суперечливі рівні, покриття
/// обов'язкових колонок), а їхні вирази не розбирала ніде. Правило
/// <c>[CDEC] &gt;=</c>, збережене до того, як збереження почало відмовляти,
/// публікувалося й доїжджало до документа, де <c>ValidationEngine</c> мовчки
/// його пропускав. Той самий <see cref="PublishChecks.CheckExpression"/>, що
/// й для формул, — з тими самими ключами зауважень.
/// </remarks>
public static class RuleExpressionChecks
{
    /// <summary>Зауваження до виразів активних правил живих таблиць.</summary>
    /// <param name="version">Версія, що публікується.</param>
    /// <param name="formulaEngine">Рушій — розбір і резолвінг.</param>
    public static IReadOnlyList<ExpressionDiagnostic> Check(TemplateVersion version, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var diagnostics = new List<ExpressionDiagnostic>();
        var scope = new ExpressionScope(formulaEngine, PublishChecks.Snapshot(version), null, null);

        foreach (var table in PublishChecks.LiveTables(version.Sheets))
        {
            foreach (var rule in table.ValidationRules.Where(r => r.IsActive))
            {
                PublishChecks.CheckExpression(
                    rule.Expression,
                    ExpressionDialect.Template,
                    new ExpressionSite(table.Id, null, rule.ColumnDefId),
                    scope,
                    diagnostics);
            }
        }

        return diagnostics;
    }
}
