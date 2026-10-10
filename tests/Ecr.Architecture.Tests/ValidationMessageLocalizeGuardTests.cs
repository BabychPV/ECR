using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// AN-47: <c>ValidationMessageTemplates.Localize</c> (<c>public static</c>) збирає текст повідомлення з його
/// <c>Params</c> - а ті несуть значення джерел перевірки, у тому числі прихованих від читача. Виклик допустимий лише
/// ПІСЛЯ маскування прихованого (<c>HiddenValidationIssues.ForViewer</c>) і лише в єдиному споживачі.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Метод статичний і відкритий, тож ніщо типами не заважає викликати його над НЕВІДФІЛЬТРОВАНИМИ
/// повідомленнями: значення недоступного читачеві джерела Check (лише в <c>Params</c>) потрапило б у текст. Сторож
/// тримає два факти за джерелами: (1) виклики є лише в <see cref="Consumer"/>; (2) там перший виклик стоїть ПІСЛЯ
/// <c>ForViewer(</c> у тексті обробника. Мутація (локально): перенести <c>.Select(... Localize ...)</c> над
/// <c>ForViewer(</c> або викликати <c>Localize</c> з іншого файла - червоніє відповідний тест.
/// ⚠ Новий законний споживач - це рішення, а не правка списку: спершу маскування, далі впиши файл у
/// <see cref="Consumer"/> разом із цим поясненням.
/// </remarks>
public sealed class ValidationMessageLocalizeGuardTests
{
    private const string Definition = "src/Ecr.Application/Validation/ValidationMessageTemplates.cs";

    private const string Consumer = "src/Ecr.Application/Documents/GetValidationResultHandler.cs";

    private const string Call = "ValidationMessageTemplates.Localize(";

    private const string Filter = "HiddenValidationIssues.ForViewer(";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Finding", "AN-47")]
    public void Localize_викликається_лише_з_обробника_результату_перевірки()
    {
        var files = SourceTree.Production();

        // ⛔ Самоперевірка: метод існує, і споживач його справді викликає - без цього сторож зелений на порожньому.
        var definition = Code(files.Single(f => f.Path == Definition));
        Assert.Contains("public static ValidationMessage Localize(", definition, StringComparison.Ordinal);

        var callers = files
            .Where(f => f.Path != Definition)
            .Where(f => Code(f).Contains(".Localize(", StringComparison.Ordinal)
                        && Code(f).Contains("ValidationMessageTemplates", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([Consumer], callers);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Finding", "AN-47")]
    public void Localize_у_обробнику_стоїть_після_маскування_прихованого()
    {
        var code = Code(SourceTree.Production().Single(f => f.Path == Consumer));

        var filter = code.IndexOf(Filter, StringComparison.Ordinal);
        var firstCall = code.IndexOf(Call, StringComparison.Ordinal);

        Assert.True(filter >= 0, $"У {Consumer} зник виклик {Filter}: маскування прихованого перед локалізацією обов'язкове.");
        Assert.True(firstCall >= 0, $"У {Consumer} зник виклик {Call}: сторож нічого б не охороняв.");
        Assert.True(
            filter < firstCall,
            $"У {Consumer} {Call} стоїть ДО {Filter}: значення прихованих джерел (Params) потрапили б у текст читача.");

        // Друга половина: жодного виклику локалізації до фільтра, навіть другого.
        var calls = new List<int>();
        for (var at = code.IndexOf(Call, StringComparison.Ordinal); at >= 0; at = code.IndexOf(Call, at + 1, StringComparison.Ordinal))
        {
            calls.Add(at);
        }

        Assert.All(calls, position => Assert.True(position > filter, "Виклик Localize до ForViewer."));
    }

    private static string Code(SourceFile file) => string.Join('\n', file.CodeLines().Select(l => l.Text));
}
