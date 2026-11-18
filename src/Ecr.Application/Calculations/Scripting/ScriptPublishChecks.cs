// src/Ecr.Application/Calculations/Scripting/ScriptPublishChecks.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>
/// Перевірки й матеріалізація публікації версії з рушієм <c>Script</c> (MS, план 4.6, 5.2): замість формульних
/// кроків (<c>CheckRulesAsync</c>, топологічний порядок, перевірки формул і констант).
/// </summary>
/// <remarks>
/// Порядок: помилки компіляції → діагностики оточення (одиниці, речовини, таблиці, колонки, довідники, читання
/// результатів, цикл) → матеріалізація (виходи, речовини, імпорти, таблиці читання, прив'язки, залежності,
/// довідники) → <c>noBoundOutput</c>. Будь-яка проблема — <c>422 ECR-CALC-0422</c> з переліком <c>problems</c>
/// (ключ кожної — <c>script.&lt;code&gt;</c>, підстановки — параметри діагностики плюс <c>line</c>/<c>col</c>: позиція в тексті).
/// Зміни лишаються у трекері: відкотом на невдалу публікацію володіє транзакція викликача.
/// </remarks>
public sealed class ScriptPublishChecks(
    ScriptSourceCompiler compiler,
    ScriptEnvironmentChecker environmentChecker,
    ScriptMaterializer materializer,
    IMethodologyStore methodologies)
{
    /// <summary>Виконує перевірки й матеріалізує скрипт версії.</summary>
    /// <param name="methodology">Методологія.</param>
    /// <param name="version">Версія, що публікується (рушій <c>Script</c>).</param>
    /// <param name="effectiveFrom">Дата набуття чинності — на неї добираються версії бібліотек.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c>: <c>scriptEmpty</c> — тексту немає; <c>publishChecksFailed</c> — є проблеми.
    /// </exception>
    public async Task<ScriptMaterialization> RunAsync(
        Methodology methodology, MethodologyVersion version, DateOnly effectiveFrom, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(version);

        var script = await methodologies.GetScriptAsync(version.Id, ct).ConfigureAwait(false)
            ?? throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Версія {version.Version}: тексту скрипта немає, публікувати нічого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptEmpty",
                    ["version"] = version.Version,
                });

        // ⛔ F13a: публікується РІВНО той текст, який ця одиниця роботи прочитала разом із версією (токен паралельності
        // `ScriptHash` стоїть на тій самій сутності). Збереження, що вклинилося між читанням версії й читанням тексту,
        // дало б матеріалізацію з одного тексту й публікацію з іншого: відмова тут, а не мовчазне розходження.
        if (script.Hash is not null && !script.Hash.AsSpan().SequenceEqual(version.ScriptHash))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Скрипт версії {version.Version} змінено під час публікації: повторіть публікацію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.scriptChanged",
                    ["version"] = version.Version,
                    ["hash"] = ScriptAnalyzer.Hex(script.Hash),
                });
        }

        var isLibrary = methodology.Kind == MethodologyKind.Library;
        var compilation = await compiler
            .CompileForVersionAsync(script.Text, methodology, version, effectiveFrom, ct)
            .ConfigureAwait(false);

        var problems = compilation.Diagnostics.Select(ToProblem).ToList();
        if (compilation.Script is null)
        {
            Reject(problems);
        }

        var environment = await environmentChecker
            .CheckAsync(compilation.Script!, script.Text, methodology.Id, ct)
            .ConfigureAwait(false);
        problems.AddRange(environment.Diagnostics.Select(ToProblem));
        Reject(problems);

        var materialized = await materializer
            .MaterializeAsync(
                version.Id, methodology.Id, compilation.Script!, environment, new ScriptMaterializeOptions(Publish: true), ct)
            .ConfigureAwait(false);

        // Методологія «нічого не запише»: жоден вихід не має активної прив'язки навіть після автоприв'язки.
        if (!isLibrary && materialized.BoundOutputCodes.Count == 0)
        {
            problems.Add(ToProblem(new ScriptDiagnostic(
                ScriptDiagnosticCodes.NoBoundOutput, 1, 1, 1,
                "No output of the script is bound to a column: the methodology would write nothing.",
                new Dictionary<string, string>(StringComparer.Ordinal))));
            Reject(problems);
        }

        return materialized;
    }

    /// <summary>Діагностика скрипта як проблема перевірки публікації.</summary>
    /// <param name="diagnostic">Діагностика.</param>
    public static PublishProblem ToProblem(ScriptDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var args = new Dictionary<string, string>(diagnostic.Params, StringComparer.Ordinal)
        {
            ["line"] = diagnostic.Line.ToString(CultureInfo.InvariantCulture),
            ["col"] = diagnostic.Column.ToString(CultureInfo.InvariantCulture),
        };
        return new PublishProblem(
            diagnostic.MessageKey,
            args,
            $"{diagnostic.Message} (line {diagnostic.Line}, column {diagnostic.Column})");
    }

    private static void Reject(List<PublishProblem> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            MethodologyPublishChecks.Describe(problems),
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.publishChecksFailed",
                ["count"] = problems.Count.ToString(CultureInfo.InvariantCulture),
                ["problems"] = problems,
            });
    }
}
