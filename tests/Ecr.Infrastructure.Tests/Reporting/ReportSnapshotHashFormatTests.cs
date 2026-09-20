// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotHashFormatTests.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Domain.Entities.Reporting;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Канонічний формат числа в <c>ContentHash</c>: 16 знаків замість 10.
/// </summary>
/// <remarks>
/// Без бази: формат — чиста функція від рядків зрізу, і закріпити його треба
/// ДО того, як <c>rpt.ReportRow.ValueNumeric</c> стане <c>decimal(28,16)</c>.
/// Коло через базу перевіряють <c>ReportSnapshotVerifyTests</c> і
/// <c>ReportSnapshotLayoutTests</c> — там же лежить незмінність суми зрізу IEC.
/// </remarks>
public sealed class ReportSnapshotHashFormatTests
{
    private const long SnapshotId = 77;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public void Значення_до_десяти_знаків_дають_ту_саму_суму_що_й_формат_до_розширення()
    {
        // ⛔ Очікуване рахується ДОСЛІВНОЮ копією формату до розширення, а не
        // продуктовим кодом: інакше тест порівнював би код із самим собою.
        var rows = Rows(
            ("DocumentId", null, 4217m),
            ("RowKey", "row-1", null),
            ("Value", null, 12.5000000000m),
            ("Rate", null, 0.0000000001m),
            ("SubstanceEntryId", null, null));

        Assert.Equal(
            Convert.ToHexString(HashWithTenDigits(rows)),
            Convert.ToHexString(ReportSnapshotBuilder.ComputeHash(rows)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public void Числа_різні_на_шістнадцятому_знаку_дають_різні_суми()
    {
        var left = Rows(("Value", null, 0.1234567890123456m));
        var right = Rows(("Value", null, 0.1234567890123457m));

        Assert.NotEqual(
            Convert.ToHexString(ReportSnapshotBuilder.ComputeHash(left)),
            Convert.ToHexString(ReportSnapshotBuilder.ComputeHash(right)));

        // ⚠ Саме цього формат на 10 знаків не ловив: обидва числа зводилися в
        // «0.123456789», тобто підміна виглядала б як незмінений вміст.
        Assert.Equal(
            Convert.ToHexString(HashWithTenDigits(left)),
            Convert.ToHexString(HashWithTenDigits(right)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public void Хвостові_нулі_масштабу_не_змінюють_суми()
    {
        // Те саме число з різним МАСШТАБОМ: «5.0» при побудові й
        // «5.0000000000000000» після читання з `decimal(28,16)`.
        Assert.Equal(
            Convert.ToHexString(ReportSnapshotBuilder.ComputeHash(Rows(("Value", null, 5.0m)))),
            Convert.ToHexString(
                ReportSnapshotBuilder.ComputeHash(Rows(("Value", null, 5.0000000000000000m)))));
    }

    /// <summary>Сума за форматом на 10 знаків (копія <c>HashOf</c> + <c>Canonical</c> до розширення).</summary>
    private static byte[] HashWithTenDigits(IReadOnlyList<ReportRow> rows)
        => SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{TenDigits(r.ValueNumeric)}")))));

    private static string TenDigits(decimal? value)
        => value is { } number ? number.ToString("0.##########", CultureInfo.InvariantCulture) : string.Empty;

    private static List<ReportRow> Rows(params (string Column, string? Text, decimal? Number)[] cells)
    {
        var rows = new List<ReportRow>();

        foreach (var (column, text, number) in cells)
        {
            var row = new ReportRow(SnapshotId, 1, column);
            row.SetValue(text, number, null);
            rows.Add(row);
        }

        return rows;
    }
}
