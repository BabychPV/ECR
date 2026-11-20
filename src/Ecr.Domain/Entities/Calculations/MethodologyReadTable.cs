using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Код таблиці, яку скрипт версії читає через <c>table Alias = …;</c> (інші таблиці й рядки
/// того самого документа). Матеріалізується зі скрипта разом з виходами.
/// </summary>
/// <remarks>
/// ⚠ Навіщо рядок у базі, а не лише скомпільований скрипт: перерахунок і позначка
/// «результати застаріли» мусять знайти методології, що ЧИТАЮТЬ змінену таблицю, не
/// компілюючи кожен скрипт. Зайвий перерахунок безпечний, пропущений — ні.
/// </remarks>
public sealed class MethodologyReadTable : Entity<int>
{
    private MethodologyReadTable() { }

    /// <summary>Створює запис про таблицю, що читається.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="tableCode">Код таблиці (без коду шаблону).</param>
    public MethodologyReadTable(int methodologyVersionId, string tableCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableCode);

        MethodologyVersionId = methodologyVersionId;
        TableCode = tableCode;
    }

    /// <summary>Версія, чий скрипт читає таблицю.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Код таблиці; унікальний у межах версії.</summary>
    public string TableCode { get; private set; } = null!;
}
