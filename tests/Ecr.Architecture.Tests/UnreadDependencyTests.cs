using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Вприснута й не читана залежність — помилка складання, а не попередження.
/// </summary>
/// <remarks>
/// ⛔ <c>CS9113</c> стояв у <c>WarningsNotAsErrors</c>, і саме через це
/// найдорожчий дефект моделі доступу пролежав непоміченим: компілятор казав
/// прямо і безкоштовно «Parameter 'access' is unread» у
/// <c>CreateRowHandler</c> — тобто обробник створення рядка не питає прав
/// ЖОДНОГО разу. Повідомлення тонуло серед 39 інших попереджень, а побачити
/// його можна було лише при <c>--no-incremental</c>.
///
/// ⚠ Знята заборона на рівні дерева нічого не варта, якщо її можна тихо
/// повернути. Тому перевірок дві: код не в переліку послаблень і в дереві
/// немає нових точкових придушень, крім єдиного оголошеного.
/// </remarks>
public sealed class UnreadDependencyTests
{
    /// <summary>
    /// Єдине місце, де <c>CS9113</c> придушений навмисно.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>SubmitSheetHandler</c> тримає <c>ValidationEngine</c> вприснутим і
    /// не читаним НАВМИСНО: <c>ФВ-5.4</c> вимагає, щоб рівні рядка, таблиці й
    /// документа блокували <c>Submit</c>, а подання не кличе валідацію взагалі.
    /// Прибрати параметр — найдешевший спосіб зробити прогалину невидимою
    /// (<c>Q-146</c>).
    /// </remarks>
    private const string Allowed = "SubmitSheetHandler.cs";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void CS9113_не_послаблений_на_рівні_дерева()
    {
        var props = File.ReadAllText(Path.Combine(SolutionRoot(), "Directory.Build.props"));

        var start = props.IndexOf("<WarningsNotAsErrors>", StringComparison.Ordinal);
        Assert.True(start >= 0, "У Directory.Build.props немає WarningsNotAsErrors.");

        var end = props.IndexOf("</WarningsNotAsErrors>", start, StringComparison.Ordinal);
        var relaxed = props[start..end];

        // ⛔ Повідомлення пояснює НАСЛІДОК, а не факт: «CS9113 у переліку» саме
        // по собі виглядає дрібницею, і повернув би його той, кому воно
        // завадило зібратися.
        Assert.False(
            relaxed.Contains("CS9113", StringComparison.Ordinal),
            "CS9113 повернувся в WarningsNotAsErrors. Це попередження — єдине, що вказало "
            + "на обробник створення рядка, який не питав прав: залежність була вприснута "
            + "й не читана. Не читана залежність означає невиконану вимогу, а не зайвий "
            + "аргумент.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Точкові_придушення_CS9113_лише_там_де_оголошено()
    {
        var root = SolutionRoot();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // ⚠ Шукається саме ПРИДУШЕННЯ, а не згадка. Перша редакція цього
            // сторожа читала будь-яке входження рядка `CS9113` — і негайно
            // впала на коментарі в `CreateRowHandler`, який пояснює, що саме
            // це попередження на дефект і вказало. Сторож, який забороняє
            // згадувати код помилки, прибирають разом із поясненням.
            .Where(f => File.ReadAllText(f)
                .Contains("#pragma warning disable CS9113", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Where(f => !string.Equals(f, Allowed, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
