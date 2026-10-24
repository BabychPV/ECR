using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожне право з каталогу <c>sec.Permission</c> (<c>09-seed.sql</c>) є в списку прав ролі
/// <c>E2EAdmin</c> у <c>tools/e2e-stand.ps1</c> — або стоїть у <see cref="NotGrantedToE2e"/> з причиною (CL-27).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Стенд e2e видає акаунту <c>e2e-admin</c> ЯВНИЙ список прав. Нове право каталогу в нього
/// не потрапляє саме: у rc8 (18f89d149) <c>Permit.EditType</c>/<c>PublishType</c> забули, і
/// <c>permitTypes*.spec</c> та <c>screenshots</c> дали 403 лише на PR (#564/#565). Сторож перетворює
/// «забули» на червоний тест у <c>Ecr.Architecture.Tests</c> ще до пушу.
///
/// ⚠ Вихід — або додати право в <c>permissionCodes</c> ролі <c>E2EAdmin</c>, або вписати його в
/// <see cref="NotGrantedToE2e"/> з причиною, чому e2e його не потребує. Список парситься з тексту ps1
/// (коментарі зрізаються), тож зміна формату списку має падати ГОЛОСНО — див. <see cref="E2eAdminRights"/>.
/// </remarks>
public sealed partial class E2eStandPermissionsCoverageTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";
    private const string StandFile = "tools/e2e-stand.ps1";

    /// <summary>
    /// Права каталогу, яких e2e-адміністратор свідомо не має. Базова лінія 2026-10-19: жоден spec у
    /// <c>src/Ecr.Web/e2e</c> їх не згадує, а наявні прогони проходять без них.
    /// </summary>
    /// <remarks>
    /// ⛔ Ratchet у два боки: нове право каталогу без рядка тут і без E2EAdmin — червоне; право звідси,
    /// яке з'явилося в E2EAdmin або зникло з каталогу, — теж червоне (<see cref="Перелік_винятків_не_застарів"/>).
    /// Спеку, якому потрібне право звідси, — переносити його в E2EAdmin, а не розширювати цей список.
    /// </remarks>
    private static readonly Dictionary<string, string> NotGrantedToE2e = new(StringComparer.Ordinal)
    {
        ["Document.Delete"] = "e2e не видаляє документи; руйнівне право не потрібне стенду",
        ["Document.ChangeKey"] = "небезпечне (IsDangerous); зміна бізнес-ключа e2e не перевіряє",
        ["Document.Submit"] = "e2e подає аркуш рівнем Write через наявні гранти; окремий e2e на право не існує",
        // ✎ P44: `Calculation.EditFormula` перенесено в E2EAdmin — `resizableModal.spec.ts` заводить методологію
        // й відкриває вікно формули (розмір вікна, Monaco). Виняток прибрано, як і вимагає ratchet.
        ["Calculation.EditConstant"] = "константи розрахунків e2e не змінює",
        ["Calculation.EditRule"] = "правила розрахунків e2e не змінює",
        ["Calculation.ManageRequiredInputs"] = "обов'язкові входи e2e не налаштовує",
        ["Report.Export"] = "експорт звітів e2e не перевіряє",
        ["Report.ViewSnapshot"] = "перегляд знімків звітів e2e не перевіряє (BuildSnapshot є)",
        ["Report.EditDefinition"] = "небезпечне (IsDangerous); видається явно, e2e не змінює визначення звітів",
        ["Integration.EditSchedule"] = "розклади інтеграцій e2e не редагує",
        ["Uom.EditCatalog"] = "каталог одиниць e2e не редагує",
        ["System.RunJob"] = "небезпечне (IsDangerous); e2e не запускає фонові задачі вручну",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_право_каталогу_є_в_E2EAdmin_або_у_винятках()
    {
        var rights = E2eAdminRights(StandText());
        Assert.True(
            rights.Codes.Count >= 20,
            $"{StandFile}: з блоку прав E2EAdmin розібрано лише {rights.Codes.Count} - формат списку змінився, а сторож осліп.");

        var missing = CatalogPermissions()
            .Where(code => !Covered(rights, code) && !NotGrantedToE2e.ContainsKey(code))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Права з {SeedFile}, яких немає в ролі E2EAdmin ({StandFile}:{rights.BlockLine}) і у винятках:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                missing.Select(code =>
                    $"  {code}: додай у permissionCodes ролі E2EAdmin ({StandFile}:{rights.BlockLine}) - інакше "
                    + "e2e-admin отримає 403 лише на PR - або впиши в NotGrantedToE2e з причиною.")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_винятків_не_застарів()
    {
        var rights = E2eAdminRights(StandText());
        var catalog = CatalogPermissions();

        var stale = NotGrantedToE2e.Keys
            .Where(code => Covered(rights, code) || !catalog.Contains(code))
            .Order(StringComparer.Ordinal)
            .Select(code => Covered(rights, code)
                ? $"  {code}: уже є в E2EAdmin ({StandFile}:{rights.BlockLine}) - прибери з NotGrantedToE2e."
                : $"  {code}: його немає в {SeedFile} - прибери з NotGrantedToE2e.")
            .ToList();

        Assert.True(
            stale.Count == 0,
            "NotGrantedToE2e застарів:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// Самоперевірка парсера: без неї парсер, що перестав різати коментарі, зарахував би згадку права
    /// в коментарі, а той, що не знайшов блок, мовчки зеленів би.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Парсер_бере_права_ролі_пропускає_коментарі_і_падає_голосно()
    {
        const string withPermit = """
            Call POST '/api/v1/roles' @{
                code            = 'E2EOperator'
                permissionCodes = @('Document.View', 'Operator.Only')
            } | Out-Null
            Call POST '/api/v1/roles' @{
                code            = 'E2EAdmin'
                permissionCodes = @(
                    'Template.View',
                    # 'Comment.Line' - у коментарі
                    'Registry.View', 'Permit.EditType', 'Permit.PublishType', # 'Tail.Comment'
                    <# 'Block.Comment' #>
                    'Report.%')
            } | Out-Null
            """;

        var rights = E2eAdminRights(withPermit);
        Assert.Equal(
            ["Template.View", "Registry.View", "Permit.EditType", "Permit.PublishType", "Report.%"],
            rights.Codes);
        Assert.Equal(7, rights.BlockLine);
        Assert.True(Covered(rights, "Permit.EditType"));
        Assert.True(Covered(rights, "Report.ViewCampaign"), "шаблон Report.% має покривати родину");
        Assert.False(Covered(rights, "Comment.Line"));
        Assert.False(Covered(rights, "Block.Comment"));
        Assert.False(Covered(rights, "Operator.Only"), "права іншої ролі не рахуються");

        // Без Permit.* сторож мав би впасти: право каталогу не покрите.
        var withoutPermit = withPermit.Replace("'Permit.EditType', 'Permit.PublishType', ", string.Empty, StringComparison.Ordinal);
        var rightsWithout = E2eAdminRights(withoutPermit);
        Assert.False(Covered(rightsWithout, "Permit.EditType"));
        Assert.False(Covered(rightsWithout, "Permit.PublishType"));

        // Зникле з тексту: блок не знайдено - виняток, а не порожній зелений список.
        var noRole = Assert.Throws<InvalidOperationException>(() => E2eAdminRights("Call POST '/api/v1/roles' @{ code = 'Other' }"));
        Assert.Contains("не знайдено блок прав e2e-admin", noRole.Message, StringComparison.Ordinal);
        var noList = Assert.Throws<InvalidOperationException>(() => E2eAdminRights("code = 'E2EAdmin'\nprmissions = @('A.B')"));
        Assert.Contains("не знайдено блок прав e2e-admin", noList.Message, StringComparison.Ordinal);
        var unclosed = Assert.Throws<InvalidOperationException>(() => E2eAdminRights("code = 'E2EAdmin'\npermissionCodes = @('A.B'"));
        Assert.Contains("не знайдено блок прав e2e-admin", unclosed.Message, StringComparison.Ordinal);

        // Живий файл: каталог і стенд розібрано не порожніми.
        Assert.True(CatalogPermissions().Count >= 40, $"У {SeedFile} розібрано підозріло мало прав.");
        Assert.Contains("Permit.EditType", E2eAdminRights(StandText()).Codes);
    }

    private static string StandText() =>
        File.ReadAllText(Path.Combine(SourceTree.Root, StandFile.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Covered(E2eRights rights, string code) =>
        rights.Codes.Any(granted =>
            granted.Contains('%', StringComparison.Ordinal)
                ? Regex.IsMatch(code, "^" + Regex.Escape(granted).Replace("%", ".*", StringComparison.Ordinal) + "$")
                : string.Equals(granted, code, StringComparison.Ordinal));

    private static HashSet<string> CatalogPermissions()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var start = text.IndexOf("MERGE sec.Permission AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sec.Permission AS t - сторож дивиться не туди.");
        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sec.Permission AS t у {SeedFile} не закінчується.");

        var block = string.Join(
            '\n',
            text[start..end].Split('\n').Select(line =>
                line.IndexOf("--", StringComparison.Ordinal) is var dash and >= 0 ? line[..dash] : line));

        return PermissionRow().Matches(block)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Права ролі E2EAdmin з тексту ps1 і рядок початку блоку (1-based).</summary>
    internal sealed record E2eRights(List<string> Codes, int BlockLine);

    /// <summary>
    /// Рядкові літерали масиву <c>permissionCodes = @( ... )</c> ролі <c>code = 'E2EAdmin'</c>, без коментарів
    /// (<c>#</c> до кінця рядка, <c>&lt;# ... #&gt;</c>). Не знайшов роль, масив чи його кінець -
    /// <see cref="InvalidOperationException"/> (щоб зміна формату не позеленила сторожа).
    /// </summary>
    internal static E2eRights E2eAdminRights(string ps1)
    {
        const string NotFound = "не знайдено блок прав e2e-admin (роль E2EAdmin / permissionCodes = @(...)) у tools/e2e-stand.ps1 - "
            + "формат списку змінився, онови парсер у E2eStandPermissionsCoverageTests.";

        var role = RoleAnchor().Match(ps1);
        if (!role.Success)
        {
            throw new InvalidOperationException(NotFound);
        }

        var list = ListAnchor().Match(ps1, role.Index);
        if (!list.Success)
        {
            throw new InvalidOperationException(NotFound);
        }

        var blockLine = 1 + ps1.Take(list.Index).Count(ch => ch == '\n');
        var codes = new List<string>();
        var depth = 1;
        var i = list.Index + list.Length;
        while (i < ps1.Length && depth > 0)
        {
            var c = ps1[i];
            if (c == '#')
            {
                var eol = ps1.IndexOf('\n', i);
                i = eol < 0 ? ps1.Length : eol;
            }
            else if (c == '<' && i + 1 < ps1.Length && ps1[i + 1] == '#')
            {
                var close = ps1.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = close < 0 ? ps1.Length : close + 2;
            }
            else if (c == '\'')
            {
                var literal = new StringBuilder();
                i++;
                while (i < ps1.Length)
                {
                    if (ps1[i] == '\'' && i + 1 < ps1.Length && ps1[i + 1] == '\'')
                    {
                        literal.Append('\'');
                        i += 2;
                    }
                    else if (ps1[i] == '\'')
                    {
                        break;
                    }
                    else
                    {
                        literal.Append(ps1[i++]);
                    }
                }

                i++;
                codes.Add(literal.ToString());
            }
            else
            {
                depth += c == '(' ? 1 : c == ')' ? -1 : 0;
                i++;
            }
        }

        if (depth != 0)
        {
            throw new InvalidOperationException(NotFound);
        }

        return new E2eRights(codes, blockLine);
    }

    [GeneratedRegex(@"\bcode\s*=\s*'E2EAdmin'")]
    private static partial Regex RoleAnchor();

    [GeneratedRegex(@"\bpermissionCodes\s*=\s*@\(")]
    private static partial Regex ListAnchor();

    [GeneratedRegex(@"\(\s*N'([A-Za-z]+\.[A-Za-z]+)'\s*,\s*N'[A-Za-z]+'\s*,\s*[01]\s*\)")]
    private static partial Regex PermissionRow();
}
