using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Той самий предикат «результати документа застаріли», що й <see cref="StaleResultsQuery"/>, у двох режимах одного тексту:
/// для ВІДОМОГО набору документів (<see cref="Documents"/>, <see cref="AuthorDocuments"/>: картка документа, сторінка переліку
/// при переповненні карти, лікування карти після правки чи перемикання прогону) і для ВСЬОГО періоду
/// (<see cref="PeriodDocuments"/>, <see cref="PeriodAuthorDocuments"/>: повна карта <c>StaleResultsMapCache</c>,
/// <c>-- StaleMap:full</c> / <c>-- StaleMap:authors</c>). Рядки ті самі, що <c>StaleResultsQuery.Documents(db, p)</c>
/// (звужена по id - для набору).
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
/// ⛔ Повна карта періоду (Query Store того самого стенда: <c>StaleMap:full</c> 1,1 тис. виконань, у середньому 1,8 с, до
/// 58 с; <c>StaleMap:authors</c> до 60 с) - та сама хвороба в масштабі періоду: <c>x</c> агрегував УСЮ історію результатів
/// періоду (202601: 1,27 млн рядків при 78 тис. актуальних) двічі, і <c>used</c> - ще раз: 3 скани по 8,7-9,6 тис. сторінок
/// <c>IX_CalculationResult_Version</c>, 14-29 тис. логічних читань і 0,3-0,6 с на теплому кеші (1-2 с ЦП при DOP 4).
///
/// Тут обидві частини прив'язані до (період, документ, прогін) - префікса ключа <c>IX_CalculationResult_DocRun</c>:
/// <list type="bullet">
///   <item><c>x</c>: на кожну пару (документ, прогін-кандидат) - <c>EXISTS</c> одним пошуком по (період, документ, прогін),
///   без читання історії (<see cref="CurrentRuns"/>);</item>
///   <item>довідникова гілка: різні <c>MethodologyVersionId</c> лише рядків актуального прогону документа (<c>CROSS APPLY …
///   DISTINCT</c>, діапазон того самого префікса), далі ребра <c>RegistryUse</c> і <c>RegistryDef</c>. <c>TOP (2147483647)</c> -
///   бар'єр оптимізатора (нічого не обрізає): без нього на тестовій базі з'єднання з <c>RegistryUse</c> опускалося під
///   агрегат, і діапазон прогону читався на КОЖНЕ ребро (ребра × рядки прогону; на Land це було б до 100 × 35 тис.).</item>
/// </list>
/// Виміряно на тій самій базі, теплий кеш: картка (20 пар період × документ, 202601-202605 × 3-6) - 434-1283 логічних читань
/// і 14-44 мс проти 20-60 тис. читань і 1-11 с; повна карта (202601-202605) - 2,2-2,4 тис. читань і 43-49 мс проти 14-29 тис.
/// і 0,3-0,6 с; карта авторів - 94-105 читань і 2-3 мс проти 11 тис. і 0,4 с. Рядки збігаються; для карти збігаються й
/// складники (<c>x</c> і пари «документ - довідник», незалежно від дат). Найгірший випадок довідникової гілки (усі довідники
/// «змінені після прогону») - ті самі читання, тоді як <c>EXISTS</c> на пару «документ × довідник» дав би 1 с (17 тис.
/// читань по <c>IX_CalculationResult_Version</c>).
///
/// ⛔ Корельовані <c>EXISTS</c>/<c>APPLY</c> тут - НАВМИСНО, на відміну від <see cref="StaleResultsQuery"/>: там зовнішній
/// набір - агрегат по результатах періоду з заниженою оцінкою (вкладені цикли зі спулом, LP-45/rc30), тут - пари з
/// маленьких таблиць (документи й актуальні прогони; для набору - явний список до <see cref="MaxIds"/> у <c>VALUES</c>), і
/// вкладені цикли - саме той план, що потрібен. Ціна ∝ документи × прогони-кандидати (≤ 2 на документ, пошуки по 2-3
/// сторінки) + рядки актуальних прогонів, а не історія періоду. Сторожі - <c>StaleResultsByIdsQueryShapeTests</c> (текст),
/// <c>DocumentStaleResultsStoreTests</c> <c>Картка_не_читає_історію_…</c>, <c>Повна_карта_не_читає_історію_…</c> (читання не
/// ростуть з історією), <c>Форма_для_набору_дає_ті_самі_рядки_…</c> і <c>Повна_карта_дає_ті_самі_рядки_…</c>.
///
/// Семантика 1:1 з <see cref="StaleResultsQuery.Documents"/> (без фільтра автора) і
/// <see cref="StaleResultsQuery.AuthorDocuments"/>: «актуальний прогін документа» - найбільший <c>Id</c> серед <c>Current</c>-
/// прогонів періоду, у яких є числа документа за період (кандидати звужено до власного й проєктного - див.
/// <see cref="CurrentRuns"/>); правки від <c>StartedAt</c>, довідник від <c>COALESCE(InputsAsOfUtc, StartedAt)</c>;
/// «відколи» - <c>MIN</c>. Документ без застарілості - без рядка.
/// </remarks>
internal static class StaleResultsByIdsQuery
{
    /// <summary>
    /// Стеля документів одного запиту: кожен id - параметр (≤ 2100 на команду), а вкладені цикли виправдані лише для
    /// невеликого набору. Викликачі з більшим набором ділять його на частини (<see cref="Chunks"/>).
    /// </summary>
    public const int MaxIds = 500;

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

