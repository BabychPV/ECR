using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Числа статусів у <c>05-rpt-views.sql</c> відповідають енумам, з яких їх читає код.
/// </summary>
/// <remarks>
/// ⛔ Є ДВІ нумерації, і числа в них означають різне: <see cref="DocumentStatus"/>
/// (<c>wf.ApprovalState.Status</c>, колонка <c>Status</c>/<c>_Status</c> у сирих вʼюхах:
/// 1 = Submitted, 2 = Approved) і <see cref="SnapshotStatus"/>
/// (<c>rpt.ReportSnapshot.Status</c>, фільтр вʼюхи зрізу: 1 = Approved, 2 = Submitted).
/// Звіт регулятора, що взяв число з «не того» енума, мовчки віддав би чернетку
/// чи відхилене. Зміна нумерації будь-якого енума ламає цей тест, доки SQL не збігся.
/// </remarks>
public sealed class StatusNumberingTests
{
    private const string RptFile = "src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql";

    private static string Sql() => File.ReadAllText(
        Path.Combine(SourceTree.Root, RptFile.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Нумерація_енумів_статусів_зафіксована()
    {
        Assert.Equal(
            [(0, "Draft"), (1, "Submitted"), (2, "Approved"), (3, "Rejected")],
            Enum.GetValues<DocumentStatus>().OrderBy(v => (byte)v).Select(v => ((int)(byte)v, v.ToString())).ToArray());
        Assert.Equal(
            [(0, "Draft"), (1, "Approved"), (2, "Submitted")],
            Enum.GetValues<SnapshotStatus>().OrderBy(v => (byte)v).Select(v => ((int)(byte)v, v.ToString())).ToArray());
    }

    /// <remarks>
    /// Мутація: змінити в SQL <c>IN (1, 2)</c> на <c>IN (0, 1)</c> або переставити
    /// значення енума — тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вʼюха_зрізу_бере_Approved_і_Submitted_за_нумерацією_SnapshotStatus()
    {
        var expected = $"s.Status IN ({(byte)SnapshotStatus.Approved}, {(byte)SnapshotStatus.Submitted});   -- Approved, Submitted";
        Assert.Contains(expected, Sql(), StringComparison.Ordinal);
        Assert.Contains(
            "0 Draft, 1 Approved, 2 Submitted", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сирі_вʼюхи_віддають_статус_подання_за_нумерацією_DocumentStatus()
    {
        var sql = Sql();
        var legend = $"{(byte)DocumentStatus.Draft} Draft, {(byte)DocumentStatus.Submitted} Submitted, "
                     + $"{(byte)DocumentStatus.Approved} Approved,";

        // Легенда (у коментарі) — у примітці до сирих вʼюх.
        Assert.True(
            System.Text.RegularExpressions.Regex.Count(sql, System.Text.RegularExpressions.Regex.Escape(legend)) >= 1,
            "Легенда 0 Draft, 1 Submitted, 2 Approved має бути у примітці сирих вʼюх.");

        // Сирі вʼюхи не фільтрують за статусом: він лише колонка (довга й широка).
        Assert.DoesNotContain("a.Status IN", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("a.Status =", sql, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(sql, @"COALESCE\(a\.Status, 0\)"));
    }
}
