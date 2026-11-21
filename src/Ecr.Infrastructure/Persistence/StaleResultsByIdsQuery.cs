using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Той самий предикат «результати документа застаріли», що й <see cref="StaleResultsQuery"/>, але для ВІДОМОГО
/// невеликого набору документів (картка документа, сторінка переліку при переповненні карти, лікування карти після
/// правки чи перемикання прогону). Рядки ті самі, що <c>StaleResultsQuery.Documents(db, p).Where(ids.Contains)</c>.
/// </summary>
/// <remarks>
/// ⛔ Навіщо окрема форма (стенд Land 21.11, <c>EcrLandMsLegacy3</c>): картка великого документа за період, карта якого
/// не прогріта, - 6-49 с без навантаження і 503 <c>ECR-SYS-0503</c> (таймаут команди 60 с у <c>-- StaleMap:direct</c>) під
/// віялом перерахунків. Плоска форма періоду, звужена зовнішнім <c>WHERE DocumentId IN (…)</c>, у фактичному плані:
/// <list type="bullet">
///   <item><c>x</c> (актуальний прогін документа) - діапазон <c>IX_CalculationResult_DocRun</c> по (період, документ) через
///   УСЮ історію прогонів документа: 602 тис. рядків для документа 3 (актуальний прогін - 35 тис.), і двічі - по разу на гілку;</item>
///   <item><c>used</c> (довідники, які читали результати) - по <c>IX_CalculationResult_Version</c> для ВСЬОГО періоду: 100
///   виконань, 2,2 млн рядків, з яких лишається 115 тис. документа і 13 рядків після групування;</item>
/// </list>
/// разом 52,8 тис. логічних читань і 4,2 с ЦП на теплому кеші (холодний - до 21 тис. фізичних читань). Обидві частини
/// ростуть з ІСТОРІЄЮ (кожен перерахунок лишає попередні результати) і з розміром ІНШИХ документів періоду.
///
/// Тут обидві частини прив'язані до (період, документ, прогін) - префікса ключа <c>IX_CalculationResult_DocRun</c>:
/// <list type="bullet">
///   <item><c>x</c>: на кожен актуальний прогін періоду - <c>EXISTS</c> одним пошуком по (період, документ, прогін), без
///   читання історії;</item>
///   <item>довідникова гілка: різні <c>MethodologyVersionId</c> лише рядків актуального прогону документа (<c>CROSS APPLY …
///   DISTINCT</c>, діапазон того самого префікса), далі ребра <c>RegistryUse</c> і <c>RegistryDef</c>. <c>TOP (2147483647)</c> -
///   бар'єр оптимізатора (нічого не обрізає): без нього на тестовій базі з'єднання з <c>RegistryUse</c> опускалося під
///   агрегат, і діапазон прогону читався на КОЖНЕ ребро (ребра × рядки прогону; на Land це було б до 100 × 35 тис.).</item>
/// </list>
/// Виміряно на тій самій базі (20 пар період × документ, 202601-202605 × 3-6, теплий кеш): 434-1283 логічних читань і
/// 14-44 мс (одиночний викид 0,7 с під стороннім навантаженням) проти 20-60 тис. читань і 1-11 с; рядки збігаються.
/// Найгірший випадок довідникової гілки (усі довідники «змінені після прогону») - та сама тисяча читань, тоді як
/// <c>EXISTS</c> на пару «документ × довідник» дав би 1 с (17 тис. читань по <c>IX_CalculationResult_Version</c>).
///
/// ⛔ Корельовані <c>EXISTS</c>/<c>APPLY</c> тут - НАВМИСНО, на відміну від <see cref="StaleResultsQuery"/>: там зовнішній
/// набір - УСІ документи періоду з заниженою оцінкою (вкладені цикли зі спулом, LP-45/rc30), тут - явний список до
/// <see cref="MaxIds"/> документів (<c>VALUES</c>), і вкладені цикли - саме той план, що потрібен. Ціна ∝ документи ×
/// актуальні прогони періоду (пошуки по 2-3 сторінки) + рядки актуального прогону документа, а не історія періоду.
/// Сторожі - <c>StaleResultsByIdsQueryShapeTests</c> (текст), <c>DocumentStaleResultsStoreTests</c>
/// <c>Картка_не_читає_історію_…</c> (читання не ростуть з історією) і <c>Форма_для_набору_дає_ті_самі_рядки_…</c>.
///
/// Семантика 1:1 з <see cref="StaleResultsQuery.Documents"/> (без фільтра автора) і
/// <see cref="StaleResultsQuery.AuthorDocuments"/>: «актуальний прогін документа» - найбільший <c>Id</c> серед <c>Current</c>-
/// прогонів періоду, у яких є числа документа за період; правки від <c>StartedAt</c>, довідник від
/// <c>COALESCE(InputsAsOfUtc, StartedAt)</c>; «відколи» - <c>MIN</c>. Документ без застарілості - без рядка.
/// </remarks>
internal static class StaleResultsByIdsQuery
{
    /// <summary>
    /// Стеля документів одного запиту: кожен id - параметр (≤ 2100 на команду), а вкладені цикли виправдані лише для
    /// невеликого набору. Викликачі з більшим набором ділять його на частини (<see cref="Chunks"/>).
    /// </summary>
    public const int MaxIds = 500;

