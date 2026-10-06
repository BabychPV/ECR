using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен параметр збереженої процедури зі скриптів розгортання вживається в її тілі
/// (аудит L10-14).
/// </summary>
/// <remarks>
/// ⛔ Уже траплялося: <c>arc.usp_ArchiveYear</c> приймав <c>@BatchSize</c>, застосунок
/// старанно підбирав його за редакцією сервера (<c>SqlCapabilitiesProbe</c>), health
/// показував його оператору — а тіло процедури параметра не читало. Налаштування,
/// яке нічого не налаштовує, гірше за відсутнє: йому вірять.
/// <para>
/// ⚠ Коментарі (<c>--</c> до кінця рядка) вирізаються: згадка параметра в поясненні
/// не є вживанням. Межа заголовка — перший рядок, що складається лише з <c>AS</c>
/// (так оформлені всі процедури в <c>Sql/*.sql</c>); процедура без такого рядка —
/// червоне з назвою, а не тихий пропуск.
/// </para>
/// Мутація: прибрати пакетне копіювання з <c>usp_ArchiveYear</c> (повернути один
/// <c>INSERT</c> на період) — червоне з <c>arc.usp_ArchiveYear: @BatchSize</c>.
/// </remarks>
public sealed partial class ProcedureParameterUsageTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Audit", "L10-14")]
    public void Параметри_процедур_у_Sql_скриптах_вживаються_в_тілі()
    {
        var directory = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");
        var procedures = 0;
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.sql"))
        {
            var text = string.Join('\n', File.ReadAllLines(path).Select(StripComment));

            foreach (var batch in BatchSeparator().Split(text))
            {
                var create = CreateProcedure().Match(batch);
                if (!create.Success)
                {
                    continue;
                }

                procedures++;
                var name = create.Groups["name"].Value;
                var rest = batch[(create.Index + create.Length)..];
                var asLine = AsLine().Match(rest);
                if (!asLine.Success)
                {
                    offenders.Add($"{Path.GetFileName(path)}: {name} — не знайдено рядка «AS» після заголовка");
                    continue;
                }

                var header = rest[..asLine.Index];
                var body = rest[(asLine.Index + asLine.Length)..];

                foreach (Match parameter in ParameterDeclaration().Matches(header))
                {
                    var parameterName = parameter.Groups["param"].Value;
                    var usage = new Regex(
                        Regex.Escape(parameterName) + @"(?![\w@#$])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                    if (!usage.IsMatch(body))
                    {
                        offenders.Add($"{Path.GetFileName(path)}: {name}: {parameterName}");
                    }
                }
            }
        }

        Assert.True(procedures >= 5, $"знайдено лише {procedures} процедур — сторож нічого не перевіряє");
        Assert.True(
            offenders.Count == 0,
            "Параметр процедури оголошено, але тіло його не читає — або вжити, або прибрати "
            + "разом із викликачами:\n" + string.Join('\n', offenders));
    }

    private static string StripComment(string line)
    {
        var at = line.IndexOf("--", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+ALTER\s+)?PROC(?:EDURE)?\s+(?<name>[\w\.\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateProcedure();

    [GeneratedRegex(@"^\s*AS\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AsLine();

    [GeneratedRegex(@"(?<param>@\w+)\s+(?:AS\s+)?[\w\.\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterDeclaration();
}
