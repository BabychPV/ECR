using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// ✎ D-461: позначка «ручне значення замість формули» на комірці документа (<c>doc.CellFormulaOverride</c>).
/// </summary>
/// <remarks>
/// ⚠ Саме значення лежить у <see cref="CellValue"/> з <c>IsCalculated = 0</c>; цей рядок робить його
/// ОДНОЗНАЧНИМ (не «колись введене, а потім колонку зробили обчислюваною», а саме свідома заміна формули) і
/// тримає чинну причину для підказки в сітці без читання журналу. Історію «хто/чому/коли» тримає журнал
/// (<c>aud.CellChange.Reason</c>, походження <c>FormulaOverride</c>/<c>FormulaRestored</c>).
///
/// ⛔ Зовнішніх ключів на <c>doc.TableRow</c>/<c>cfg.ColumnDef</c> НЕМАЄ навмисно — той самий прийом, що
/// <see cref="CellStyle"/>: архівація звільняє партиції <c>doc.TableRow</c> через <c>TRUNCATE … WITH (PARTITIONS)</c>,
/// а SQL Server відмовляє в ньому, якщо на таблицю посилається будь-який ключ. Цілісність тримають явні дії:
/// видалення документа, перенос версії, і те, що читачі беруть позначки лише наявних рядків.
/// </remarks>
public sealed class CellFormulaOverride
{
    /// <summary>Найдовша причина — та сама межа, що в причини зміни ключа документа.</summary>
    public const int MaxReasonLength = 1000;

    private CellFormulaOverride() { }

    /// <summary>Нова позначка.</summary>
    public CellFormulaOverride(CellAddress address, long tableInstanceId, string reason, int userId, DateTime at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        PeriodKeyValue = address.PeriodKey.Value;
        TableRowId = address.TableRowId;
        ColumnDefId = address.ColumnDefId;
        TableInstanceId = tableInstanceId;
        Reason = reason;
        SetByUserId = userId;
        SetAt = at;
    }

    /// <summary>Ключ партиції (перший у ключі).</summary>
    public int PeriodKeyValue { get; private set; }

    public long TableRowId { get; private set; }

    public int ColumnDefId { get; private set; }

    /// <summary>Екземпляр таблиці — денормалізовано, щоб зріз читав позначки одним пошуком.</summary>
    public long TableInstanceId { get; private set; }

    /// <summary>Чинна причина (обов'язкова, обрізана).</summary>
    public string Reason { get; private set; } = null!;

    public int SetByUserId { get; private set; }

    public DateTime SetAt { get; private set; }

    public CellAddress Address => new(new PeriodKey(PeriodKeyValue), TableRowId, ColumnDefId);
}
