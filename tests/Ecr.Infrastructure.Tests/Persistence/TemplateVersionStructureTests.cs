// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionStructureTests.cs
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Завантаження версії шаблону зі структурою — на <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Саме на базі, і саме тому, що дефект був у різниці між моком і
/// реалізацією. Одинадцять тестів публікації підставляли
/// <c>Substitute.For&lt;IRepository&lt;TemplateVersion, int&gt;&gt;()</c>, який
/// віддавав граф <c>Sheets → Tables → Columns</c>, зібраний рефлексією в
/// пам'яті. Справжній <c>Repository.GetAsync</c> — це <c>FindAsync</c> без
/// жодного <c>Include</c> при вимкненому лінивому завантаженні, тобто версія з
/// ПОРОЖНІМИ навігаціями.
///
/// ⛔ Наслідок вимірювався живим прогоном (<c>A2 §3.1</c>): версія з формулою
/// <c>SUM([CurrentRow].[НЕМАЄ_ТАКОЇ]</c> — незакрита дужка І посилання на
/// неіснуючу колонку — публікувалася кодом <c>204</c>. Дванадцять перевірок
/// §12 не бачили жодної формули, бо бачити не було чого.
///
/// ⚠ Другий тест перевіряє СТАРИЙ шлях і має лишатися зеленим: він фіксує,
/// чим саме мок відрізнявся від реалізації. Якщо він колись почервоніє —
/// значить <c>IRepository</c> навчився вантажити навігації, і тоді перший
/// тест перестане щось доводити.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionStructureTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Сховище_віддає_версію_РАЗОМ_зі_структурою()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 3, rowCount: 4);

        // ⛔ ОКРЕМИЙ контекст. Той, у якому будували, тримає весь граф у карті
        // ідентичності, і будь-яке завантаження повернуло б заповнені
        // навігації — тобто тест зеленів би на зламаному коді.
        await using var db = builder.CreateContext();
        var store = new TemplateVersionStore(db);

        var version = await store.GetWithStructureAsync(document.TemplateVersionId, CancellationToken.None);

        Assert.NotEmpty(version.Sheets);

        var tables = version.Sheets.SelectMany(s => s.Tables).ToList();
        Assert.NotEmpty(tables);
        Assert.Equal(3, tables.SelectMany(t => t.Columns).Count());
        Assert.Equal(4, tables.SelectMany(t => t.Rows).Count());

        // ⛔ Головне твердження: знімок, на якому працюють ВСІ перевірки
        // публікації, бачить колонки. Порожній знімок не давав жодної
        // діагностики й пропускав будь-яку версію.
        var snapshot = PublishChecks.Snapshot(version);
        Assert.Equal(3, snapshot.ColumnsById.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Узагальнений_репозиторій_віддає_ту_саму_версію_БЕЗ_структури()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 3, rowCount: 4);

        await using var db = builder.CreateContext();
        var repository = new Repository<TemplateVersion, int>(db);

        var version = await repository.GetAsync(document.TemplateVersionId, CancellationToken.None);

        // ⚠ Це не дефект репозиторію: він і задуманий тонким («усе складніше
        // за знайти за ключем пишеться спеціалізованим портом»). Дефект був у
        // тому, що публікація брала версію ЗВІДСИ і мовчки працювала на
        // порожнечі.
        Assert.Empty(version.Sheets);

        var snapshot = PublishChecks.Snapshot(version);
        Assert.Empty(snapshot.ColumnsById);
    }
}
