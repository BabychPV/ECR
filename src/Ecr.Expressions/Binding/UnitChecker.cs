using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє сумісність одиниць. **Головна цінність механізму одиниць** —
/// саме ця перевірка: помилка ловиться до продуктиву, а не на звірці через
/// місяць (ФВ-16.7).
/// </summary>
public sealed class UnitChecker
{
    /// <summary>Виводить одиницю результату і перевіряє сумісність операндів.</summary>
    /// <returns>Ідентифікатор одиниці результату або <c>null</c>, якщо вираз безрозмірний.</returns>
    public int? Check(AstNode node, IUnitContext context, List<Parsing.ExpressionDiagnostic> diagnostics)
        => throw new NotImplementedException(
            "TODO: правила виведення:\n" +
            "— Add/Subtract: одиниці мають ЗБІГАТИСЯ; інакше ECR-TMPL-4223 (неявної конверсії немає, D-74);\n" +
            "— Multiply/Divide: одиниця результату — похідна; шукати відповідну в uom.Unit за\n" +
            "  NumeratorUnitId/DenominatorUnitId; не знайдено → ECR-TMPL-4223;\n" +
            "— CONVERT(x, from, to): результат — 'to'; перевірити, що from і to однієї розмірності;\n" +
            "— SUM/AVERAGE/MIN/MAX: усі елементи однієї одиниці;\n" +
            "— порівняння: одиниці мають збігатися;\n" +
            "— агрегація колонки з DataType = Unit без CONVERT → ECR-TMPL-4223 (ФВ-16.8).\n" +
            "Наприкінці звірити з оголошеною одиницею колонки або виходу методології.");
}

/// <summary>Джерело одиниць.</summary>
public interface IUnitContext
{
    /// <summary>Одиниця колонки; <c>null</c> — безрозмірна.</summary>
    public int? GetColumnUnit(int tableDefId, int columnDefId);

    /// <summary>Одиниця константи методології.</summary>
    public int? GetConstantUnit(string code);

    /// <summary>Розмірність одиниці.</summary>
    public byte GetDimension(int unitId);

    /// <summary>Шукає похідну одиницю за чисельником і знаменником.</summary>
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId);
}
