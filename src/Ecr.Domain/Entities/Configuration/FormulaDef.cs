using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Формула шаблону: вираз плюс область дії.</summary>
public sealed class FormulaDef : Entity<int>
{
    /// <summary>Довжина колонки cfg.FormulaDef.Expression — єдине джерело межі (A4-03).</summary>
    public const int MaxExpressionLength = 2000;

    private FormulaDef() { }

    public FormulaDef(int tableDefId, FormulaScope scope, string expression, ExpressionDialect dialect)
    {
        TableDefId = tableDefId;
        Scope = scope;
        Expression = expression;
        Dialect = dialect;
    }

    public int TableDefId { get; private set; }
    public FormulaScope Scope { get; private set; }
    public int? ColumnDefId { get; private set; }
    public int? RowDefId { get; private set; }
    public ExpressionDialect Dialect { get; private set; }
    public string Expression { get; private set; } = null!;

    /// <summary>
    /// Топологічний порядок. Обчислюється **при публікації**, а не в рантаймі:
    /// сортувати граф на кожен запит — це витрата, якої бюджет не передбачає.
    /// </summary>
    public int EvaluationOrder { get; private set; }

    public bool IsCrossSheet { get; private set; }

    /// <summary>Знімок: значення матеріалізується один раз і не перераховується каскадом.</summary>
    public bool IsSnapshot { get; private set; }

    public bool IsDeleted { get; private set; }

    /// <summary>
    /// ✎ D-461: «Дозволити ручне значення» — у документі комірку цієї формули можна замінити ручним значенням
    /// (з причиною й окремим правом <c>Document.OverrideFormula</c>), а видаленням — повернути формулу.
    /// </summary>
    /// <remarks>
    /// ⚠ Налаштування, а не структура: змінюється й в ОПУБЛІКОВАНІЙ версії без нової версії
    /// (<c>TR_FormulaDef_Immutable</c> пропускає UPDATE, що міняє лише цей стовпець). Вимкнення не стирає наявних
    /// ручних значень — нові лише заборонені.
    /// </remarks>
    public bool AllowManualOverride { get; private set; }

    /// <summary>✎ D-461: вмикає чи вимикає ручне значення замість формули.</summary>
    /// <returns><c>true</c>, якщо значення справді змінилося.</returns>
    public bool SetAllowManualOverride(bool allow)
    {
        if (AllowManualOverride == allow)
        {
            return false;
        }

        AllowManualOverride = allow;
        return true;
    }

    /// <summary>Фіксує обчислений порядок. Викликається лише під час <c>Publish</c>.</summary>
    public void SetEvaluationOrder(int order) => EvaluationOrder = order;

    /// <summary>
    /// Прив'язує формулу до колонки — точка входу для <see cref="FormulaScope.Column"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ До зрізу <c>W5.3</c> (авторство формул через API) у <c>ColumnDefId</c>
    /// не було публічного сеттера взагалі: тести виставляли поле рефлексією
    /// напряму (<c>CascadeRecalculationTests</c>), бо жоден обробник формулу не
    /// писав — писати не було чим. Тепер, коли <c>PUT
    /// …/formulas/column/{columnDefId}</c> існує, рефлексія в тестах лишається
    /// діагностичним прийомом для чужих, вже готових знімків
    /// (<c>TemplateBuilder</c>), а не заміною відсутньої операції: сам домен
    /// більше не змушує виклик ламати власну інкапсуляцію.
    /// </remarks>
    /// <exception cref="DomainException">Область формули не <c>Column</c>.</exception>
    public void AssignColumn(int columnDefId)
    {
        if (Scope != FormulaScope.Column)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Формулу з областю {Scope} не можна прив'язати до колонки: очікується Column.",
                new Dictionary<string, object?>
                {
                    // ⚠ Один ключ на обидва напрямки (AssignColumn/AssignRow нижче) —
                    // той самий факт «спробували прив'язати формулу не до тієї цілі».
                    ["messageKey"] = "err.ECR-TMPL-0422.formulaAssignScopeMismatch",
                    ["scope"] = Scope.ToString(),
                    ["expectedScope"] = nameof(FormulaScope.Column),
                });
        }

        ColumnDefId = columnDefId;
    }

    /// <summary>
    /// Прив'язує формулу до рядка — точка входу для <see cref="FormulaScope.Row"/>.
    /// </summary>
    /// <exception cref="DomainException">Область формули не <c>Row</c>.</exception>
    public void AssignRow(int rowDefId)
    {
        if (Scope != FormulaScope.Row)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Формулу з областю {Scope} не можна прив'язати до рядка: очікується Row.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.formulaAssignScopeMismatch",
                    ["scope"] = Scope.ToString(),
                    ["expectedScope"] = nameof(FormulaScope.Row),
                });
        }

        RowDefId = rowDefId;
    }

    /// <summary>
    /// Прив'язує формулу до однієї комірки (рядок × колонка) — точка входу для
    /// <see cref="FormulaScope.Cell"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ D1 (міжтаблична консолідація, напр. 7.0 ← 7): рантайм і
    /// <c>CK_Formula_Scope</c> Cell-область вже мали (<c>Scope = 2</c> вимагає
    /// обох ключів); бракувало лише цієї операції в домені, тож комірочну
    /// формулу не можна було завести інакше, ніж рефлексією чи конструктором
    /// у тесті.
    /// </remarks>
    /// <exception cref="DomainException">Область формули не <c>Cell</c>.</exception>
    public void AssignCell(int rowDefId, int columnDefId)
    {
        if (Scope != FormulaScope.Cell)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Формулу з областю {Scope} не можна прив'язати до комірки: очікується Cell.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.formulaAssignScopeMismatch",
                    ["scope"] = Scope.ToString(),
                    ["expectedScope"] = nameof(FormulaScope.Cell),
                });
        }

        RowDefId = rowDefId;
        ColumnDefId = columnDefId;
    }

    /// <summary>
    /// Змінює текст виразу.
    /// </summary>
    /// <remarks>
    /// ⚠ Ідентичність формули — не її текст, а колонка чи рядок, який вона
    /// обчислює (адреса <c>PUT …/formulas/{scope}/{targetId}</c>): повторний
    /// запис за тією самою адресою МІНЯЄ вираз, а не заводить другу формулу на
    /// ту саму колонку (<c>D2-147</c>).
    /// </remarks>
    public void SetExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        Expression = expression;
    }

    /// <summary>Змінює діалект, за яким читається вираз.</summary>
    public void SetDialect(ExpressionDialect dialect) => Dialect = dialect;

    /// <summary>
    /// Логічне видалення.
    /// </summary>
    /// <remarks>
    /// ⛔ Без параметрів — на відміну від <see cref="SheetDef.SoftDelete"/>:
    /// схема <c>cfg.FormulaDef</c> не несе <c>DeletedAt</c>/<c>DeletedByUserId</c>
    /// (02a-db-schema.md), тільки <c>IsDeleted</c>. Рушій перерахунку й
    /// експорт уже фільтрують <c>!IsDeleted</c> скрізь, де читають формули
    /// (<c>RecalculationPlanBuilder</c>, <c>ExcelExporter</c>) — м'яко видалена
    /// формула лишається в таблиці, бо на неї можуть посилатися інші вирази чи
    /// граф залежностей навіть у чернетці (ФВ-7.6), просто перестає рахуватися.
    /// </remarks>
    public void SoftDelete() => IsDeleted = true;
}
