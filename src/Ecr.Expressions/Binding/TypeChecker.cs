using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє типи **при публікації**. Приведення не відбувається мовчки:
/// Excel вгадує тип і саме тому дає «майже правильні» числа; тут
/// неоднозначність — помилка, поки її ще дешево виправити (02b §5).
/// </summary>
public sealed class TypeChecker
{
    /// <summary>Виводить тип виразу і збирає діагностики.</summary>
    public ExpressionValueType Check(AstNode node, ITypeContext context, List<Parsing.ExpressionDiagnostic> diagnostics)
        => throw new NotImplementedException(
            "TODO: рекурсивно вивести тип за правилами 02b §5. Заборонити: Number + Text, " +
            "Boolean в арифметиці, порівняння різних типів, Lookup-комірку в арифметиці. " +
            "Дозволити: Text & будь-що (друге приводиться до тексту), Date − Date → Number, " +
            "Date + Number → Date. Кожне порушення — ECR-TMPL-4222 із позицією.");
}

/// <summary>Джерело типів для посилань.</summary>
public interface ITypeContext
{
    /// <summary>Тип значення колонки.</summary>
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId);

    /// <summary>Тип аргументу методології.</summary>
    public ExpressionValueType GetArgumentType(string name);
}
