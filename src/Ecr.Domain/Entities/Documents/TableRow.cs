using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Рядок таблиці документа.</summary>
/// <remarks>
/// <see cref="ModifiedAt"/> оновлюється **завжди** при зміні комірок цього
/// рядка — саме це піднімає <c>RowVersion</c>. Забути про це = зламати
/// оптимістичне блокування тихо (B04 §2.4).
/// </remarks>
public sealed class TableRow
{
    private TableRow() { }

    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <param name="id">Ідентифікатор із <c>SEQUENCE</c>.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKey">Бізнес-ідентичність рядка.</param>
    /// <param name="ordinal">Порядок на екрані.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <param name="rowDefId">
    /// Опис рядка з шаблону; <c>null</c> — рядок, який додав користувач.
    /// </param>
    /// <remarks>
    /// ⛔ <see cref="RowDefId"/> існував як поле і не заповнювався ніколи:
    /// рядків із шаблону не будував ніхто, тому «рядок із <c>RowDef</c>» був
    /// станом, якого в базі не бувало (директива №09 `W8` п.2, `S-13`).
    /// </remarks>
    public TableRow(
        PeriodKey periodKey, long id, long tableInstanceId, RowKey rowKey, int ordinal, DateTime utcNow,
        int? rowDefId = null)
    {
        PeriodKeyValue = periodKey.Value;
        Id = id;
        TableInstanceId = tableInstanceId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        ModifiedAt = utcNow;
        RowDefId = rowDefId;
    }

    public int PeriodKeyValue { get; private set; }
    public long Id { get; private set; }
    public long TableInstanceId { get; private set; }
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Заповнене для <c>RowMode = Fixed</c>.</summary>
    public int? RowDefId { get; private set; }

    public int Ordinal { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// Рядок посилається на запис реєстру, який перестав бути чинним у цьому
    /// періоді (ФВ-8.13, <c>D-98</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Ознака <b>зберігається</b>, а не обчислюється при читанні: перерахунок
    /// на кожен зріз убив би бюджет 400 мс. Її ставить нічна перевірка
    /// інваріантів (ФВ-7.7) і перерахунок при зміні вікна дії запису реєстру.
    /// Читання осиротілий рядок <b>не</b> блокує, <c>Submit</c> — блокує
    /// (<c>ECR-SUB-4221</c>).
    /// </remarks>
    public bool IsOrphaned { get; private set; }

    /// <summary>Коли рядок став осиротілим; <c>null</c>, якщо не є.</summary>
    public DateTime? OrphanedAt { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public PeriodKey PeriodKey => new(PeriodKeyValue);
    public RowKey RowKey => ValueObjects.RowKey.Create(RowKeyValue);

    /// <summary>«Дотик» рядка при зміні його комірок.</summary>
    public void Touch(DateTime utcNow) => ModifiedAt = utcNow;

    /// <summary>
    /// Позначає рядок осиротілим або знімає позначку.
    /// </summary>
    /// <remarks>
    /// Викликається <c>OrphanScanJob</c> (Етап 4), а не шляхом читання:
    /// саме тому ознака є полем, а не обчисленням.
    /// </remarks>
    public void SetOrphaned(bool orphaned, DateTime utcNow)
    {
        IsOrphaned = orphaned;
        OrphanedAt = orphaned ? utcNow : null;
    }

    /// <summary>Логічне видалення рядка динамічної таблиці.</summary>
    public void SoftDelete(DateTime utcNow)
    {
        IsDeleted = true;
        ModifiedAt = utcNow;
    }
}
