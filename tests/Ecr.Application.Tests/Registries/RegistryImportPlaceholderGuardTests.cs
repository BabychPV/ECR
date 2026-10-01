using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// D1: помилка рядка імпорту несе <c>params</c> для кожного плейсхолдера <c>{…}</c> свого тексту
/// (інакше клієнт друкує сирий шаблон). Ключі з плейсхолдерами, які видає сам імпорт, і імена, що
/// їх код передає, — у таблиці нижче; новий плейсхолдер у тексті без правки коду червоніє тут.
/// </summary>
/// <remarks>
/// Ключі, що йдуть із винятків домену, беруть <c>params</c> із <c>Details</c> винятку
/// (<c>RegistryEntryWriter.ParamsOf</c>), а імена деталей — ті самі, що в тексті каталогу.
/// </remarks>
public sealed class RegistryImportPlaceholderGuardTests
{
    public static TheoryData<string, string> Direct => new()
    {
        { "err.ECR-REG-0422.valueNotNumber", "dataType,value" },
        { "err.ECR-REG-0422.fieldTypeNotAllowed", "dataType" },
        { "err.ECR-CFG-0422.invalidCode", "code" },
        { "err.ECR-REG-0409.entryCodeTaken", "code,id" },
        { "err.ECR-REG-0404.registryEntry", "entryId" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [MemberData(nameof(Direct))]
    public void Плейсхолдери_тексту_збігаються_з_params_імпорту(string key, string provided)
    {
        var text = EnglishText(key);
        var placeholders = Regex.Matches(text, @"\{(\w+)\}").Select(m => m.Groups[1].Value)
            .Distinct().OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(provided, string.Join(',', placeholders));
    }

    private static string EnglishText(string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var seed = File.ReadAllText(Path.Combine(
            dir.FullName, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));
        var match = Regex.Match(seed, $@"N'{Regex.Escape(key)}',\s*N'en',\s*N'((?:[^']|'')*)'");
        Assert.True(match.Success, $"У сіді немає en-тексту ключа {key}.");
        return match.Groups[1].Value;
    }
}
