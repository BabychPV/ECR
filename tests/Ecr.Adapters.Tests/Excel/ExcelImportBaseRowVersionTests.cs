// tests/Ecr.Adapters.Tests/Excel/ExcelImportBaseRowVersionTests.cs
using Ecr.Adapters.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// L6-08 (аудит 2026-10-03): застосування імпорту ніколи не шле рядок із
/// <c>baseVersion = null</c>, тобто ніколи не просить СТВОРИТИ рядок.
/// </summary>
/// <remarks>
/// ⛔ Що було: рядок книги, якого не стало між експортом і переглядом, ішов без версії —
/// для <c>PatchCellsHandler</c> це намір створити рядок (R-B2). Імпорт мовчки відтворював
/// видалений рядок, а в таблиці зі стелею створення брало виняткове блокування аркуша
/// поверх спільного, уже взятого імпортом, — прихований дедлок двох імпортів.
/// </remarks>
public sealed class ExcelImportBaseRowVersionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Finding", "L6-08")]
    public void Рядок_без_версії_в_перегляді_йде_нульовою_версією_а_не_null()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal) { ["R1"] = "AAAAAAAAB9E=" };

        Assert.Equal("AAAAAAAAB9E=", ExcelImporter.BaseRowVersion(versions, "R1"));
        Assert.Equal("AAAAAAAAAAA=", ExcelImporter.BaseRowVersion(versions, "GONE"));
        Assert.Equal("AAAAAAAAAAA=", ExcelImporter.MissingRowVersion);
    }
}
