using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>
/// Відмова через зауваження до виразу — на ЗБЕРЕЖЕННІ формули чи правила і на
/// публікації (V-19).
/// </summary>
/// <remarks>
/// ⛔ V-19 (UX-прохід 2026-09-24, третій раунд): <c>[CDEC] * * 2</c>,
/// <c>[NOPE] + 1</c> і правило <c>[CDEC] &gt;=</c> зберігалися з «saved» —
/// обробники запису виразу не перевіряли зовсім (перевірка стояла лише на
/// публікації, а для правил валідації — ніде). А публікація, коли таки
/// відмовляла, показувала загальне «does not pass validation · ECR-TMPL-0422»:
/// причина («Колонки 'NOPE' немає в таблиці 'FT2'») лежала в
/// <c>Details.diagnostics</c> українським реченням, без ключа.
///
/// ⚠ Тепер ключ відмови — ключ ПЕРШОГО зауваження, яке його має
/// (<c>expr.*</c> парсера й резолвера), а його підстановки — поля
/// <c>Details</c>: конвеєр помилок (<c>ExceptionHandlingMiddleware</c>)
/// локалізує саме таку пару. Решта зауважень іде переліком
/// <c>diagnostics</c> теж із ключами. Зауваження без ключа (типи, одиниці,
/// цикл) дають загальний, але конкретний ключ: код і позиція.
/// </remarks>
public static class ExpressionRejection
{
    /// <summary>
    /// Перевіряє синтаксис і посилання виразу проти структури версії; відмовляє, якщо є зауваження.
    /// </summary>
    /// <param name="formulaEngine">Рушій — розбір і резолвінг.</param>
    /// <param name="version">Версія-чернетка з повною структурою.</param>
    /// <param name="expression">Текст виразу.</param>
    /// <param name="dialect">Діалект.</param>
    /// <param name="site">Місце виразу: таблиця, рядок, колонка.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-TMPL-0422</c> із ключем першого зауваження.</exception>
    /// <remarks>
    /// ⚠ Типи й одиниці на збереженні НЕ перевіряються — лише синтаксис і
    /// посилання. Тип залежить від колонок, яких у чернетці ще може не бути
    /// (формулу пишуть до того, як доведуть структуру), і відмова на цьому
    /// кроці заважала б будувати шаблон у природному порядку. Публікація
    /// перевіряє все — там структура вже остаточна.
    /// </remarks>
    public static void RequireValid(
        IFormulaEngine formulaEngine,
        TemplateVersion version,
        string expression,
        ExpressionDialect dialect,
        ExpressionSite site)
    {
        ArgumentNullException.ThrowIfNull(formulaEngine);
        ArgumentNullException.ThrowIfNull(version);

        var diagnostics = new List<ExpressionDiagnostic>();
        PublishChecks.CheckExpression(
            expression,
            dialect,
            site,
            new ExpressionScope(formulaEngine, PublishChecks.Snapshot(version), TypeContext: null, UnitContext: null),
            diagnostics);

        if (diagnostics.Count > 0)
        {
            throw Build(
                diagnostics,
                "err.ECR-TMPL-0422.expressionInvalid",
                $"Вираз «{expression}» відхилено: {diagnostics[0].Message}",
                extra: new Dictionary<string, object?> { ["expression"] = expression });
        }
    }

    /// <summary>Будує відмову з переліку зауважень.</summary>
    /// <param name="diagnostics">Зауваження; не порожні.</param>
    /// <param name="fallbackKey">Ключ, коли жодне зауваження власного не несе.</param>
    /// <param name="message">Запасне речення (показується, коли ключа немає в каталозі).</param>
    /// <param name="extra">Додаткові поля <c>Details</c>.</param>
    public static BusinessRuleException Build(
        IReadOnlyList<ExpressionDiagnostic> diagnostics,
        string fallbackKey,
        string message,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var first = diagnostics[0];
        var keyed = diagnostics.FirstOrDefault(d => d.MessageKey is not null);

        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                details[name] = value;
            }
        }

        if (keyed is not null)
        {
            foreach (var (name, value) in keyed.MessageParams ?? new Dictionary<string, string>())
            {
                details[name] = value;
            }

            details["messageKey"] = keyed.MessageKey;
        }
        else
        {
            details["code"] = first.Code;
            details["position"] = first.Position.ToString(CultureInfo.InvariantCulture);
            details["count"] = diagnostics.Count.ToString(CultureInfo.InvariantCulture);
            details["messageKey"] = fallbackKey;
        }

        details["diagnostics"] = diagnostics
            .Select(d => new DiagnosticInfo(d.Code, d.Message, d.Position, d.Length, d.MessageKey, d.MessageParams))
            .ToList();

        return new BusinessRuleException(ErrorCodes.TemplateInvalid, message, details);
    }
}