    /// <summary>Документи набору із застарілими результатами (будь-який автор правки + довідники).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="documentIds">Документи: 1..<see cref="MaxIds"/>.</param>
    public static IQueryable<StaleDocumentRow> Documents(EcrDbContext db, int periodKey, IReadOnlyCollection<long> documentIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (format, args) = Build(periodKey, documentIds, ids => $$"""
            SELECT t.DocumentId AS DocumentId, MIN(t.At) AS Since
              FROM (
                    SELECT x.DocumentId AS DocumentId, c.ChangedAt AS At
                      FROM {{CurrentRuns(ids)}}
                      JOIN calc.CalculationRun AS r ON r.Id = x.RunId
                      JOIN aud.CellChange AS c
                        ON c.DocumentId = x.DocumentId
                       AND c.PeriodKey = {0}
                       AND c.ChangedAt > r.StartedAt
                       AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')
                    UNION ALL
                    SELECT x.DocumentId AS DocumentId, rd.DataChangedAt AS At
                      FROM {{CurrentRuns(ids)}}
                      JOIN calc.CalculationRun AS r ON r.Id = x.RunId
                      CROSS APPLY (SELECT DISTINCT TOP (2147483647) cr2.MethodologyVersionId AS MethodologyVersionId
                                     FROM calc.CalculationResult AS cr2 {{ResultSeek}}
                                    WHERE cr2.PeriodKey = {0}
                                      AND cr2.DocumentId = x.DocumentId
                                      AND cr2.CalculationRunId = x.RunId) AS mv
                      JOIN cfg.RegistryUse AS u
                        ON u.SourceId = mv.MethodologyVersionId
                       AND u.SourceKind = 1
                      JOIN cfg.RegistryDef AS rd
                        ON rd.Id = u.RegistryDefId
                       AND rd.DataChangedAt > COALESCE(r.InputsAsOfUtc, r.StartedAt)
                   ) AS t
             GROUP BY t.DocumentId
            """);

        return db.Database.SqlQuery<StaleDocumentRow>(FormattableStringFactory.Create(format, args));
    }

    /// <summary>
    /// Карти авторів для набору документів: рівно <see cref="StaleResultsQuery.AuthorDocuments"/>, звужений до набору
    /// (лише гілка правок комірок; довідникової гілки в карті автора немає).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="documentIds">Документи: 1..<see cref="MaxIds"/>.</param>
    public static IQueryable<StaleAuthorRow> AuthorDocuments(EcrDbContext db, int periodKey, IReadOnlyCollection<long> documentIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (format, args) = Build(periodKey, documentIds, ids => $$"""
            SELECT c.ChangedByUserId AS UserId, x.DocumentId AS DocumentId, MIN(c.ChangedAt) AS Since
              FROM {{CurrentRuns(ids)}}
              JOIN calc.CalculationRun AS r ON r.Id = x.RunId
              JOIN aud.CellChange AS c
                ON c.DocumentId = x.DocumentId
               AND c.PeriodKey = {0}
               AND c.ChangedAt > r.StartedAt
               AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')
             GROUP BY c.ChangedByUserId, x.DocumentId
            """);

        return db.Database.SqlQuery<StaleAuthorRow>(FormattableStringFactory.Create(format, args));
    }

