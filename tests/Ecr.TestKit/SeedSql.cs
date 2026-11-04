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
/// ⚠ Блоки, які сторожі шукають першим входженням (<c>MERGE sec.Permission AS t</c>),
/// лежать у пролозі, тож перше входження в зведеному тексті — те саме, що й у колишньому файлі.
/// ⛔ en-каталог — НЕ один блок <c>MERGE sys_ecr.UiString AS t</c>: з lane integ-seed-uien його рядки
/// лежать порціями <c>INSERT INTO #UiEn … OPTION (RECOMPILE);</c> у <c>seed/features/*.sql</c>
/// (MERGE з <c>#UiEn</c> — в епілозі). Сторожі беруть рядки каталогу лише через
/// <see cref="EnCatalog"/> / <see cref="EnCatalogOf"/>, а не шукають MERGE самі.
/// </remarks>
public static class SeedSql
{
    /// <summary>Каталог частин відносно кореня репозиторію — для повідомлень сторожів.</summary>
    public const string DisplayPath = "src/Ecr.Infrastructure/Persistence/Sql/seed/";

    /// <summary>Початок порції en-каталогу у файлі фічі.</summary>
    /// <remarks>⚠ З дужкою: інакше збіглося б і з <c>INSERT INTO #UiEnCounts</c> прологу.</remarks>
    public const string EnCatalogInsert = "INSERT INTO #UiEn ([Key]";

    /// <summary>Кінець порції (і порцій <c>#I18N</c>).</summary>
    public const string PortionEnd = "OPTION (RECOMPILE)";

    private static readonly Lazy<string> Composed = new(() => SeedScript.ReadDirectory(SeedDirectory()));

    private static readonly Lazy<string> ComposedEnCatalog = new(() => EnCatalogOf(Text));

    /// <summary>Зведений текст сіду (кешується на прогін).</summary>
    public static string Text => Composed.Value;

    /// <summary>
    /// Рядки en-каталогу: усі порції <c>INSERT INTO #UiEn</c> зведеного сіду (від початку порції до
    /// <c>OPTION (RECOMPILE)</c>), склеєні в порядку виконання. Рядок має форму
    /// <c>(N'ключ', N'en', N'текст', область)</c> — та сама, що й у колишньому VALUES en-MERGE.
    /// </summary>
    public static string EnCatalog => ComposedEnCatalog.Value;

    /// <summary>Те саме для довільного тексту сіду (сторожі з підкладеним рядком).</summary>
    public static string EnCatalogOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while ((pos = text.IndexOf(EnCatalogInsert, pos, StringComparison.Ordinal)) >= 0)
        {
            var stop = text.IndexOf(PortionEnd, pos, StringComparison.Ordinal);
            if (stop < 0)
            {
                throw new InvalidOperationException($"Порція {EnCatalogInsert} у {DisplayPath} не закінчується {PortionEnd}.");
            }

            sb.Append(text, pos, stop - pos).Append('\n');
            pos = stop;
        }

        return sb.ToString();
    }

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
