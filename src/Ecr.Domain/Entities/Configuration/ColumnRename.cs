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

    /// <summary>
    /// ✎ B127: запис, збережений ДО прив'язки перейменувань до проєкту (міграція B127 ставить <c>true</c> усім наявним
    /// рядкам). Проєкт, що його застосував, невідомий — такий запис діє для всіх проєктів, як і раніше. Нові записи —
    /// <c>false</c>: діють лише для проєктів із <see cref="ColumnRenameProject"/>.
    /// </summary>
    public bool IsLegacyGlobal { get; private set; }
}

/// <summary>
/// ✎ B127: проєкт, перенос якого застосував перейменування <see cref="ColumnRename"/>. <c>cfg.ColumnRename</c> — пара колонок
/// версій ШАБЛОНУ (спільного для проєктів), а перенос — на проєкт: проєкт A міг відкинути колонку <c>b</c>, яку проєкт B тих
/// самих версій переніс у <c>z</c>. Родовід для документа бере лише пари свого проєкту (і застарілі глобальні).
/// </summary>
/// <remarks>⚠ Без зовнішніх ключів — з тієї самої причини, що й <see cref="ColumnRename"/>.</remarks>
public sealed class ColumnRenameProject
{
    private ColumnRenameProject() { }

    /// <summary>Створює прив'язку.</summary>
    /// <param name="fromColumnDefId">Колонка вихідної версії.</param>
    /// <param name="toColumnDefId">Колонка цільової версії.</param>
    /// <param name="projectId">Проєкт, перенос якого застосував перейменування.</param>
    public ColumnRenameProject(int fromColumnDefId, int toColumnDefId, int projectId)
    {
        FromColumnDefId = fromColumnDefId;
        ToColumnDefId = toColumnDefId;
        ProjectId = projectId;
    }

    /// <summary>Колонка вихідної версії.</summary>
    public int FromColumnDefId { get; private set; }

    /// <summary>Колонка цільової версії.</summary>
    public int ToColumnDefId { get; private set; }

    /// <summary>Проєкт.</summary>
    public int ProjectId { get; private set; }
}
