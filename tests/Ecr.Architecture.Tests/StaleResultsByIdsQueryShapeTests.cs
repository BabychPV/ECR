using System.Text.RegularExpressions;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Застарілість для ВІДОМОГО набору документів (картка, сторінка при переповненні карти, лікування карти) іде формою
/// <c>StaleResultsByIdsQuery</c>, прив'язаною до префікса (період, документ, прогін), а НЕ плоскою формою періоду,
/// звуженою зовнішнім фільтром по <c>DocumentId</c>.
/// </summary>
/// <remarks>
/// ⛔ Стенд Land 21.11: картка великого документа — 6-49 с без навантаження і 503 під віялом перерахунків. План плоскої
/// форми, звуженої <c>WHERE DocumentId IN (…)</c>: актуальний прогін через УСЮ історію результатів документа (602 тис.
/// рядків при 35 тис. актуальних) і довідникова частина ВСЬОГО періоду (2,2 млн рядків) — 52,8 тис. читань, 4,2 с ЦП.
/// Форма для набору — 13-44 мс і 0,4-1,2 тис. читань на тих самих 20 парах. Сторож по тексту (план на малій тестовій базі
/// не показовий); вартість на базі стереже
/// <c>DocumentStaleResultsStoreTests.Картка_не_читає_історію_результатів_документа_і_довідники_всього_періоду</c>.
/// Мутації: у <c>DocumentStore.StaleResultsDirectAsync</c> повернути
/// <c>StaleResultsQuery.Documents(db, p).Where(r =&gt; ids.Contains(r.DocumentId))</c> — червоний перший тест; у
/// <c>StaleResultsByIdsQuery</c> прибрати <c>cr.CalculationRunId = r0.Id</c> з <c>EXISTS</c>, підказку пошуку чи бар'єр
/// <c>TOP</c> — червоний другий.
/// </remarks>
public sealed class StaleResultsByIdsQueryShapeTests
{
    private const string ByIdsPath = "src/Ecr.Infrastructure/Persistence/StaleResultsByIdsQuery.cs";

    /// <summary><c>StaleResultsQuery.Documents(…)</c> чи <c>AuthorDocuments(…)</c>, одразу звужений по <c>DocumentId</c>.</summary>
    private static readonly Regex NarrowedPeriodShape = new(
        @"StaleResultsQuery\s*\.\s*(?:Documents|AuthorDocuments)\s*\([^;()]*\)\s*\.\s*Where\s*\(\s*\w+\s*=>\s*(?:[\w.]+\s*\.\s*Contains\s*\(\s*\w+\s*\.\s*DocumentId\s*\)|\w+\s*\.\s*DocumentId\s*==)",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Набір_документів_не_звужує_плоску_форму_періоду_фільтром_по_id()
    {
        var offenders = SourceTree.Production("Ecr.Infrastructure")
            .Where(f => NarrowedPeriodShape.IsMatch(Code(f)))
            .Select(f => f.Path)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Плоска форма періоду, звужена по DocumentId, читає історію результатів документа й довідники всього періоду; "
            + "для набору документів - StaleResultsByIdsQuery:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // Сторож живий: зразок колишнього виклику він ловить.
        Assert.Matches(NarrowedPeriodShape, "StaleResultsQuery.Documents(db, periodKey)\n    .Where(r => ids.Contains(r.DocumentId))");
        Assert.Matches(NarrowedPeriodShape, "StaleResultsQuery.Documents(db, k).Where(r => r.DocumentId == documentId)");
        Assert.DoesNotMatch(NarrowedPeriodShape, "StaleResultsQuery.Documents(db, key, byUserId).Select(r => r.DocumentId)");
    }

    [Fact]
    public void Форма_для_набору_шукає_по_префіксу_період_документ_прогін_без_історії_і_без_усього_періоду()
    {
        var sql = Code(SourceTree.Production("Ecr.Infrastructure").Single(f => f.Path == ByIdsPath)).ToUpperInvariant();
        string Squeeze(string s) => Regex.Replace(s, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
        var flat = Squeeze(sql);

        // Явний набір документів, а не всі документи періоду.
        Assert.Contains("FROM (VALUES", flat, StringComparison.Ordinal);

        // Актуальний прогін: EXISTS по (період, документ, прогін) — не агрегат по всіх результатах документа.
        Assert.Contains("CR.DOCUMENTID = D.DOCUMENTID AND CR.CALCULATIONRUNID = R0.ID", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY CR.DOCUMENTID", flat, StringComparison.Ordinal);

        // Довідники: лише рядки актуального прогону документа — не множинний `used` по всьому періоду.
        Assert.Contains("CR2.DOCUMENTID = X.DOCUMENTID AND CR2.CALCULATIONRUNID = X.RUNID", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("INNER HASH JOIN", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY CR2.CALCULATIONRUNID", flat, StringComparison.Ordinal);

        // План не залежить від оцінок: пошук саме по IX_CalculationResult_DocRun в обох місцях і бар'єр перед ребрами.
        Assert.Contains("RESULTSEEK = \"WITH (FORCESEEK, INDEX(IX_CALCULATIONRESULT_DOCRUN))\"", flat, StringComparison.Ordinal);
        Assert.Contains("FROM CALC.CALCULATIONRESULT AS CR {RESULTSEEK}", flat, StringComparison.Ordinal);
        Assert.Contains("FROM CALC.CALCULATIONRESULT AS CR2 {{RESULTSEEK}}", flat, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT TOP (2147483647) CR2.METHODOLOGYVERSIONID", flat, StringComparison.Ordinal);
    }

    /// <summary>Код без рядків-коментарів (опис колишньої форми в коментарях не рахується).</summary>
    private static string Code(SourceFile file)
        => string.Join(
            '\n',
            file.Text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
