using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Q1-05: індекс <c>IX_StructureChange_Entity (EntityType, EntityId, ChangedAt)</c> є і в <c>aud.StructureChange</c>,
/// і в дзеркалі <c>arc.AuditStructureChange</c> — однаковий, ідемпотентний і вирівняний по <c>ps_AuditByMonth</c>.
/// </summary>
/// <remarks>
/// ⛔ Розбіжність між <c>11-audit-tables.sql</c> і <c>12-archive-tables.sql</c> валить SWITCH партиції в архів
/// (Msg 4943/4904/4912/4913); справжній SWITCH доводить <c>AuditStructureChangeEntityIndexTests</c> в
/// Infrastructure.Tests, тут — дешевий сторож тексту, що ловить розбіжність без бази. Мутація: прибрати індекс із
/// одного зі скриптів або змінити порядок стовпців ключа в одному з них — червоніє.
/// </remarks>
public sealed class StructureChangeIndexMirrorTests
{
    [Theory]
    [InlineData("11-audit-tables.sql", "aud.StructureChange")]
    [InlineData("12-archive-tables.sql", "arc.AuditStructureChange")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Індекс_історії_сутності_є_ідемпотентний_і_вирівняний(string script, string table)
    {
        var path = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", script);
        var sql = Regex.Replace(File.ReadAllText(path), @"\s+", " ");

        Assert.Contains(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StructureChange_Entity' "
            + $"AND object_id = OBJECT_ID(N'{table}'))",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"N'CREATE INDEX IX_StructureChange_Entity ' + N'ON {table} (EntityType, EntityId, ChangedAt)'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("THEN N' WITH (ONLINE = ON)' ELSE N'' END + N' ON ps_AuditByMonth(ChangedAt);'", sql, StringComparison.Ordinal);
    }
}
