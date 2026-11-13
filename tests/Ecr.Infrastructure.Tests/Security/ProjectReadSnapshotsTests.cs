using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// P-6 (аудит 2026-11-10): знімок-обʼєднання багатоверсійного проєкту будується раз на набір ЕКЗЕМПЛЯРІВ знімків
/// версій, а не на кожне звернення (зріз, PATCH, перелік документів).
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: приберіть кеш у <c>ProjectReadSnapshots.Merge</c> — «той самий набір → той самий обʼєкт»
/// червоніє. Кеш, що не дивиться на екземпляр іншої версії (лише на її Id), зламав би «нова ревізія версії» — це
/// шлях прав, застаріла структура показала б не ті колонки.
/// </remarks>
public sealed class ProjectReadSnapshotsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    public void Той_самий_набір_екземплярів_дає_той_самий_обʼєкт()
    {
        var current = Version(3, "S3", "T", "A", "B");
        var v1 = Version(1, "S1", "T", "A");
        var v2 = Version(2, "S2", "T", "A");

        var first = ProjectReadSnapshots.Merge(current, [v1, v2]);
        var second = ProjectReadSnapshots.Merge(current, [v1, v2]);

        Assert.Same(first, second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    public void Нова_ревізія_версії_дає_нове_обʼєднання_з_її_колонками()
    {
        var current = Version(3, "S3", "T", "A");
        var v1 = Version(1, "S1", "T", "A");
        var v2 = Version(2, "S2", "T", "A");
        var first = ProjectReadSnapshots.Merge(current, [v1, v2]);

        // Та сама версія (Id = 2), але інший екземпляр знімка з новою колонкою — так приходить нова ревізія з кешу метаданих.
        var v2Next = Version(2, "S2", "T", "A", "NEW");
        var second = ProjectReadSnapshots.Merge(current, [v1, v2Next]);

        Assert.NotSame(first, second);
        var added = v2Next.ColumnsById.Keys.Except(v2.ColumnsById.Keys).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, id => Assert.True(second.ColumnsById.ContainsKey(id)));
        Assert.All(added, id => Assert.False(first.ColumnsById.ContainsKey(id)));

        // Інший склад версій проєкту — теж окреме обʼєднання.
        Assert.NotSame(second, ProjectReadSnapshots.Merge(current, [v1]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    public void Вміст_обʼєднання_аркуші_всіх_версій_колонки_й_рядки_поточна_перемагає()
    {
        var current = Version(3, "S3", "T", "A");
        var v1 = Version(1, "S1", "T", "A", "B");

        var merged = ProjectReadSnapshots.Merge(current, [v1]);

        Assert.Equal(current.TemplateVersionId, merged.TemplateVersionId);
        Assert.Equal(
            [.. current.Sheets.Select(s => s.Id), .. v1.Sheets.Select(s => s.Id)],
            merged.Sheets.Select(s => s.Id).ToList());
        Assert.Equal(
            current.ColumnsById.Keys.Union(v1.ColumnsById.Keys).Order().ToList(),
            merged.ColumnsById.Keys.Order().ToList());
        Assert.Equal(
            current.RowsByKey.Keys.Union(v1.RowsByKey.Keys).Order().ToList(),
            merged.RowsByKey.Keys.Order().ToList());
        Assert.All(current.ColumnsById, c => Assert.Same(c.Value, merged.ColumnsById[c.Key]));

        // Вихідні знімки не змінено: обʼєднання — нові словники.
        Assert.Single(current.Sheets);
        Assert.DoesNotContain(v1.ColumnsById.Keys.Except(current.ColumnsById.Keys).First(), current.ColumnsById.Keys);
    }

    /// <summary>Версія з одним аркушем і таблицею; Id сутностей не перетинаються між версіями.</summary>
    private static TemplateVersionSnapshot Version(int versionId, string sheet, string table, params string[] columns)
    {
        var builder = new TemplateBuilder { TemplateVersionId = versionId, NextId = versionId * 1000 };
        var s = builder.Sheet(sheet);
        var t = builder.Table(s, table);
        foreach (var column in columns)
        {
            builder.Column(t, column);
        }

        builder.Row(t, "R1", 1);
        return builder.Build();
    }
}