    /// <summary>Документи всього періоду: <c>dd</c> - кожен документ (пари з прогонами-кандидатами відсікають решту).</summary>
    private const string PeriodSource = "doc.Document AS dd";

    /// <summary>Документи набору із застарілими результатами (будь-який автор правки + довідники).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="documentIds">Документи: 1..<see cref="MaxIds"/>.</param>
    public static IQueryable<StaleDocumentRow> Documents(EcrDbContext db, int periodKey, IReadOnlyCollection<long> documentIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (source, args) = ListSource(periodKey, documentIds);
        return db.Database.SqlQuery<StaleDocumentRow>(FormattableStringFactory.Create(DocumentsSql(source), args));
    }

    /// <summary>
    /// Документи ВСЬОГО періоду із застарілими результатами - повна карта (<c>StaleResultsMapCache</c>): той самий текст,
    /// що <see cref="Documents"/>, з документами періоду замість списку id.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    public static IQueryable<StaleDocumentRow> PeriodDocuments(EcrDbContext db, int periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQuery<StaleDocumentRow>(FormattableStringFactory.Create(DocumentsSql(PeriodSource), periodKey));
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
        var (source, args) = ListSource(periodKey, documentIds);
        return db.Database.SqlQuery<StaleAuthorRow>(FormattableStringFactory.Create(AuthorsSql(source), args));
    }

    /// <summary>
    /// Карти всіх авторів ВСЬОГО періоду: рівно <see cref="StaleResultsQuery.AuthorDocuments"/> (той самий текст, що
    /// <see cref="AuthorDocuments"/>, з документами періоду замість списку id).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період.</param>
    public static IQueryable<StaleAuthorRow> PeriodAuthorDocuments(EcrDbContext db, int periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQuery<StaleAuthorRow>(FormattableStringFactory.Create(AuthorsSql(PeriodSource), periodKey));
    }

    /// <summary>Набір, поділений на частини до <see cref="MaxIds"/> (без повторів).</summary>
    /// <param name="documentIds">Документи.</param>
    public static IEnumerable<long[]> Chunks(IEnumerable<long> documentIds) => documentIds.Distinct().Chunk(MaxIds);

    /// <summary>Обидві гілки (правки комірок + довідники) над документами <paramref name="documents"/> (<c>dd</c>).</summary>
    private static string DocumentsSql(string documents) => $$"""
        SELECT t.DocumentId AS DocumentId, MIN(t.At) AS Since
          FROM (
                SELECT x.DocumentId AS DocumentId, c.ChangedAt AS At
                  FROM {{CurrentRuns(documents)}}
                  JOIN calc.CalculationRun AS r ON r.Id = x.RunId
                  JOIN aud.CellChange AS c
                    ON c.DocumentId = x.DocumentId
                   AND c.PeriodKey = {0}
                   AND c.ChangedAt > r.StartedAt
                   AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')
                UNION ALL
                SELECT x.DocumentId AS DocumentId, rd.DataChangedAt AS At
                  FROM {{CurrentRuns(documents)}}
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
        """;

    /// <summary>Гілка правок комірок з ключем автора над документами <paramref name="documents"/> (<c>dd</c>).</summary>
    private static string AuthorsSql(string documents) => $$"""
        SELECT c.ChangedByUserId AS UserId, x.DocumentId AS DocumentId, MIN(c.ChangedAt) AS Since
          FROM {{CurrentRuns(documents)}}
          JOIN calc.CalculationRun AS r ON r.Id = x.RunId
          JOIN aud.CellChange AS c
            ON c.DocumentId = x.DocumentId
           AND c.PeriodKey = {0}
           AND c.ChangedAt > r.StartedAt
           AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')
         GROUP BY c.ChangedByUserId, x.DocumentId
        """;

    /// <summary>
    /// <c>x</c>: актуальний прогін кожного документа <paramref name="documents"/> (джерело з рядками <c>doc.Document AS dd</c>).
    /// ⛔ Не діапазон усіх результатів документа (історія прогонів), а по одному <c>EXISTS</c> на пару (документ,
    /// прогін-кандидат) - пошук по префіксу (період, документ, прогін).
    /// </summary>
    /// <remarks>
    /// ⛔ Кандидати - не ВСІ актуальні прогони періоду, а прогони, що можуть мати числа документа: власний документний і
    /// прогін усього проєкту документа (той самий набір, що <c>CurrentRunSelection.RunsOfDocumentAsync</c>, D-557). Без
    /// звуження пар «документ × прогін» було б документи × актуальні прогони періоду - квадрат для повної карти (тестова
    /// база після прогону: 745 документів × 163 прогони). Рядки ті самі: прогін пише результати лише документів свого
    /// проєкту, документний - лише свого документа (на цьому стоять читання результатів D-557 і точкове лікування карти
    /// AN-155b); стенд Land 21.11, 6,5 млн результатів: 0 рядків поза цим правилом, 0 рядків без документа.
    /// </remarks>
    private static string CurrentRuns(string documents) => $"""
        (SELECT dd.Id AS DocumentId, MAX(r0.Id) AS RunId
                            FROM {documents}
                            JOIN calc.CalculationRun AS r0
                              ON r0.ProjectId = dd.ProjectId
                             AND r0.PeriodKey = {"{0}"}
                             AND r0.Status = N'Current'
                             AND (r0.DocumentId IS NULL OR r0.DocumentId = dd.Id)
                           WHERE EXISTS (SELECT 1
                                           FROM calc.CalculationResult AS cr {ResultSeek}
                                          WHERE cr.PeriodKey = {"{0}"}
                                            AND cr.DocumentId = dd.Id
                                            AND cr.CalculationRunId = r0.Id)
                           GROUP BY dd.Id) AS x
        """;

    /// <summary>
    /// Джерело документів набору й аргументи для <see cref="FormattableStringFactory"/>: <c>{0}</c> - період, <c>{1..n}</c> -
    /// документи (кожен id - параметр; той самий індекс у двох гілках - той самий параметр).
    /// </summary>
    private static (string Source, object[] Args) ListSource(int periodKey, IReadOnlyCollection<long> documentIds)
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

        return ($"(VALUES {values}) AS d (DocumentId) JOIN doc.Document AS dd ON dd.Id = d.DocumentId", args);
    }
}
