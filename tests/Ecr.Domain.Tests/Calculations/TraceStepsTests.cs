// tests/Ecr.Domain.Tests/Calculations/TraceStepsTests.cs
using System.IO.Compression;
using System.Text;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// ✎ D-565 (PERF-03): прапор методології «трасувати проміжні кроки» і стиснений <c>TraceJson</c> кроку.
/// </summary>
/// <remarks>
/// ⛔ Мутації (локально): стискати UTF-8 замість UTF-16LE — червоний <see cref="Формат_той_самий_що_SQL_COMPRESS"/>;
/// у <see cref="CalculationStep.TraceJson"/> брати лише стиснену колонку — червоний
/// <see cref="Старий_нестиснений_крок_читається_як_раніше"/>; <c>Describe</c> пише текст, а не стиснене, — червоний
/// <see cref="Describe_пише_стиснене_і_читається_назад_тим_самим"/>; прапор за замовчуванням <c>true</c> — червоний
/// <see cref="Прапор_вимкнено_за_замовчуванням_і_зміна_повідомляє_про_себе"/>.
/// </remarks>
public sealed class TraceStepsTests
{
    private const string Json =
        """{"v":1,"expr":"CONVERT(!V_Sm3 * @Rho20, 'kg', 't') — тонни","result":"0.2581914962","unit":"t","inputs":[{"kind":"arg","code":"Rho20","value":"0.9589"}]}""";

    [Fact]
    public void Прапор_вимкнено_за_замовчуванням_і_зміна_повідомляє_про_себе()
    {
        var methodology = new Methodology(
            EcrCode.Create("D565"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));

        Assert.False(methodology.TraceSteps);
        Assert.False(methodology.SetTraceSteps(false));
        Assert.True(methodology.SetTraceSteps(true));
        Assert.True(methodology.TraceSteps);
        Assert.False(methodology.SetTraceSteps(true));
        Assert.True(methodology.SetTraceSteps(false));
        Assert.False(methodology.TraceSteps);
    }

    [Fact]
    public void Describe_пише_стиснене_і_читається_назад_тим_самим()
    {
        var step = new CalculationStep(1, 202601, 1, "M_t");

        step.Describe("!V_Sm3 * @Rho20", 0.2581914962m, Json, resultId: 5);

        Assert.Null(step.TraceJsonText);
        Assert.NotNull(step.TraceJsonGz);
        Assert.True(step.TraceJsonGz!.Length < Encoding.Unicode.GetByteCount(Json));
        Assert.Equal(Json, step.TraceJson);
    }

    [Fact]
    public void Крок_без_деталей_лишається_null()
    {
        var step = new CalculationStep(1, 202601, 1, "S");

        step.Describe("1+1", 2m, traceJson: null, resultId: null);

        Assert.Null(step.TraceJsonGz);
        Assert.Null(step.TraceJson);
        Assert.Null(TraceJsonCompression.Compress(null));
        Assert.Null(TraceJsonCompression.Decompress(null));
    }

    [Fact]
    public void Старий_нестиснений_крок_читається_як_раніше()
    {
        // Рядок до D-565: текст у колонці TraceJson, стисненої немає — так його матеріалізує EF.
        var step = new CalculationStep(1, 202601, 1, "OLD");
        typeof(CalculationStep).GetProperty(nameof(CalculationStep.TraceJsonText))!.SetValue(step, "#DIV/0");

        Assert.Equal("#DIV/0", step.TraceJson);
    }

    /// <remarks>
    /// Те, що дає SQL Server <c>COMPRESS(N'…')</c>: GZIP над UTF-16LE. Тож <c>CAST(DECOMPRESS(TraceJsonGz) AS
    /// nvarchar(max))</c> в інструменті DBA читає рівно той текст, що й код (наскрізно — у тесті сховища на SQL).
    /// </remarks>
    [Fact]
    public void Формат_той_самий_що_SQL_COMPRESS()
    {
        var compressed = TraceJsonCompression.Compress(Json)!;

        using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);

        Assert.Equal(Json, Encoding.Unicode.GetString(raw.ToArray()));
        Assert.Equal(Json, TraceJsonCompression.Decompress(compressed));
    }

    [Fact]
    public void Довгий_трейс_стискається_в_рази_і_повертається_побайтно()
    {
        var json = string.Concat(Enumerable.Repeat(Json, 40));

        var compressed = TraceJsonCompression.Compress(json)!;

        Assert.True(compressed.Length * 5 < Encoding.Unicode.GetByteCount(json), $"{compressed.Length} байт");
        Assert.Equal(json, TraceJsonCompression.Decompress(compressed));
    }

    [Theory]
    [InlineData(1999, false)]
    [InlineData(2000, false)]
    [InlineData(2001, true)]
    public void TrimExpression_та_сама_стеля_що_й_Describe(int length, bool trimmed)
    {
        var expression = new string('x', length);
        var step = new CalculationStep(1, 202601, 1, "F");
        step.Describe(expression, null, null, null);

        Assert.Equal(step.Expression, CalculationStep.TrimExpression(expression));
        Assert.Equal(trimmed, CalculationStep.TrimExpression(expression)!.EndsWith('…'));
    }
}
