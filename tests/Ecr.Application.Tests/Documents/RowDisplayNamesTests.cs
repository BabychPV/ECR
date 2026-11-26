using Ecr.Application.Documents;
using Ecr.Application.Localization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// integ-row-label-messages (follow-up): серверні відмови називають рядок «N» / «N (назва)» — номером у порядку зрізу
/// (шаблон за <c>Ordinal</c>, далі за Id; клієнт показує index+1), а не службовим ключем; рядка немає — «(added by a user)».
/// </summary>
public sealed class RowDisplayNamesTests
{
    private const string A = "adc95dc2bdb84fa0bce2d80e0defc581";
    private const string B = "bbc95dc2bdb84fa0bce2d80e0defc582";
    private const string Template = "0123456789abcdef0123456789abcdef";

    private static RowDef Def(string key, int ordinal, string label)
        => new(3, RowKey.Create(key), ordinal, new LocalizedText(new Dictionary<string, string> { ["en"] = label }), RowKind.Item);

    [Fact]
    public void Номер_за_порядком_зрізу_шаблонні_рядки_за_Ordinal_потім_додані_за_Id_і_підпис_шаблонного()
    {
        var live = new Dictionary<string, long> { [A] = 10, [B] = 20, [Template] = 30 };

        var names = RowDisplayNames.Of(live, [Def(Template, 1, "B12 diesel generator DG-1")], [A, B, Template], "en");

        // Шаблонний рядок першим (хоч Id найбільший), далі A, B — як index+1 у `GetTableSliceHandler`.
        Assert.Equal("1 (B12 diesel generator DG-1)", names[Template]);
        Assert.Equal("2", names[A]);
        Assert.Equal("3", names[B]);
    }

    [Fact]
    public void Рядка_немає_і_короткий_код_у_назви_не_потрапляють()
    {
        var names = RowDisplayNames.Of(new Dictionary<string, long> { [A] = 1, ["LTD-A01"] = 2 }, [], [B, "LTD-A01"], "en");

        Assert.Empty(names);
    }

    [Theory]
    [InlineData("en", "(added by a user)")]
    [InlineData("ru", "(добавлена пользователем)")]
    [InlineData("kz", "(пайдаланушы қосқан)")]
    public void Humanize_підставляє_назву_а_без_запису_запасну_позначку(string lang, string fallback)
    {
        var refusal = new Dictionary<string, string>
        {
            ["rowKeys"] = $"{A}, {B}",
            [RowKeyText.RowNamesParameter] = RowKeyText.EncodeNames(new Dictionary<string, string> { [A] = "2 (Diesel)" }),
        };

        var text = RowKeyText.Humanize(refusal, lang)["rowKeys"];

        Assert.Equal($"2 (Diesel), {fallback}", text);
    }

    [Fact]
    public void Merge_додає_rowNames_і_лишає_сирий_ключ_для_клієнта()
    {
        var details = new Dictionary<string, object?> { ["messageKey"] = "k", ["rowKey"] = A };

        var merged = RowDisplayNames.Merge(details, new Dictionary<string, string> { [A] = "4" });

        Assert.Equal(A, merged["rowKey"]);
        Assert.Equal($"{A}\t4", merged[RowKeyText.RowNamesParameter]);
        Assert.Same(details, RowDisplayNames.Merge(details, new Dictionary<string, string>()));
        Assert.Equal([A], RowKeyText.OpaqueKeysIn(details));
    }
}
