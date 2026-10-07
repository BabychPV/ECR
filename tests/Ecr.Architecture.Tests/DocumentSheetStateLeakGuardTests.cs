using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Фікс S0 (R1/R2): склад, стан і лічильники аркушів документа (<c>DocumentSummary.Sheets</c>,
/// <c>SheetStates</c>, <c>SheetCount</c>, <c>ErrorCount</c>) не віддаються читачеві, що не бачить
/// аркуша (<c>Deny</c> / звуження ролі аркушами).
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>DocumentStore</c> віддає ПОВНИЙ склад — його читають і шляхи запису; межу читача
/// накладає обробник через <c>DocumentSheetVisibility.ApplyAsync</c>. Сторож тримає цей клас
/// закритим для НОВИХ місць: файл застосунку, що працює з <c>DocumentSummary</c>, мусить або
/// проходити через <c>DocumentSheetVisibility</c>, або стояти в <see cref="NotReturnedToReader"/>
/// з причиною. Порт і сховище (<c>Ports/</c>, <c>DocumentStore.cs</c>) — місце народження даних, не
/// споживач. Поведінку тримає <c>DocumentListHiddenSheetScopeTests</c> (Api.Tests).
/// Мутація (локально): прибрати виклик <c>ApplyAsync</c> з <c>DocumentQueryHandlers.cs</c> —
/// червоніє і цей сторож, і поведінковий тест.
/// </remarks>
public sealed class DocumentSheetStateLeakGuardTests
{
    /// <summary>Файли, що згадують <c>DocumentSummary</c>, але не віддають його читачеві, — з причиною.</summary>
    private static readonly Dictionary<string, string> NotReturnedToReader = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Api/Controllers/DocumentsController.cs"] =
            "лише ProducesResponseType: тіло збирає GetDocumentHandler/ListDocumentsHandler (фільтр у них)",
        ["src/Ecr.Application/Documents/DocumentVersionHandlers.cs"] =
            "RequireAsync бере GetDocumentHandler як ворота видимості; склад і стан далі не віддаються",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Склад_і_стан_аркушів_документа_віддаються_лише_через_межі_читача()
    {
        var consumers = SourceTree.Production()
            .Where(f => !f.Path.Contains("/Ports/", StringComparison.Ordinal)
                        && !f.Path.EndsWith("Persistence/DocumentStore.cs", StringComparison.Ordinal))
            .Select(f => (f.Path, Code: string.Join('\n', f.CodeLines().Select(l => l.Text))))
            .Where(f => f.Code.Contains("DocumentSummary", StringComparison.Ordinal))
            .ToList();

        var fresh = consumers
            .Where(f => !f.Code.Contains("DocumentSheetVisibility", StringComparison.Ordinal)
                        && !NotReturnedToReader.ContainsKey(f.Path))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();
        var gone = NotReturnedToReader.Keys
            .Except(consumers.Select(f => f.Path), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(consumers);
        Assert.True(
            fresh.Count == 0,
            "Файл застосунку працює з DocumentSummary (склад, стан і лічильники аркушів) повз межі читача: "
            + "пропусти документи через DocumentSheetVisibility.ApplyAsync або впиши файл у "
            + "NotReturnedToReader з причиною:" + Environment.NewLine + string.Join(Environment.NewLine, fresh));
        Assert.True(
            gone.Count == 0,
            "Файл більше не згадує DocumentSummary — прибери його з NotReturnedToReader:"
            + Environment.NewLine + string.Join(Environment.NewLine, gone));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Обробники_переліку_і_картки_документа_накладають_межі_читача()
    {
        var text = SourceTree.Production()
            .Single(f => f.Path.EndsWith("Documents/DocumentQueryHandlers.cs", StringComparison.Ordinal));
        var code = string.Join('\n', text.CodeLines().Select(l => l.Text));

        // Один виклик на обробник: перелік і картка.
        var calls = code.Split("DocumentSheetVisibility", StringSplitOptions.None).Length - 1;
        Assert.True(calls >= 2, $"ListDocumentsHandler і GetDocumentHandler мають викликати DocumentSheetVisibility.ApplyAsync (знайдено {calls}).");
    }
}
