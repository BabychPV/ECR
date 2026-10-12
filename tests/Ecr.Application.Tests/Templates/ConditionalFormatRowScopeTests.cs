using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// CELL-STYLE L6: умовне форматування за областю рядків, текстом і «статусом рядка» (колонка-джерело).
/// Оцінювач — чиста функція; тут же валідація правила й збереження набору.
/// </summary>
public sealed class ConditionalFormatRowScopeTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static ConditionalFormatRule Text(
        string op, string value, string column = "*", string? table = "T1", string? reference = "Status",
        string? bg = "#ff9900", string? pattern = null, string scope = "column", string[]? rowKeys = null,
        int ordinal = 1)
        => new(1, column, ordinal, op, value, null, bg, null, false, scope, table, rowKeys, reference, pattern);

    private static RuleCellContext Context(
        string table, string row, Dictionary<string, object?> values)
        => new(table, row, (string code, out object? found) => values.TryGetValue(code, out found));

    // ---------- оцінювач ----------

    [Theory]
    [InlineData("textEq", "Removed", "Removed", true)]
    [InlineData("textEq", "Removed", " removed ", true)]   // регістр і крайні пробіли — як в Excel «=»
    [InlineData("textEq", "Removed", "Running", false)]
    [InlineData("textNe", "Removed", "Running", true)]
    [InlineData("textNe", "Removed", "Removed", false)]
    public void Text_operators_compare_ignoring_case_and_edge_spaces(string op, string operand, string cell, bool expected)
    {
        var rule = Text(op, operand);
        var ctx = Context("T1", "R1", new Dictionary<string, object?> { ["Status"] = cell });

        Assert.Equal(expected, ConditionalFormatEvaluator.Evaluate([rule], null, ctx) is not null);
    }

    [Fact]
    public void Text_operators_do_not_match_numbers_dates_or_ids_even_textNe()
    {
        // Число не «текст, відмінний від Removed»: паритет із числовими операторами, що не беруть текст.
        foreach (var notText in new object[] { 5m, true, new DateTime(2026, 1, 1), 42L })
        {
            var ctx = Context("T1", "R1", new Dictionary<string, object?> { ["Status"] = notText });

            Assert.Null(ConditionalFormatEvaluator.Evaluate([Text("textNe", "Removed")], null, ctx));
            Assert.Null(ConditionalFormatEvaluator.Evaluate([Text("textEq", "Removed")], null, ctx));
        }
    }

    [Fact]
    public void Empty_status_is_text_so_textNe_matches_and_textEq_does_not()
    {
        var ctx = Context("T1", "R1", new Dictionary<string, object?> { ["Status"] = null });

        Assert.NotNull(ConditionalFormatEvaluator.Evaluate([Text("textNe", "Removed")], null, ctx));
        Assert.Null(ConditionalFormatEvaluator.Evaluate([Text("textEq", "Removed")], null, ctx));
    }

    [Fact]
    public void Status_of_the_row_paints_the_cell_of_another_column()
    {
        // Land: Land_Status = Removed → заливка FF9900 на КОМІРЦІ іншої колонки того ж рядка.
        var rule = Text("textEq", "Removed", column: "Volume", reference: "Land_Status");
        var removed = Context("T1", "R1", new Dictionary<string, object?> { ["Land_Status"] = "REMOVED" });
        var running = Context("T1", "R2", new Dictionary<string, object?> { ["Land_Status"] = "RUNNING" });

        Assert.Equal(new CellFormatDto("#ff9900", null, false), ConditionalFormatEvaluator.Evaluate([rule], 5m, removed));
        Assert.Null(ConditionalFormatEvaluator.Evaluate([rule], 5m, running));
    }

    [Fact]
    public void Hatch_pattern_travels_in_the_format()
    {
        var rule = Text("textEq", "N/A", bg: null, pattern: "hatch");
        var ctx = Context("T1", "R1", new Dictionary<string, object?> { ["Status"] = "N/A" });

        var format = ConditionalFormatEvaluator.Evaluate([rule], null, ctx);

        Assert.Equal(new CellFormatDto(null, null, false, "hatch"), format);
    }

    [Fact]
    public void Row_scope_applies_only_to_listed_rows_and_only_in_its_table()
    {
        var rule = new ConditionalFormatRule(
            1, "Jan", 1, "ne", "0", null, "#ff0000", null, false, "row", "T7", ["R700", "R701"]);

        Assert.NotNull(ConditionalFormatEvaluator.Evaluate([rule], 3m, Context("T7", "R700", new Dictionary<string, object?>())));
        Assert.Null(ConditionalFormatEvaluator.Evaluate([rule], 3m, Context("T7", "R999", new Dictionary<string, object?>())));
        // Той самий ключ рядка в ІНШІЙ таблиці не фарбується.
        Assert.Null(ConditionalFormatEvaluator.Evaluate([rule], 3m, Context("T8", "R700", new Dictionary<string, object?>())));
    }

    [Fact]
    public void Hidden_reference_column_disables_the_rule_instead_of_reading_it()
    {
        // Нерозкриття: резолвер каже «колонка недоступна» → правило не спрацьовує НАВІТЬ для textNe, що
        // збігалося б із порожнім значенням. Інакше вигляд комірки видавав би вміст прихованої колонки.
        var rule = Text("textNe", "Removed");
        var ctx = new RuleCellContext("T1", "R1", (string code, out object? found) =>
        {
            found = "Removed";
            return false;
        });

        Assert.Null(ConditionalFormatEvaluator.Evaluate([rule], null, ctx));
    }

    [Fact]
    public void Evaluation_without_context_skips_scoped_rules_instead_of_painting_blindly()
    {
        var scoped = Text("textEq", "Removed");
        var plain = new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#00ff00", null, false);

        Assert.Null(ConditionalFormatEvaluator.Evaluate([scoped], "Removed"));
        Assert.NotNull(ConditionalFormatEvaluator.Evaluate([plain], 2m));
    }

    [Fact]
    public void Column_specific_rules_win_over_all_columns_rules()
    {
        var own = new ConditionalFormatRule(
            1, "Volume", 1, "textEq", "Removed", null, "#111111", null, false, "column", "T1", null, "Status");
        var wide = Text("textEq", "Removed", bg: "#222222");
        var byColumn = ConditionalFormatEvaluator.ByColumn([wide, own]);
        var ctx = Context("T1", "R1", new Dictionary<string, object?> { ["Status"] = "Removed" });

        var format = ConditionalFormatEvaluator.Evaluate(ConditionalFormatEvaluator.RulesFor(byColumn, "Volume"), null, ctx);
        var other = ConditionalFormatEvaluator.Evaluate(ConditionalFormatEvaluator.RulesFor(byColumn, "Mass"), null, ctx);

        Assert.Equal("#111111", format!.BackgroundHex);
        Assert.Equal("#222222", other!.BackgroundHex);
    }

    // ---------- валідація правила ----------

    [Theory]
    [InlineData("textEq", "", "condFormatTextOperand")]
    [InlineData("textEq", "   ", "condFormatTextOperand")]
    public void Text_operator_needs_a_non_empty_text_operand(string op, string value, string key)
    {
        var ex = Assert.Throws<DomainException>(() => Text(op, value));

        Assert.Equal("err.ECR-CFG-0422." + key, ex.Details!["messageKey"]);
    }

    [Fact]
    public void Row_scope_needs_a_table_and_distinct_nonblank_keys_and_column_scope_must_have_none()
    {
        string Key(Func<object> act) => Assert.Throws<DomainException>(act).Details!["messageKey"]!.ToString()!;

        Assert.Equal("err.ECR-CFG-0422.condFormatTable", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, "row", null, ["R1"])));
        Assert.Equal("err.ECR-CFG-0422.condFormatRowKeys", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, "row", "T1", [])));
        Assert.Equal("err.ECR-CFG-0422.condFormatRowKeys", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, "row", "T1", ["R1", "R1"])));
        Assert.Equal("err.ECR-CFG-0422.condFormatRowKeys", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, "column", "T1", ["R1"])));
        Assert.Equal("err.ECR-CFG-0422.condFormatScope", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, "page")));
        Assert.Equal("err.ECR-CFG-0422.condFormatPattern", Key(() =>
            new ConditionalFormatRule(1, "A", 1, "gt", "1", null, "#ff0000", null, false, pattern: "dots")));
        // «Усі колонки» без таблиці фарбувало б усі таблиці версії.
        Assert.Equal("err.ECR-CFG-0422.condFormatTable", Key(() =>
            new ConditionalFormatRule(1, "*", 1, "gt", "1", null, "#ff0000", null, false)));
    }

    [Fact]
    public void CloneTo_copies_every_field_of_a_scoped_rule()
    {
        var source = new ConditionalFormatRule(
            3, "Volume", 4, "textNe", "Removed", null, "#ff9900", "#000000", true, "row", "T1", ["R1", "R2"], "Status", "hatch");

        var copy = source.CloneTo(9);

        Assert.Equal(9, copy.TemplateVersionId);
        Assert.Equal(
            (source.ColumnCode, source.Ordinal, source.Operator, source.Value, source.BackgroundHex, source.ForegroundHex,
                source.IsBold, source.Scope, source.TableCode, source.RefColumnCode, source.Pattern),
            (copy.ColumnCode, copy.Ordinal, copy.Operator, copy.Value, copy.BackgroundHex, copy.ForegroundHex,
                copy.IsBold, copy.Scope, copy.TableCode, copy.RefColumnCode, copy.Pattern));
        Assert.Equal(source.RowKeys, copy.RowKeys);
    }

    // ---------- збереження набору ----------

    private readonly IConditionalFormatStore _rules = Substitute.For<IConditionalFormatStore>();
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TemplateVersion _draft = new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
    private IReadOnlyList<ConditionalFormatRule>? _saved;
    private static readonly string Etag = $"\"{ConditionalFormatsVersion.Of([])}\"";

    public ConditionalFormatRowScopeTests()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "VOL");
        builder.Column(table, "Land_Status", Ecr.Domain.Enums.CellDataType.Lookup);
        builder.Row(table, "R1");
        builder.Row(table, "R2");
        var dynamic = builder.Table(sheet, "Dyn", Ecr.Domain.Enums.TableRowMode.Dynamic);
        builder.Column(dynamic, "VOL");
        _draft.AddSheet(sheet);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _draft.Status);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _rules.GetAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _rules.ReplaceAsync(1, Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _saved = call.ArgAt<IReadOnlyList<ConditionalFormatRule>>(1);
                return Task.CompletedTask;
            });
    }

    private SaveConditionalFormatsHandler Save()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new SaveConditionalFormatsHandler(_rules, _store, _uow, _access, _user, clock);
    }

    private static ConditionalFormatRuleDto Dto(
        string column = "*", string op = "textEq", string? value = "Removed", string? table = "Main",
        string? reference = "Land_Status", string? scope = null, IReadOnlyList<string>? rowKeys = null,
        string? pattern = null)
        => new(column, op, value, null, "#ff9900", null, false, scope, table, rowKeys, reference, pattern);

    [Fact]
    public async Task Scoped_rules_round_trip_through_the_set_with_all_fields()
    {
        var saved = await Save().HandleAsync(
            1,
            [
                Dto(),
                Dto(column: "VOL", op: "ne", value: "0", reference: null, scope: "row", rowKeys: ["R1", "R2"]),
                Dto(op: "textEq", value: "N/A", pattern: "hatch"),
            ],
            Etag, CancellationToken.None);

        Assert.Equal(3, saved.Count);
        Assert.Equal("*", _saved![0].ColumnCode);
        Assert.Equal("Land_Status", _saved[0].RefColumnCode);
        Assert.Equal("row", _saved[1].Scope);
        Assert.Equal(["R1", "R2"], _saved[1].RowKeys);
        Assert.Equal("hatch", saved[2].Pattern);
        // Колонкове правило без області віддається БЕЗ scope: контракт для старих клієнтів не змінюється.
        Assert.Null(saved[0].Scope);
    }

    [Fact]
    public async Task Unknown_table_reference_column_or_row_key_is_rejected_with_catalog_key()
    {
        async Task<string> Key(ConditionalFormatRuleDto rule)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(
                () => Save().HandleAsync(1, [rule], Etag, CancellationToken.None));

            return ex.Details!["messageKey"]!.ToString()!;
        }

        Assert.Equal("err.ECR-CFG-0422.condFormatTable", await Key(Dto(table: "Nope")));
        Assert.Equal("err.ECR-CFG-0422.condFormatRef", await Key(Dto(reference: "Missing")));
        Assert.Equal("err.ECR-CFG-0422.condFormatColumn", await Key(Dto(column: "Missing")));
        Assert.Equal(
            "err.ECR-CFG-0422.condFormatRowKeys",
            await Key(Dto(column: "VOL", op: "ne", value: "0", reference: null, scope: "row", rowKeys: ["R404"])));
        Assert.Equal("err.ECR-CFG-0422.condFormatTable", await Key(Dto(table: null)));   // «*» без таблиці
    }

    [Fact]
    public async Task Dynamic_table_row_keys_are_not_checked_against_the_template()
    {
        var saved = await Save().HandleAsync(
            1,
            [Dto(column: "VOL", op: "ne", value: "0", table: "Dyn", reference: null, scope: "row", rowKeys: ["R-new"])],
            Etag, CancellationToken.None);

        Assert.Single(saved);
    }

    [Fact]
    public void Set_version_is_unchanged_for_legacy_rules_and_changes_when_the_scope_changes()
    {
        var legacy = new ConditionalFormatRuleDto("VOL", "gt", "1", null, "#ff0000", null, false);
        var scoped = legacy with { Scope = "row", TableCode = "Main", RowKeys = ["R1"] };
        var otherRows = scoped with { RowKeys = ["R2"] };

        Assert.NotEqual(ConditionalFormatsVersion.Of([legacy]), ConditionalFormatsVersion.Of([scoped]));
        Assert.NotEqual(ConditionalFormatsVersion.Of([scoped]), ConditionalFormatsVersion.Of([otherRows]));
        // Хеш колонкового правила — той самий, що рахував алгоритм ДО L6 (рядок без полів області):
        // ETag відкритих редакторів не інвалідується.
        var before = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("VOL\u001fgt\u001f1\u001f\u001f#ff0000\u001f\u001f0\u001e"));
        Assert.Equal(Convert.ToHexString(before.AsSpan(0, 16)), ConditionalFormatsVersion.Of([legacy]));
    }
}
