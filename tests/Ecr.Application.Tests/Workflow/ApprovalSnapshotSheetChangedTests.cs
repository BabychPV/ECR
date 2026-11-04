using System.Globalization;
using System.Text.Json;
using Ecr.Application.Workflow;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// AN-135: «примусовий» шлях затвердження (хеш живих даних ≠ хешу зрізу) — <see cref="ApprovalSnapshotRefresh.SheetChanged"/>
/// і однопрохідний <see cref="SubmissionPayload.Read"/>. Рішення мусить лишитися тим самим, що й до оптимізації:
/// порівняння МНОЖИН клітинок аркуша (рядок, колонка, значення, тип).
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітиться): у <c>ApprovalSnapshotRefresh.SequenceEqual</c> пропустити останню клітинку
/// (<c>i &lt; left.Count - 1</c>) — червоніє <see cref="Changed_last_cell_of_the_sheet_is_detected"/> (поелементний
/// швидкий шлях вирішив би «без змін»).
/// </remarks>
public sealed class ApprovalSnapshotSheetChangedTests
{
    private static readonly HashSet<int> Sheet = [10, 11];

    private static string Payload(IEnumerable<SubmissionPayloadCell> cells, bool envelope)
        => envelope
            ? JsonSerializer.Serialize(new
            {
                cells = cells.ToList(),
                header = new SortedDictionary<string, SubmissionPayloadHeaderValue>(StringComparer.Ordinal)
                {
                    ["code"] = new("v", null),
                },
            })
            : JsonSerializer.Serialize(cells.ToList());

    private static List<SubmissionPayloadCell> Base() =>
    [
        new(1, 10, "1.5", null),
        new(1, 11, "abc", null),
        new(1, 99, "outside", null),
        new(2, 10, null, null),
        new(2, 11, "2026-01-01T00:00:00.0000000", SubmissionPayload.Date),
        new(3, 11, "7", null),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_change_is_not_a_change(bool envelope)
    {
        var p = Payload(Base(), envelope);

        Assert.False(ApprovalSnapshotRefresh.SheetChanged(p, Payload(Base(), envelope), Sheet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_changed_cell_is_detected(bool envelope)
    {
        var live = Base();
        live[1] = live[1] with { Value = "abd" };

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), envelope), Payload(live, envelope), Sheet));
    }

    [Fact]
    public void Changed_type_only_is_detected()
    {
        var live = Base();
        live[4] = live[4] with { Type = null };

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), true), Payload(live, true), Sheet));
    }

    [Fact]
    public void Changed_last_cell_of_the_sheet_is_detected()
    {
        var live = Base();
        live[^1] = live[^1] with { Value = "8" };

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), true), Payload(live, true), Sheet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_cell_is_detected(bool envelope)
    {
        var live = Base();
        live.Add(new(4, 10, "1", null));

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), envelope), Payload(live, envelope), Sheet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removed_cell_is_detected(bool envelope)
    {
        var live = Base();
        live.RemoveAt(3);

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), envelope), Payload(live, envelope), Sheet));
    }

    [Fact]
    public void Change_outside_the_sheet_columns_is_not_a_change()
    {
        var live = Base();
        live[2] = live[2] with { Value = "changed" };
        live.Add(new(9, 99, "new", null));

        Assert.False(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), true), Payload(live, true), Sheet));
    }

    [Fact]
    public void Order_and_duplicates_do_not_matter_set_semantics_kept()
    {
        var live = Base();
        live.Reverse();
        live.Add(Base()[0]);

        Assert.False(ApprovalSnapshotRefresh.SheetChanged(Payload(Base(), false), Payload(live, true), Sheet));
    }

    [Fact]
    public void No_snapshot_is_a_change()
        => Assert.True(ApprovalSnapshotRefresh.SheetChanged(null, "[]", Sheet));

    [Fact]
    public void Malformed_snapshot_payload_still_throws_JsonException()
    {
        Assert.ThrowsAny<JsonException>(() => ApprovalSnapshotRefresh.SheetChanged("[{\"row\":1,", "[]", Sheet));
        Assert.ThrowsAny<JsonException>(() => ApprovalSnapshotRefresh.SheetChanged("[]", "{\"cells\":5}", Sheet));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("  \r\n[{\"row\":1,\"column\":2,\"value\":\"x\",\"type\":\"ref\",\"extra\":1}]")]
    [InlineData("{}")]
    [InlineData("{\"header\":{\"a\":{\"value\":\"1\"}}}")]
    [InlineData("{\"cells\":null}")]
    [InlineData("{\"cells\":[{\"row\":1,\"column\":2,\"value\":\"a\"}],\"cells\":[{\"row\":3,\"column\":4,\"value\":null}]}")]
    [InlineData("{\"Cells\":[{\"row\":1,\"column\":2,\"value\":\"a\"}]}")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Read_is_equivalent_to_the_previous_two_pass_reader(string json)
        => Assert.Equal(PreviousRead(json), SubmissionPayload.Read(json));

    [Theory]
    [InlineData("")]
    [InlineData("[1,")]
    [InlineData("{\"cells\":[]} x")]
    [InlineData("{\"cells\":{}}")]
    [InlineData("{\"cells\":[],\"header\":{]}")]
    public void Read_throws_where_the_previous_reader_threw(string json)
    {
        Assert.ThrowsAny<JsonException>(() => PreviousRead(json));
        Assert.ThrowsAny<JsonException>(() => SubmissionPayload.Read(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_generated_payload_old_and_new_compare_agree(bool envelope)
    {
        var rnd = new Random(135);
        var cells = Enumerable.Range(0, 20_000)
            .Select(i => new SubmissionPayloadCell(
                1_000 + (i / 20), 10 + (i % 20),
                i % 13 == 0 ? null : (rnd.NextDouble() * 1e6).ToString("0.000000", CultureInfo.InvariantCulture),
                i % 29 == 0 ? SubmissionPayload.Unit : null))
            .ToList();
        var sheet = Enumerable.Range(10, 10).ToHashSet();
        var latest = Payload(cells, envelope);

        Assert.Equal(PreviousRead(latest), SubmissionPayload.Read(latest));

        for (var k = 0; k < 40; k++)
        {
            var live = cells.ToList();
            var at = rnd.Next(live.Count);
            switch (k % 4)
            {
                case 0: live[at] = live[at] with { Value = "x" + k }; break;
                case 1: live.RemoveAt(at); break;
                case 2: live.Insert(at, new(9_999, 10 + rnd.Next(20), "n", null)); break;
                default: break;
            }

            var livePayload = Payload(live, envelope);
            Assert.Equal(
                PreviousSheetChanged(latest, livePayload, sheet),
                ApprovalSnapshotRefresh.SheetChanged(latest, livePayload, sheet));
        }
    }

    /// <summary>Реалізація до AN-135 — еталон еквівалентності.</summary>
    private static List<SubmissionPayloadCell> PreviousRead(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<SubmissionPayloadCell>>(root.GetRawText()) ?? [];
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cells", out var cellsElement))
        {
            return JsonSerializer.Deserialize<List<SubmissionPayloadCell>>(cellsElement.GetRawText()) ?? [];
        }

        return [];
    }

    private static bool PreviousSheetChanged(string latest, string live, HashSet<int> sheet)
    {
        var before = PreviousRead(latest).Where(c => sheet.Contains(c.Column)).ToHashSet();
        var after = PreviousRead(live).Where(c => sheet.Contains(c.Column)).ToHashSet();
        return !before.SetEquals(after);
    }
}
