using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// P1-05 / AN-112: фільтрований індекс <c>IX_CellChange_LateEdit</c> під позначку «пізня правка»
/// (<c>DocumentStore.LateEditDocumentIds</c>) є і в <c>aud.CellChange</c>, і в дзеркалі
/// <c>arc.AuditCellChange</c> — однаковий, ідемпотентний і вирівняний по <c>ps_AuditByMonth</c>.
/// </summary>
/// <remarks>
/// ⛔ Розбіжність між <c>11-audit-tables.sql</c> і <c>12-archive-tables.sql</c> валить SWITCH
/// партиції в архів (Msg 4943/4904/4912/4913). Ключ (DocumentId, PeriodKey) + INCLUDE (ColumnDefId)
/// — це предикат запиту після AN-109: <c>DocumentId IN (…)</c>, <c>PeriodKey = @p</c>, <c>ColumnDefId</c>
/// у NOT EXISTS схованих колонок; <c>IsLateEdit = 1</c> — фільтр індексу.
/// </remarks>
public sealed class AuditLateEditIndexTests
{
    [Theory]
    [InlineData("11-audit-tables.sql", "aud.CellChange")]
    [InlineData("12-archive-tables.sql", "arc.AuditCellChange")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Індекс_пізніх_правок_є_ідемпотентний_і_вирівняний(string script, string table)
    {
        var path = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", script);
        var sql = Regex.Replace(File.ReadAllText(path), @"\s+", " ");

        Assert.Contains(
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_LateEdit' "
            + $"AND object_id = OBJECT_ID(N'{table}'))",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"N'CREATE INDEX IX_CellChange_LateEdit ' + N'ON {table} (DocumentId, PeriodKey) INCLUDE (ColumnDefId) ' "
            + "+ N'WHERE IsLateEdit = 1'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("THEN N' WITH (ONLINE = ON)' ELSE N'' END + N' ON ps_AuditByMonth(ChangedAt);'", sql, StringComparison.Ordinal);
    }
}
