using Ecr.Infrastructure.Persistence;

namespace Ecr.TestKit;

/// <summary>
/// Текст сіду з дерева репозиторію — РІВНО в тому порядку, у якому його виконує
/// <see cref="SeedRunner"/>: <c>seed/00-prelude.sql</c> → <c>seed/features/*.sql</c>
/// (ordinal за іменем) → <c>seed/99-epilogue.sql</c>.
/// </summary>
/// <remarks>
/// ⛔ Єдине місце, звідки тести й сторожі читають сід. До поділу кожен із них
/// відкривав <c>09-seed.sql</c> сам; тепер склад частин знає лише
/// <see cref="SeedScript"/>, і тест не може «забути» файл фічі.
/// ⚠ Блоки, які сторожі шукають першим входженням (<c>MERGE sys_ecr.UiString AS t</c> —
/// en-каталог, <c>MERGE sec.Permission AS t</c>), лежать у пролозі, тож перше
/// входження в зведеному тексті — те саме, що й у колишньому файлі.
/// </remarks>
public static class SeedSql
{
    /// <summary>Каталог частин відносно кореня репозиторію — для повідомлень сторожів.</summary>
    public const string DisplayPath = "src/Ecr.Infrastructure/Persistence/Sql/seed/";

    private static readonly Lazy<string> Composed = new(() => SeedScript.ReadDirectory(SeedDirectory()));

    /// <summary>Зведений текст сіду (кешується на прогін).</summary>
    public static string Text => Composed.Value;

    /// <summary>Абсолютний шлях до <c>Persistence/Sql/seed</c> у дереві.</summary>
    public static string SeedDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ecr.Infrastructure", "Persistence", "Sql", "seed");
            if (File.Exists(Path.Combine(candidate, SeedScript.PreludeFile)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Не знайдено {DisplayPath} угору від каталогу збірки.");
    }
}
