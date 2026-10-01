using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Жоден SQL-/PowerShell-скрипт розгортання не містить NUL чи іншого керуючого
/// байта (крім табуляції й кінців рядків) і не є UTF-16.
/// </summary>
/// <remarks>
/// ⛔ Уже траплялося (ФВ-2.16, <c>11-audit-tables.sql</c>): NUL у коментарі перед
/// <c>ALTER TABLE … ADD IsOutOfWindow</c>. <c>sqlcmd -b -I -i</c> виконував файл з
/// кодом 0, але ALTER тихо не виконувався, тож на оновлюваній базі колонки не
/// було, а на новій (CREATE TABLE) усе працювало. Причина — PowerShell:
/// <c>`0</c> у подвійних лапках дає NUL, <c>`a</c> — BEL (так само зіпсовано
/// коментар у <c>12-archive-tables.sql</c>). Читання тут — БАЙТАМИ: текстові
/// сторожі й ripgrep такі файли пропускають або не бачать.
/// </remarks>
public sealed class ScriptControlBytesTests
{
    private static readonly string[] Extensions =
        [".sql", ".ps1", ".psm1", ".sh", ".cmd", ".bat", ".wxs", ".wxi"];

    private static readonly string[] Roots = ["src", "tools", "installer", "deploy"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипти_розгортання_не_містять_NUL_і_керуючих_байтів()
    {
        var root = SourceTree.Root;
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var top in Roots)
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/node_modules/", StringComparison.Ordinal)
                    || !Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;
                offenders.AddRange(Find(File.ReadAllBytes(path)).Select(f => $"{relative}: {f}"));
            }
        }

        Assert.True(scanned >= 20, $"знайдено лише {scanned} скриптів — сторож дивиться не туди");
        Assert.True(
            offenders.Count == 0,
            "Скрипти з керуючими байтами (sqlcmd їх тихо ковтає разом із наступною командою):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Мутаційний доказ: детектор червоніє на NUL, BEL і UTF-16, і мовчить на чистому тексті.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Детектор_керуючих_байтів_ловить_NUL_BEL_і_UTF16()
    {
        var clean = System.Text.Encoding.UTF8.GetBytes("-- лишаються 0;\r\n\tSELECT 1;\n");
        Assert.Empty(Find(clean));

        var withNul = (byte[])clean.Clone();
        withNul[clean.Length / 2] = 0x00;
        Assert.Single(Find(withNul));

        var withBel = (byte[])clean.Clone();
        withBel[3] = 0x07;
        Assert.Single(Find(withBel));

        Assert.NotEmpty(Find([0xFF, 0xFE, 0x53, 0x00]));
    }

    private static IEnumerable<string> Find(byte[] bytes)
    {
        if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            yield return "UTF-16 BOM";
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0x20 && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
            {
                yield return $"байт 0x{b:X2} на позиції {i}";
            }
        }
    }
}
