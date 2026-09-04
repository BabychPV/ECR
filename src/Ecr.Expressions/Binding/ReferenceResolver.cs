using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Резолвить посилання в конкретні <c>TableDefId</c>, <c>ColumnDefId</c>,
/// <c>RowKey</c>. Виконується **при публікації**: нерезолвлене посилання має
/// зупинити публікацію, а не зіпсувати число в проді.
/// </summary>
public sealed class ReferenceResolver(TemplateVersionSnapshot snapshot)
{
    /// <summary>Резолвить одне посилання.</summary>
    /// <param name="node">Вузол посилання.</param>
    /// <param name="currentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
    /// <param name="currentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
    /// <returns>Резолвлене посилання або діагностика <c>ECR-TMPL-4222</c>.</returns>
    public ResolvedReference Resolve(CellReferenceNode node, int currentTableDefId, string? currentRowKey)
        => throw new NotImplementedException(
            "TODO: доповнити скорочені форми з контексту (02b §3.1); знайти SheetDef/TableDef/ColumnDef " +
            "за кодами в snapshot; для RowMode = Fixed перевірити наявність RowKey у cfg.RowDef; " +
            "для Dynamic заборонити конкретний RowKey (ECR-TMPL-4222) — посилатися можна лише предикатом; " +
            "'{Month}' підставити поточну місячну колонку; " +
            "[Period:±N] зберегти як PeriodOffset — вихід за межі проєкту в рантаймі дає null, НЕ помилку.");
}

/// <summary>Резолвлене посилання.</summary>
public sealed record ResolvedReference(
    int TableDefId, string? RowKey, int ColumnDefId, int PeriodOffset, string? FilterJson);
