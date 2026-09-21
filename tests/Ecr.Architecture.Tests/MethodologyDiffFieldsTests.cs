// tests/Ecr.Architecture.Tests/MethodologyDiffFieldsTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Поля порівняння версій методології (<c>changedFields</c>) — лише з
/// <see cref="MethodologyDiffFields"/>, і перелік дорівнює тому, що віддає обробник.
/// </summary>
/// <remarks>
/// ⚠ Сторож по тексту джерела: поле, додане сирим рядком, проявилось би лише в
/// тесті, що змінює саме його, — а клієнт показав би для нього сирий ключ.
/// </remarks>
public sealed class MethodologyDiffFieldsTests
{
    private const string Handler = "CompareMethodologyVersionsHandler.cs";

    /// <summary>Кортеж, що починається сирим рядком: <c>("field", a.X != b.X)</c>.</summary>
    private static readonly Regex RawField = new(
        @"\(\s*""(?<field>[^""]*)""\s*,", RegexOptions.CultureInvariant);

    private static readonly Regex FieldReference = new(
        @"\bMethodologyDiffFields\.(?<name>[A-Za-z]+)\b", RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "BE-25")]
    public void Значення_на_дроті_ті_самі_що_їх_уже_віддавав_обробник()
    {
        // ⛔ Літералами: перейменування константи змінило б JSON мовчки.
        Assert.Equal(
            [
                "argumentsCsv", "expectedJson", "expression", "inputJson", "kind", "outputUnitId",
                "resultType", "source", "textValue", "tolerance", "unitId", "validTo", "value",
            ],
            MethodologyDiffFields.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "BE-25")]
    public void Обробник_віддає_рівно_поля_з_MethodologyDiffFields_і_жодного_сирим_рядком()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, "src", "Ecr.Application", "Calculations", Handler));

        Assert.Empty(RawField.Matches(text).Select(m => m.Groups["field"].Value));

        var used = FieldReference.Matches(text)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Константа, якої обробник не віддає, — мертвий ключ каталогу в клієнті.
        var declared = typeof(MethodologyDiffFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(declared, used.Order(StringComparer.Ordinal));
    }
}
