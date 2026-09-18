using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Відмова, зібрана в контролері АНОНІМНИМ об'єктом, минає
/// <c>ExceptionHandlingMiddleware</c> — а разом із ним і весь механізм
/// локалізації. Таких місць не стає більше.
/// </summary>
/// <remarks>
/// ⛔ Предмет, знайдений проходом інтерфейсу (<c>docs/build/UI-WALKTHROUGH.md</c>,
/// F4). <c>/documents/999999999</c> показує заголовок «HTTP 404» і під ним те
/// саме «HTTP 404» — при тому, що в каталозі ВЖЕ Є речення
/// <c>err.ECR-DOC-0404.document</c> = «Document {documentId} was not found.», а
/// <c>err.ECR-DOC-0404</c> = «Document not found» править за заголовок.
///
/// Ланцюг такий. <c>DocumentsController.Get</c> повертає
/// <c>NotFound(new { errorCode = "ECR-DOC-0404" })</c> — звичайний JSON, а не
/// <c>application/problem+json</c>. Виняток не кидається, тому
/// <c>ExceptionHandlingMiddleware</c> цієї відповіді не бачить ЖОДНИМ боком:
/// ні <c>LocalizedTitleAsync</c> (<c>Title</c> за ключем <c>err.&lt;код&gt;</c>),
/// ні <c>LocalizedDetailAsync</c> (подробиця за <c>Details["messageKey"]</c>).
/// У тілі немає ні <c>title</c>, ні <c>detail</c> — і клієнт підставляє свій
/// запасний варіант (<c>src/Ecr.Web/src/api/client.ts</c>, <c>problemOf</c>:
/// <c>title: `HTTP ${response.status}`</c>), який <c>ErrorAlert</c> малює
/// заголовком, а <c>EcrApiError</c> (<c>detail ?? title</c>) — ще й текстом.
/// Звідси «HTTP 404» двічі. Код при цьому доїжджає, і саме тому дефект
/// виглядає як «майже працює».
///
/// Виправлення — кинути <c>NotFoundException</c> з ключем у <c>Details</c>,
/// як це вже робить, наприклад, <c>DocumentStore</c>:
/// <code>
/// throw new NotFoundException(
///     "ECR-DOC-0404",
///     $"Документ {id} не знайдено.",
///     new Dictionary&lt;string, object?&gt;(StringComparer.Ordinal)
///     {
///         ["messageKey"] = "err.ECR-DOC-0404.document",
///         ["documentId"] = id.ToString(CultureInfo.InvariantCulture),
///     });
/// </code>
///
/// ⚠ Друга родина в переліку — <c>BadRequest(new { error = "…" })</c> з готовим
/// УКРАЇНСЬКИМ реченням і БЕЗ коду помилки взагалі (F1). Там клієнт показує
/// «HTTP 400» і не має чого розрізняти: код — єдине, за чим йому дозволено
/// розрізняти причини (<c>02-contracts.md</c> §7).
///
/// ⚠ Чому перелік-замір, а не гола заборона. Виправлення п'яти наявних місць —
/// це правка контролерів, тобто окрема робота з власним прогоном; сторож
/// зупиняє РІСТ уже сьогодні. Перевірка йде в ОБИДВА боки (той самий прийом,
/// що <c>MessageKeyRatchetTests</c>): число, яке стало МЕНШИМ, теж червоне —
/// інакше перелік тихо розійшовся б із дійсністю і перестав бути заміром.
/// </remarks>
public sealed partial class ControllerErrorContractTests
{
    /// <summary>Каталог контролерів відносно кореня репозиторію.</summary>
    private const string ControllersPath = "src/Ecr.Api/Controllers/";

