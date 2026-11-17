// src/Ecr.Application/Calculations/Scripting/ScriptTextLocator.cs
using System.Text.RegularExpressions;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>
/// Додаткові коди діагностик, яких компілятор не створює (перевірки оточення й публікації, MS 13.9).
/// </summary>
/// <remarks>
/// ⚠ Тут, а не в <c>Ecr.Scripting.ScriptDiagnosticCodes</c>: проєкт скриптів не чіпається поза своїм власником.
/// Усі коди разом із наявними — ключі каталогу <c>script.&lt;code&gt;</c>.
/// </remarks>
public static class ScriptEnvironmentCodes
{
    /// <summary>Скрипт читає колонку-результат власного виходу (§13.9).</summary>
    public const string ReadsOwnOutput = "readsOwnOutput";

    /// <summary>Цикл між методологіями через колонки-результати (§13.9).</summary>
    public const string ResultCycle = "resultCycle";

    /// <summary>Код виходу не є кодом сутності (латиниця, цифри, «_», до 64 символів, перша — літера).</summary>
    public const string InvalidOutputCode = "invalidOutputCode";
}

/// <summary>Пошук місця згадки в тексті скрипта для діагностик, у яких компілятор не дав позиції.</summary>
internal static partial class ScriptTextLocator
{
    /// <summary>Позиція за зсувом у тексті.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="index">Зсув.</param>
    /// <param name="length">Довжина.</param>
    public static ScriptSpan At(string text, int index, int length)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new ScriptSpan(index, Math.Max(1, length), line, index - lineStart + 1);
    }

    /// <summary>Перше входження рядкового літерала <c>"value"</c> або <c>'value'</c>; не знайдено — початок тексту.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="value">Значення літерала.</param>
    public static ScriptSpan FindLiteral(string text, string value)
    {
        foreach (var quote in new[] { "\"", "'" })
        {
            var index = text.IndexOf(quote + value + quote, StringComparison.Ordinal);
            if (index >= 0)
            {
                return At(text, index, value.Length + 2);
            }
        }

        return At(text, 0, 1);
    }

    /// <summary>Місце оголошення виходу <c>output Code</c>; не знайдено — початок тексту.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="code">Код виходу.</param>
    public static ScriptSpan FindOutput(string text, string code)
    {
        var match = Regex.Match(
            text, @"\boutput\s+(" + Regex.Escape(code) + @")\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? At(text, match.Groups[1].Index, code.Length) : At(text, 0, 1);
    }
}
