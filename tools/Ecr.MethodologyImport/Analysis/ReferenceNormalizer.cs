using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Analysis;

/// <summary>
/// Правило нормалізації посилання: токен у вигляді, як він записаний в AF (<c>!Ім'я</c>, <c>CST.Ім'я</c>),
/// замінюється на токен, який існує в ECR. <see cref="Reason"/> — факт із джерела, не здогад.
/// </summary>
public sealed record NormalizationRule(string Id, string From, string To, string Reason);

/// <summary>Скільки разів правило спрацювало: у списках аргументів і в текстах формул.</summary>
public sealed record NormalizationApplied(string Id, string From, string To, int InArguments, int InTexts);

/// <summary>
/// Декларативна таблиця відповідностей посилань AF → ECR (кожен рядок — факт, а не здогад) і застосування її
/// до моделі ДО резолвінгу. Правила — точний збіг імені (без префіксів і регістру): інші <c>!</c>-імена не чіпаються.
/// <list type="bullet">
/// <item><c>!Total</c>, <c>!Duration</c>, <c>!FlareUnitMode</c> — не формули, а параметри, які CLR подає з події
/// факела (<c>R_Air_HSE30X_Cont_General.cs:116–139</c>); у V2 формули записані як <c>@Total</c> тощо.</item>
/// <item><c>!Common_WtCi_DimethilSulfide</c> → формула <c>Common_WtCi_C2H6S</c>.</item>
/// <item><c>CST.Mh2s_ECW_C11_11_02_</c> → <c>CST.MMh2s_ECW_C11_11_02_</c> (значення 34);
/// <c>CST.k3_ECW_C11_11_05_</c> → <c>CST.k3_ECW_C11_11_05</c> (3600).</item>
/// </list>
/// Значення констант (34, 3600) у таблиці не дублюються: вони вже є в моделі як <see cref="ConstantDef"/>.
/// </summary>
public static class ReferenceNormalizer
{
    private const string FlareEvent = "параметр події факела, який CLR подає у формулу (R_Air_HSE30X_Cont_General.cs:116–139); у V2 — @";

    /// <summary>Таблиця відповідностей. Порядок значущий лише для звіту; правила не перетинаються за <c>From</c>.</summary>
    public static IReadOnlyList<NormalizationRule> Rules { get; } =
    [
        new("flare-total", "!Total", "@Total", FlareEvent),
        new("flare-duration", "!Duration", "@Duration", FlareEvent),
        new("flare-unit-mode", "!FlareUnitMode", "@FlareUnitMode", FlareEvent),
        new("formula-dimethylsulfide", "!Common_WtCi_DimethilSulfide", "!Common_WtCi_C2H6S",
            "формула Common з назвою за формулою речовини (C2H6S)"),
        new("const-mh2s-c11-11-02", "CST.Mh2s_ECW_C11_11_02_", "CST.MMh2s_ECW_C11_11_02_",
            "константа ECW_C11_11_02 названа MMh2s_…, значення 34"),
        new("const-k3-c11-11-05", "CST.k3_ECW_C11_11_05_", "CST.k3_ECW_C11_11_05",
            "константа ECW_C11_11_05 названа без кінцевого «_», значення 3600"),
    ];

    public static (MethodologyModel Model, IReadOnlyList<NormalizationApplied> Applied) Normalize(
        MethodologyModel model, IReadOnlyList<NormalizationRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        rules ??= Rules;
        var byFrom = rules.ToDictionary(r => r.From, StringComparer.Ordinal);
        var textRegex = rules.ToDictionary(r => r.Id, r => TextRegex(r.From), StringComparer.Ordinal);
        var inArgs = rules.ToDictionary(r => r.Id, _ => 0, StringComparer.Ordinal);
        var inTexts = rules.ToDictionary(r => r.Id, _ => 0, StringComparer.Ordinal);

        var formulas = new List<FormulaDef>(model.Formulas.Count);
        foreach (var f in model.Formulas)
        {
            var tokens = f.Arguments.Split(';');
            var argsChanged = false;
            for (var i = 0; i < tokens.Length; i++)
            {
                var trimmed = tokens[i].Trim();
                if (byFrom.TryGetValue(trimmed, out var rule))
                {
                    tokens[i] = rule.To;
                    inArgs[rule.Id]++;
                    argsChanged = true;
                }
            }

            var text = f.Text;
            foreach (var rule in rules)
            {
                var re = textRegex[rule.Id];
                var count = re.Count(text);
                if (count > 0)
                {
                    inTexts[rule.Id] += count;
                    text = re.Replace(text, rule.To);
                }
            }

            formulas.Add(argsChanged || text != f.Text
                ? f with { Arguments = argsChanged ? string.Join(";", tokens) : f.Arguments, Text = text }
                : f);
        }

        var applied = rules
            .Where(r => inArgs[r.Id] + inTexts[r.Id] > 0)
            .Select(r => new NormalizationApplied(r.Id, r.From, r.To, inArgs[r.Id], inTexts[r.Id]))
            .ToList();

        var normalized = new MethodologyModel
        {
            Formulas = formulas,
            Constants = model.Constants,
            BuildStats = model.BuildStats,
        };
        return (normalized, applied);
    }

    // Межі імені: перед `!X` — не слово й не `!`; перед `CST.X` — не слово й не `.`; після — не слово (не префікс довшого імені).
    private static Regex TextRegex(string from)
    {
        var before = from[0] == '!' ? @"(?<![\w!])" : @"(?<![\w.])";
        return new Regex(before + Regex.Escape(from) + @"(?!\w)", RegexOptions.CultureInvariant);
    }
}
