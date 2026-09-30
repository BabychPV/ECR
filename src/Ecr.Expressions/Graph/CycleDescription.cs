namespace Ecr.Expressions.Graph;

/// <summary>
/// Людський опис знайденого циклу формул (<c>ECR-TMPL-4221</c>).
/// </summary>
/// <remarks>
/// ⛔ Клас існує, щоб розв'язати суперечність, яка жила в коді:
/// <c>FormulaEngine.BuildEvaluationOrder</c> документував, що формула,
/// залежна від себе, — це цикл і публікація має його побачити, а **обидва**
/// викликачі відсіювали самопосилання перед побудовою графа. Тобто обіцянка
/// рушія була недосяжна.
///
/// ⚠ Кожна сторона мала половину рації. Рушій має рацію по суті: формула, що
/// читає власний результат, не має порядку обчислення, і в чинній системі вона
/// **мовчки не рахується** — ітерація до нерухомої точки виходить після ста
/// проходів без результату і без запису (<c>StagesBase.cs:187-232</c>).
/// Викликачі мали рацію щодо форми: «формули утворюють цикл: X → X» нічого не
/// пояснює тому, хто це читає.
///
/// ⛔ Тому самопосилання тепер **є** циклом, але **названим окремо**. Це
/// перетворює тихий нерезультат чинної системи на явну відмову публікації — і
/// саме такі випадки директива №06 (<c>H-24d-2</c>) велить рахувати
/// **покращенням**, а не регресією, в звіті золотої звірки.
/// </remarks>
public static class CycleDescription
{
    /// <summary>
    /// Описує цикл словами, які пояснюють, що робити.
    /// </summary>
    /// <param name="path">Шлях циклу; порожній — цикл є, але шлях невідомий.</param>
    /// <param name="nameOf">Ім'я формули за ідентифікатором; для читабельності.</param>
    /// <returns>Готовий текст діагностики.</returns>
    public static string Describe(
        IReadOnlyList<int>? path, Func<int, string>? nameOf = null)
    {
        // ⛔ Цикл довжиною в один вузол — це самопосилання, і воно потребує
        // ІНШИХ слів. «Формули утворюють цикл: X → X» читач сприймає як збій
        // сортувальника, а не як опис своєї формули, і йде шукати проблему не
        // туди.
        //
        // ⛔ Розпізнається шлях, де ВСІ вузли однакові, а не лише `Count == 1`
        // (аудит 2026-09-16, §2.2). `TopologicalSorter.Walk` при справжньому
        // самопосиланні будує шлях `[node, node]` — два елементи (так і
        // задокументовано тестом `Самопосилання_формули_це_цикл`, що очікує
        // `[7, 7]`), а `PublishChecks`/`PublishMethodologyHandler` передають
        // `CyclePath` прямо сюди. Тож умова `Count == 1` не спрацьовувала НІКОЛИ
        // на реальному самопосиланні, і воно давало саме те заплутане
        // «Формули утворюють цикл: X → X», яке цей клас мав усунути. Тести на
        // обидві половини існували, але жоден не з'єднував їх.
        // ⚠ Порівнюються ІДЕНТИФІКАТОРИ, не імена: `nameOf` не зобов'язаний
        // давати унікальні підписи, і два різні вузли з однаковим іменем — це
        // справжній цикл, а не самопосилання.
        return Diagnostic(path, nameOf).Message;
    }

    /// <summary>
    /// Те саме — діагностикою з ключем каталогу (<c>expr.cycle*</c>) для
    /// локалізації клієнтом.
    /// </summary>
    /// <param name="path">Шлях циклу; порожній — цикл є, але шлях невідомий.</param>
    /// <param name="nameOf">Ім'я формули за ідентифікатором.</param>
    /// <remarks>
    /// ⛔ V-20: текст був українським реченням без ключа і доїжджав до
    /// людини як є (перевірки публікації шаблону — <c>PublishChecks</c>).
    /// Англійський текст — запасний.
    /// </remarks>
    public static Parsing.ExpressionDiagnostic Diagnostic(
        IReadOnlyList<int>? path, Func<int, string>? nameOf = null)
    {
        var ids = path ?? [];
        var names = ids.Select(id => nameOf?.Invoke(id) ?? id.ToString(
            System.Globalization.CultureInfo.InvariantCulture)).ToList();

        if (ids.Count > 0 && ids.All(id => id == ids[0]))
        {
            return new Parsing.ExpressionDiagnostic(
                ExpressionErrors.Cycle,
                $"Formula \"{names[0]}\" reads its own result. It has no evaluation order: it needs "
                + "the value before the value exists. In the legacy system such a formula was silently "
                + "never calculated.",
                0, 1,
                "expr.cycleSelf", Parsing.DiagnosticParams.Of(("name", names[0])));
        }

        return names.Count == 0
            ? new Parsing.ExpressionDiagnostic(
                ExpressionErrors.Cycle, "The formulas form a cycle.", 0, 1, "expr.cycleUnknownPath")
            : new Parsing.ExpressionDiagnostic(
                ExpressionErrors.Cycle,
                $"The formulas form a cycle: {string.Join(" → ", names)}.",
                0, 1,
                "expr.cycle", Parsing.DiagnosticParams.Of(("path", string.Join(" → ", names))));
    }
}
