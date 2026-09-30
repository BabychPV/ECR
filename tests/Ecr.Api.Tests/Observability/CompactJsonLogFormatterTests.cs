// tests/Ecr.Api.Tests/Observability/CompactJsonLogFormatterTests.cs

using System.Text.Json;
using Ecr.Api.Observability;
using Ecr.TestKit;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Ecr.Api.Tests.Observability;

/// <summary>Формат машиночитного журналу (<c>U20</c>): один рядок — один JSON-об'єкт CLEF.</summary>
public sealed class CompactJsonLogFormatterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U20")]
    public void Запис_це_один_рядок_JSON_з_полями_верхнього_рівня()
    {
        var template = new MessageTemplateParser().Parse("Запит відхилено: {Code} ({Status}). CorrelationId={CorrelationId}");
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 9, 28, 13, 5, 7, 123, TimeSpan.FromHours(3)),
            LogEventLevel.Warning,
            new InvalidOperationException("зламалось \"тут\"\nі тут"),
            template,
            [
                new LogEventProperty("Code", new ScalarValue("ECR-AUTH-0403")),
                new LogEventProperty("Status", new ScalarValue(403)),
                new LogEventProperty("CorrelationId", new ScalarValue("corr-1")),
                new LogEventProperty("UserId", new ScalarValue("42")),
                new LogEventProperty("@evil", new ScalarValue("x")),
            ]);

        using var writer = new StringWriter();
        new CompactJsonLogFormatter().Format(logEvent, writer);
        var text = writer.ToString();

        // ⛔ Рядок на запис: перенос у винятку не має розірвати запис на два.
        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text.TrimEnd());

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal("2026-09-28T10:05:07.1230000Z", root.GetProperty("@t").GetString());
        Assert.Equal("Warning", root.GetProperty("@l").GetString());
        Assert.Equal(template.Text, root.GetProperty("@mt").GetString());
        Assert.Equal("Запит відхилено: \"ECR-AUTH-0403\" (403). CorrelationId=\"corr-1\"", root.GetProperty("@m").GetString());
        Assert.Contains("зламалось \"тут\"\nі тут", root.GetProperty("@x").GetString(), StringComparison.Ordinal);

        // ⚠ Поля, за якими агрегує SIEM, — верхнім рівнем і своїм типом.
        Assert.Equal("ECR-AUTH-0403", root.GetProperty("Code").GetString());
        Assert.Equal(403, root.GetProperty("Status").GetInt32());
        Assert.Equal("corr-1", root.GetProperty("CorrelationId").GetString());
        Assert.Equal("42", root.GetProperty("UserId").GetString());

        // ⚠ Властивість не може підмінити службове поле CLEF.
        Assert.Equal("x", root.GetProperty("@@evil").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U20")]
    public void Без_винятку_поля_x_немає()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("просто"), []);

        using var writer = new StringWriter();
        new CompactJsonLogFormatter().Format(logEvent, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        Assert.False(json.RootElement.TryGetProperty("@x", out _));
        Assert.Equal("Information", json.RootElement.GetProperty("@l").GetString());
    }
}
