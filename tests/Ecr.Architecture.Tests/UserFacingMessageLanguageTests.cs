using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ 25.11 (людина: «не забувай про мову — чому помилки українською»): текст, який сервер віддає людині в полі
/// повідомлення DTO, — ключем каталогу мовою читача, а не українським літералом.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Банер сітки «required column(s) missing» показував «Колонка «Land_TypeFuel» обов'язкова для
/// методології …» в англійському інтерфейсі (<c>PatchCellsHandler.RequiredInputMessage</c>), а діалог підтвердження
/// правки поза вікном — «Місяць 03.2026 поза вікном дії …» (<c>PeriodAccessRules</c> → <c>CellConfirmations</c>).
/// Сторож ловить обидва класи там, де текст народжується: (1) конструктор <c>ValidationMessage</c>/
/// <c>ValidationMessageDto</c> з кириличним літералом — текст лише з шаблону за ключем (<c>MessageKey</c> поруч із
/// літералом не рятує: читач бачить саме <c>Message</c>); (2) <c>new PeriodRuleOutcome(…)</c> і
/// <c>AllowWithConfirmation(…)</c> з кириличним літералом (пояснення — <c>DetailKey</c> + <c>DetailIn</c>).
/// <para>
/// ⚠ Межа: текст, складений у змінну заздалегідь і переданий у конструктор, сторож не бачить; журнали й внутрішні
/// винятки — поза предметом (людині їх не показують, а винятки з ключем має свій сторож
/// <c>MessageKeyRatchetTests</c>).
/// </para>
/// </remarks>
public sealed partial class UserFacingMessageLanguageTests
{
    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*[\\u0400-\\u04FF](?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex CyrillicLiteral();

    [GeneratedRegex(@"new\s+(?:Validation\.)?ValidationMessage(?:Dto)?\s*\(")]
    private static partial Regex ValidationMessageCtor();

    [GeneratedRegex(@"new\s+PeriodRuleOutcome\s*\(|AllowWithConfirmation\s*\(")]
    private static partial Regex AccessHintSite();

    /// <remarks>
    /// Мутація: повернути в <c>RequiredInputCollector.Messages</c> колишнє
    /// <c>$"Колонка «{columnCode}» обов'язкова для методології …"</c> прямо в конструктор без <c>MessageKey:</c> —
    /// тест червоний і називає файл і рядок.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повідомлення_валідації_для_людини_не_народжуються_українським_літералом()
    {
        var files = SourceTree.Production("Ecr.Application", "Ecr.Infrastructure", "Ecr.Api", "Ecr.Domain");

        // ⛔ Порожній перелік файлів дав би зелене без жодної перевірки.
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        var sites = 0;
        foreach (var file in files)
        {
            var text = StripLineComments(file.Text);
            foreach (Match site in ValidationMessageCtor().Matches(text))
            {
                sites++;
                var args = Arguments(text, site.Index + site.Length - 1);
                if (CyrillicLiteral().IsMatch(args))
                {
                    offenders.Add($"{file.Path}:{LineOf(text, site.Index)}");
                }
            }

            foreach (Match site in AccessHintSite().Matches(text))
            {
                sites++;
                var args = Arguments(text, site.Index + site.Length - 1);
                if (CyrillicLiteral().IsMatch(args))
                {
                    offenders.Add($"{file.Path}:{LineOf(text, site.Index)}");
                }
            }
        }

        // ⛔ Регулярка, що перестала збігатися, теж дала б зелене.
        Assert.True(sites > 0, "Не знайдено жодного конструктора ValidationMessage — сторож зламався.");

        Assert.True(
            offenders.Count == 0,
            "Текст для людини народжується українським літералом без ключа каталогу (✎ 25.11). Заведи ключ у "
            + "ValidationMessageTemplates + сід en/ru/kz і передай MessageKey/Params (або DetailKey):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Текст аргументів від відкривної дужки до парної закривної (рядкові літерали пропускаються).</summary>
    private static string Arguments(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    /// <summary>Прибирає <c>//</c>-коментарі (рядок <c>///</c> теж), не чіпаючи <c>//</c> усередині літерала.</summary>
    private static string StripLineComments(string text)
        => string.Join('\n', text.Split('\n').Select(line =>
        {
            var inString = false;
            for (var i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString && line[i] == '/' && line[i + 1] == '/')
                {
                    return line[..i];
                }
            }

            return line;
        }));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
