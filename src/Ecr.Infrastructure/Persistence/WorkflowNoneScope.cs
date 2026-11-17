// src/Ecr.Infrastructure/Persistence/WorkflowNoneScope.cs
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ B147 (B75): ключ «без погодження» — документи проєкту <see cref="ProjectId"/> з шаблоном <see cref="TemplateId"/>
/// (<c>null</c> — без власного шаблону) за період <see cref="PeriodKey"/> мають версію шаблону без подання й
/// затвердження (<c>WorkflowMode.None</c>).
/// </summary>
public sealed class WorkflowNoneKey
{
    /// <summary>Проєкт документа.</summary>
    public int ProjectId { get; set; }

    /// <summary>Власний шаблон документа (<c>doc.Document.TemplateId</c>); <c>null</c> — основний шаблон проєкту.</summary>
    public int? TemplateId { get; set; }

    /// <summary>Період; <c>Рік*100 + Номер</c>.</summary>
    public int PeriodKey { get; set; }
}

/// <summary>
/// ✎ B147 (B75): які документи й за які періоди — «без погодження», ОДНЕ правило для чотирьох місць переліку документів
/// (смуга лічильників, фільтр стану, стан рядка/аркушів, клітинки року).
/// </summary>
/// <remarks>
/// <para>
/// Версія за період — те саме правило, що всюди (<c>SheetEditGate</c>, B138, B144): документ без шаблону —
/// <c>doc.fn_VersionOf(проєкт, період)</c>, із власним (B74) — <c>doc.fn_TemplateVersionOf(проєкт, шаблон, період)</c>.
/// </para>
/// <para>
/// ⚠ Продуктивність. Скалярна функція версії на КОЖЕН рядок стану чи клітинку «аркуш × період» коштує ~17 мкс/рядок
/// (замір B144) — у смузі року це сотні тисяч викликів. Тому функція викликається тут ОДИН раз на різну пару
/// «проєкт, шаблон» × період (десятки–сотні викликів), а великі запити переліку лише приєднують готові ключі
/// (<see cref="Keys"/>, <c>OPENJSON</c>) без жодної функції. Версій без погодження немає — другого запиту немає зовсім
/// (<see cref="Empty"/>).
/// </para>
/// <para>
/// ⛔ Ключі — метадані шаблону, не межа видимості: вони лише ПРИЄДНУЮТЬСЯ до документів, які запит уже відібрав
/// межею читача, і самі нікому не віддаються.
/// </para>
/// </remarks>
public sealed class WorkflowNoneScope
{
    private WorkflowNoneScope(IReadOnlyList<WorkflowNoneKey> keys)
    {
        Json = JsonSerializer.Serialize(keys.Select(k => new { p = k.ProjectId, t = k.TemplateId, k = k.PeriodKey }));
        IsEmpty = keys.Count == 0;
    }

    /// <summary>Жодного документа без погодження.</summary>
    public static WorkflowNoneScope Empty { get; } = new([]);

    /// <summary>Ключі JSON-масивом <c>[{"p":проєкт,"t":шаблон|null,"k":період}]</c> — параметр сирих запитів.</summary>
    public string Json { get; }

    /// <summary>Ключів немає.</summary>
    public bool IsEmpty { get; }

    /// <summary>Ключі за ОДИН період (місячний перелік): версія — за цей період, є в проєкті такий період чи ні.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<WorkflowNoneScope> ForPeriodAsync(EcrDbContext db, int periodKey, CancellationToken ct)
        => LoadAsync(db, single: 1, periodKey, periodKey, ct);

    /// <summary>Ключі за періоди проєктів у році (перелік за рік): усі періоди року, і розпочаті, і ні.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<WorkflowNoneScope> ForYearAsync(EcrDbContext db, int year, CancellationToken ct)
        => LoadAsync(db, single: 0, year * 100, (year + 1) * 100, ct);

    private static async Task<WorkflowNoneScope> LoadAsync(
        EcrDbContext db, int single, int from, int to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Різні пари «проєкт, шаблон» серед документів × періоди; функція версії — раз на пару й період.
        // ⚠ Зовнішній CASE обчислюється по порядку: коли версій без погодження НЕМАЄ зовсім, перша гілка дає 0 і
        // функція версії не викликається жодного разу. Завжди рівно ОДИН запит (число звернень не залежить від даних).
        var keys = await db.Database
            .SqlQuery<WorkflowNoneKey>($"""
                SELECT k.ProjectId AS ProjectId, k.TemplateId AS TemplateId, pk.PeriodKey AS PeriodKey
                FROM (SELECT DISTINCT d.ProjectId, d.TemplateId FROM doc.Document d) k
                CROSS APPLY (
                    SELECT {from} AS PeriodKey WHERE {single} = 1
                    UNION ALL
                    SELECT p.PeriodKey FROM doc.Period p
                    WHERE {single} = 0 AND p.ProjectId = k.ProjectId AND p.PeriodKey > {from} AND p.PeriodKey < {to}
                ) pk
                WHERE CASE
                          WHEN NOT EXISTS (SELECT 1 FROM cfg.TemplateVersion nv WHERE nv.WorkflowMode = 1) THEN 0
                          WHEN CASE WHEN k.TemplateId IS NULL THEN doc.fn_VersionOf(k.ProjectId, pk.PeriodKey)
                                    ELSE doc.fn_TemplateVersionOf(k.ProjectId, k.TemplateId, pk.PeriodKey) END
                               IN (SELECT nv.Id FROM cfg.TemplateVersion nv WHERE nv.WorkflowMode = 1) THEN 1
                          ELSE 0
                      END = 1
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return keys.Count == 0 ? Empty : new WorkflowNoneScope(keys);
    }

    /// <summary>
    /// Ключі як запит — для приєднання в LINQ (корельований <c>EXISTS</c> за проєктом, шаблоном і періодом); таблиць
    /// не читає, лише розгортає JSON-параметр.
    /// </summary>
    /// <param name="db">Контекст.</param>
    public IQueryable<WorkflowNoneKey> Keys(EcrDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var json = Json;

        return db.Database.SqlQuery<WorkflowNoneKey>($"""
            SELECT n.p AS ProjectId, n.t AS TemplateId, n.k AS PeriodKey
            FROM OPENJSON({json}) WITH (p int '$.p', t int '$.t', k int '$.k') AS n
            """);
    }
}
