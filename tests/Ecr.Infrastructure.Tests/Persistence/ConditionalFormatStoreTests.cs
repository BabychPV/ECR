using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>ФВ-2.7: сховище правил умовного форматування — заміна набору й клон версії.</summary>
[Collection("SqlServer")]
public sealed class ConditionalFormatStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Заміна_набору_з_тими_самими_колонкою_і_порядком_не_б_ється_об_унікальний_індекс()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await ReplaceAsync(builder, doc.TemplateVersionId, Rule(doc.TemplateVersionId, 1, "gt", "10"));
        await ReplaceAsync(builder, doc.TemplateVersionId, Rule(doc.TemplateVersionId, 1, "lt", "3"));

        await using var db = builder.CreateContext();
        var stored = await new ConditionalFormatStore(db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        var only = Assert.Single(stored);
        Assert.Equal(("lt", "3"), (only.Operator, only.Value));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Клон_версії_несе_правила_у_тому_самому_порядку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        await ReplaceAsync(
            builder, doc.TemplateVersionId,
            Rule(doc.TemplateVersionId, 1, "gt", "10"), Rule(doc.TemplateVersionId, 2, "between", "1", "5"));

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, "9.9.9." + Guid.NewGuid().ToString("N")[..6], 1,
                new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        }

        await using var verify = builder.CreateContext();
        var cloned = await new ConditionalFormatStore(verify).GetAsync(cloneId, CancellationToken.None);
        Assert.Equal(["gt", "between"], cloned.Select(r => r.Operator));
        Assert.Equal("5", cloned[1].ValueTo);
    }

    private static ConditionalFormatRule Rule(int versionId, int ordinal, string op, string value, string? to = null)
        => new(versionId, "COL", ordinal, op, value, to, "#ff0000", null, false);

    private static async Task ReplaceAsync(TestDocumentBuilder builder, int versionId, params ConditionalFormatRule[] rules)
    {
        await using var db = builder.CreateContext();
        await new ConditionalFormatStore(db).ReplaceAsync(versionId, rules, CancellationToken.None);
        await db.SaveChangesAsync();
    }
}