    /// <summary>
    /// Пошук по префіксу (PeriodKey, DocumentId, CalculationRunId) <c>IX_CalculationResult_DocRun</c> (<c>07-partition-tables.sql</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Без підказки план залежить від оцінок: на тестовій базі зі свіжими 40 тис. рядків (оцінка 1,3 рядка на версію)
    /// оптимізатор розкорелював довідникову гілку в «RegistryUse → <c>IX_CalculationResult_Version</c> (версія, період)» -
    /// усі рядки версії за період, тобто знову чужі документи й історія (152 читання проти 6 до вставки). Форма
    /// <c>FORCESEEK, INDEX(…)</c>, а не <c>FORCESEEK (індекс (колонки))</c> - друга на партиційованому індексі дає Msg 8622
    /// (урок <c>CalculationResultStore.Year</c>). Індекс перейменовано чи прибрано - запит падає з помилкою, а не повільнішає
    /// мовчки; ловлять інтеграційні тести застарілості.
    /// </remarks>
    private const string ResultSeek = "WITH (FORCESEEK, INDEX(IX_CalculationResult_DocRun))";

    /// <summary>Набір, поділений на частини до <see cref="MaxIds"/> (без повторів).</summary>
    /// <param name="documentIds">Документи.</param>
    public static IEnumerable<long[]> Chunks(IEnumerable<long> documentIds) => documentIds.Distinct().Chunk(MaxIds);

    /// <summary>
    /// <c>x</c>: актуальний прогін кожного документа набору. ⛔ Не діапазон усіх результатів документа (історія прогонів), а
    /// по одному <c>EXISTS</c> на актуальний прогін періоду - пошук по префіксу (період, документ, прогін).
    /// </summary>
    private static string CurrentRuns(string ids) => $"""
        (SELECT d.DocumentId AS DocumentId, MAX(r0.Id) AS RunId
                                FROM (VALUES {ids}) AS d (DocumentId)
                                JOIN calc.CalculationRun AS r0
                                  ON r0.PeriodKey = {"{0}"}
                                 AND r0.Status = N'Current'
                               WHERE EXISTS (SELECT 1
                                               FROM calc.CalculationResult AS cr {ResultSeek}
                                              WHERE cr.PeriodKey = {"{0}"}
                                                AND cr.DocumentId = d.DocumentId
                                                AND cr.CalculationRunId = r0.Id)
                               GROUP BY d.DocumentId) AS x
        """;

    /// <summary>
    /// Формат і аргументи для <see cref="FormattableStringFactory"/>: <c>{0}</c> - період, <c>{1..n}</c> - документи (кожен
    /// id - параметр; той самий індекс у двох гілках - той самий параметр).
    /// </summary>
    private static (string Format, object[] Args) Build(
        int periodKey, IReadOnlyCollection<long> documentIds, Func<string, string> sql)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count is 0 or > MaxIds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentIds), documentIds.Count, $"Очікується 1..{MaxIds} документів; більший набір - частинами (Chunks).");
        }

        var args = new object[documentIds.Count + 1];
        args[0] = periodKey;
        var values = new StringBuilder();
        var i = 1;
        foreach (var id in documentIds)
        {
            args[i] = id;
            values.Append(i == 1 ? "(" : ", (").Append('{').Append(i.ToString(CultureInfo.InvariantCulture)).Append("})");
            i++;
        }

        return (sql(values.ToString()), args);
    }
}