    /// <summary>
    /// Зразок, на якому перевіряється, що регулярка ще жива.
    /// </summary>
    /// <remarks>
    /// ⛔ Замість переліку-заміру, який тут стояв спершу. Замір на 2026-09-18
    /// був <c>CellsController</c> = 1, <c>DocumentsController</c> = 3,
    /// <c>SecurityController</c> = 1 — і всі п'ять виправлено тим самим PR, що
    /// завів цього сторожа. Порожній перелік-замір гірший за пряму заборону:
    /// його <c>Assert.NotEmpty</c> вимагав би тримати борг живим.
    ///
    /// ⚠ Але сама заборона має ту ваду, що й будь-яка «перевірка, яка нічого
    /// не знаходить»: регулярка, що перестала збігатися (контролери переїхали,
    /// форма виклику змінилася), дає порожній перелік і ЗЕЛЕНЕ. Тому нижче —
    /// самоперевірка на зразку, дослівно взятому з коду до виправлення.
    /// </remarks>
    private const string SampleBeforeFix = @"return NotFound(new { errorCode = ""ECR-DOC-0404"" });";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9a")]
    public void Відмов_повз_problem_json_у_контролерах_немає()
    {
        var actual = Bare();

        // ⛔ Самоперевірка ПЕРШОЮ: без неї регулярка, що перестала збігатися,
        // дала б порожній перелік — і тест зеленів би саме тоді, коли зламався.
        Assert.True(
            BareResult().IsMatch(SampleBeforeFix),
            "Регулярка більше не бачить власного зразка — тобто перевірка мертва, "
            + "а не система чиста. Полагодь BareResult() або онови SampleBeforeFix.");

        var failures = actual
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p =>
                $"{p.Key}: відмови, зібрані анонімним об'єктом, у рядках "
                + $"{string.Join(", ", p.Value)}. Кидай виняток (NotFoundException / "
                + "BusinessRuleException) із Details[\"messageKey\"] замість IActionResult "
                + "із new { … }: інакше відповідь минає ExceptionHandlingMiddleware, "
                + "у тілі немає ні title, ні detail, і користувач бачить "
                + "«HTTP <статус>» замість речення з каталогу.")
            .ToList();

        // ⛔ `Assert.True` з готовим текстом, а не `Assert.Empty(failures)`:
        // xUnit друкує колекцію обрізаною, і зникає першою саме та частина, де
        // сказано, що робити.
        Assert.True(
            failures.Count == 0,
            "Відмови повз problem+json:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>Відмови, зібрані анонімним об'єктом: шлях файлу → номери рядків.</summary>
    private static Dictionary<string, List<int>> Bare()
    {
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in SourceTree.Production("Ecr.Api"))
        {
            if (!file.Path.StartsWith(ControllersPath, StringComparison.Ordinal))
            {
                continue;
            }

            // ⛔ `CodeLines()`, а не `file.Text`: сторож, який не відрізняє коду
            // від коментаря, червоніє на ПОЯСНЕННІ до власного виправлення.
            // Саме це й сталося того самого дня: у `DocumentsController.Get` і
            // `CellsController.Patch` дефект виправили, а рядок «Кидок, а не
            // `NotFound(new { errorCode })`» у коментарі лишив сторожа
            // червоним — тобто перевірка вимагала не називати те, від чого
            // лікує. Помилка була в безпечний бік (хибно-червоний, не
            // хибно-зелений), але вона робить сторожа неправдивим.
            //
            // ⚠ Порядок рядків збережено: `CodeLines()` іде файлом згори вниз,
            // а `Ledger` вирівнює саме за порядком (`lines.Skip(allowed)`).
            foreach (var (line, text) in file.CodeLines())
            {
                if (!BareResult().IsMatch(text))
                {
                    continue;
                }

                if (!found.TryGetValue(file.Path, out var lines))
                {
                    found[file.Path] = lines = [];
                }

                lines.Add(line);
            }
        }

        return found;
    }

    /// <summary>
    /// Помилковий <c>IActionResult</c> з анонімним об'єктом у тілі.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелічені саме ПОМИЛКОВІ відповіді. <c>Ok(new { … })</c> сюди не
    /// входить: анонімне тіло успіху — інша вада (у нього немає імені в схемі
    /// OpenAPI, `A7-16`), і за нею свій сторож.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:NotFound|BadRequest|Conflict|UnprocessableEntity|Unauthorized|Forbid|StatusCode)"
        + @"\s*\(\s*(?:\d+\s*,\s*)?new\s*\{")]
    private static partial Regex BareResult();
}
