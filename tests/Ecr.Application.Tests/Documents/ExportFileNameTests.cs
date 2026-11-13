using System.Text;
using Ecr.Application.Common;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Аудит 2026-11, X-1 і X-8: людські й безпечні імена файлів експорту.</summary>
public sealed class ExportFileNameTests
{
    [Theory]
    [InlineData("Звіт Тенгіз", "LAND-0007", 202611, "Monthly", "Звіт Тенгіз (LAND-0007) 2026-11")]
    [InlineData(null, "P3-V12-0007", 202602, "Quarterly", "P3-V12-0007 Q2 2026")]
    [InlineData("Цех №1 \"Північ\"/Південь", "K1", 202601, "Yearly", "Цех №1 'Північ'-Південь (K1) 2026")]
    [InlineData("a:b*c?d<e>f|g\\h", "K", 202611, "Monthly", "a_b_c_d_e_f-g-h (K) 2026-11")]
    [InlineData("Қазақстан Ұлттық", "KZ-1", 202611, "Monthly", "Қазақстан Ұлттық (KZ-1) 2026-11")]
    [InlineData(" .Назва. ", "K", 202611, "Monthly", "Назва (K) 2026-11")]
    [InlineData("Line1\r\nLine2\tX", "K", 202611, "Monthly", "Line1 Line2 X (K) 2026-11")]
    [InlineData("K", "K", 202611, "Monthly", "K 2026-11")]
    [InlineData("CON", "K", 202611, "Monthly", "_CON (K) 2026-11")]
    [InlineData(null, "", 202605, "Custom", "document 2026-P05")]
    public void ForDocument_дає_людське_безпечне_ім_я(
        string? name, string? businessKey, int periodKey, string? periodKind, string expected)
        => Assert.Equal(expected, ExportFileName.ForDocument(businessKey, name, periodKey, periodKind));

    [Fact]
    public void Довга_назва_ріжеться_а_ключ_і_період_лишаються()
    {
        var result = ExportFileName.ForDocument("LAND-0007", new string('Д', 300), 202611, "Monthly");

        Assert.True(result.Length <= ExportFileName.MaxBaseLength, $"довжина {result.Length}");
        Assert.EndsWith(" (LAND-0007) 2026-11", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Різ_не_розриває_сурогатну_пару()
    {
        var result = ExportFileName.ForDocument("K", string.Concat(Enumerable.Repeat("😀", 60)), 202611, "Monthly");

        Assert.False(char.IsHighSurrogate(result[result.IndexOf(" (K)", StringComparison.Ordinal) - 1]));
        Assert.Equal(result, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result)));
    }

    [Theory]
    [InlineData("CON", "x", "_CON")]
    [InlineData("lpt1.txt", "x", "_lpt1.txt")]
    [InlineData("   ", "snapshot-7", "snapshot-7")]
    [InlineData("a<>b", "x", "a__b")]
    public void Sanitize_знешкоджує_заборонені_імена_й_символи(string raw, string fallback, string expected)
        => Assert.Equal(expected, ExportFileName.Sanitize(raw, fallback));

    [Theory]
    [InlineData(202611, "Monthly", "2026-11")]
    [InlineData(202602, "Quarterly", "Q2 2026")]
    [InlineData(202601, "Yearly", "2026")]
    [InlineData(202605, null, "2026-P05")]
    public void Period_читабельний_за_періодичністю(int periodKey, string? periodKind, string expected)
        => Assert.Equal(expected, ExportFileName.Period(periodKey, periodKind));

    [Theory]
    [InlineData("Звіт Тенгіз (LAND-0007) 2026-11", "document-42", "LAND-0007 2026-11")]
    [InlineData("Report (K1) Q2 2026", "document-42", "Report (K1) Q2 2026")]
    [InlineData("Қазақ", "document-42", "document-42")]
    public void AsciiFallback_лишає_друковані_ASCII_або_бере_запасне(string baseName, string asciiDefault, string expected)
        => Assert.Equal(expected, ExportFileName.AsciiFallback(baseName, asciiDefault));

    [Theory]
    [InlineData("IEC", "Water report", 202602, "Quarterly", 7L, "Water report (IEC) Q2 2026 #7")]
    [InlineData(null, null, null, null, 7L, "snapshot #7")]
    [InlineData("IEC", null, null, null, 7L, "IEC #7")]
    public void ForSnapshot_називає_зріз_за_тим_самим_правилом(
        string? reportCode, string? reportName, int? periodKey, string? periodKind, long snapshotId, string expected)
        => Assert.Equal(expected, ExportFileName.ForSnapshot(reportCode, reportName, periodKey, periodKind, snapshotId));
}
