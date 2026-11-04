namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Збережена мапа перейменування колонки між версіями шаблону (RPT-STABLE Part B): колонка
/// <see cref="FromColumnDefId"/> старої версії стала <see cref="ToColumnDefId"/> нової. Пишеться переносом версії із
/// <c>TableRenames</c> запиту; читають стабільні вʼюхи SSRS (колонка видна під обома іменами) і міжперіодні формули
/// (старий код розвʼязується в новий як псевдонім).
/// </summary>
/// <remarks>
/// ⚠ Без зовнішніх ключів навмисно: колонки опублікованих версій незмінні, а видалення чернетки не повинно впиратись
/// у мапу, що її ніколи не стосувалась.
/// </remarks>
public sealed class ColumnRename
{
    private ColumnRename() { }

    /// <summary>Створює запис мапи.</summary>
    /// <param name="fromColumnDefId">Колонка вихідної версії.</param>
    /// <param name="toColumnDefId">Колонка цільової версії.</param>
    public ColumnRename(int fromColumnDefId, int toColumnDefId)
    {
        FromColumnDefId = fromColumnDefId;
        ToColumnDefId = toColumnDefId;
    }

    /// <summary>Колонка вихідної версії.</summary>
    public int FromColumnDefId { get; private set; }

    /// <summary>Колонка цільової версії.</summary>
    public int ToColumnDefId { get; private set; }
}
