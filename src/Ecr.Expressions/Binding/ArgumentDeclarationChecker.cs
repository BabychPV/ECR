// src/Ecr.Expressions/Binding/ArgumentDeclarationChecker.cs
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Звіряє токени <c>@Arg</c> у тексті виразу з <b>оголошеним</b> списком
/// аргументів формули (<c>FormulaDef.Arguments</c>, перенос <c>FInfo_Arguments</c>).
/// </summary>
/// <remarks>
/// ⛔ Джерело істини про аргументи — СПИСОК, а не текст виразу (директива ПК-1
/// №05 §7, пастка 2). Збірка підставляє рівно те, що перелічено в
/// <c>;</c>-списку; токен, якого в списку немає, у вираз <b>не потрапляє</b>.
/// Наслідок у чинній системі не аварія, а число: формула рахується з
/// невизначеним параметром і повертає правдоподібний результат, який помітять
/// на звірці через місяць — якщо помітять.
/// <para>
/// ⚠ Два боки розбіжності мовчазні по-різному, тому й реакція різна:
/// «у тексті, немає в списку» — помилка публікації (<c>ECR-CALC-0432</c>),
/// «у списку, немає в тексті» — лише попередження. Друге чинна система
/// допускала масово (замір: 359 таких аргументів у 186 формулах проти 38
/// токенів першого роду у двох формулах <c>Flert</c>), і ламати міграцію
/// корпусу через це не можна.
/// </para>
/// </remarks>
public static class ArgumentDeclarationChecker
{
    /// <summary>Розділювач <c>;</c>-списку <c>FInfo_Arguments</c>.</summary>
    private const char DeclarationSeparator = ';';

    /// <summary>Розбирає оголошений список аргументів.</summary>
    /// <param name="declaration">
    /// Значення <c>FormulaDef.Arguments</c> — <c>;</c>-список; <c>null</c> і
    /// порожній рядок дають порожній перелік.
    /// </param>
    /// <returns>Імена як їх записано в джерелі, без порожніх елементів.</returns>
    /// <remarks>
    /// ⚠ Розділювач рівно один — <c>;</c>. Прийняти ще й кому означало б, що
    /// ім'я з комою (а такі в корпусі не заборонені) мовчки розпадається на
    /// два неіснуючих аргументи, і обидва потраплять у перелік «оголошено, не
    /// вжито» замість того, щоб збігтися з токеном виразу.
    /// </remarks>
    public static IReadOnlyList<string> ParseDeclaration(string? declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return [];
        }

        return declaration
            .Split(DeclarationSeparator)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>Усі вживання <c>@Arg</c> у дереві, у порядку обходу.</summary>
    /// <param name="root">Корінь виразу.</param>
    /// <returns>Вживання з позиціями; одне ім'я може трапитися кілька разів.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> — <c>null</c>.</exception>
    public static IReadOnlyList<ArgumentUsage> Used(AstNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<ArgumentUsage>();
        Walk(root, found);
        return found;
    }

    /// <summary>Звіряє вжите з оголошеним.</summary>
    /// <param name="root">Корінь розібраного виразу.</param>
    /// <param name="declared">Оголошений список (див. <see cref="ParseDeclaration"/>).</param>
    /// <param name="contextual">
    /// Контекстні аргументи методології — ті, що збірка передає <b>кожній</b>
    /// формулі незалежно від списку; <c>null</c> — жодного.
    /// </param>
    /// <returns>Перше — помилки публікації, друге — попередження.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/> або <paramref name="declared"/> — <c>null</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Контекстний аргумент глушить <b>обидва</b> боки, і причина в обох та
    /// сама: його зв'язує збірка, а не список. Тому він не є ані невизначеним
    /// параметром (бік перший), ані переданим даремно (бік другий).
    /// <para>
    /// ⚠ Без глушника другий бік дає 186 попереджень на кожну публікацію
    /// (замір корпусу), і майже всі — про <c>@CalculationDate</c> та
    /// <c>@Location</c>, які передаються всім. Перелік такого розміру
    /// перестають читати за тиждень, і разом із ним перестають бачити ті
    /// кілька, що означають описку в імені токена.
    /// </para>
    /// </remarks>
    public static ArgumentAudit Audit(
        AstNode root,
        IEnumerable<string> declared,
        IEnumerable<string>? contextual = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(declared);

        var declaredKeys = new HashSet<string>(declared.Select(NormalizeName), StringComparer.OrdinalIgnoreCase);
        var contextualKeys = new HashSet<string>(
            (contextual ?? []).Select(NormalizeName), StringComparer.OrdinalIgnoreCase);

        var used = Used(root);
        var usedKeys = new HashSet<string>(used.Select(u => NormalizeName(u.Name)), StringComparer.OrdinalIgnoreCase);

        // ⚠ Перше вживання, а не всі: методолог виправляє СПИСОК, і другий
        // рядок про той самий токен не додає йому нічого, крім довжини.
        var undeclared = used
            .Where(u => !declaredKeys.Contains(NormalizeName(u.Name))
                        && !contextualKeys.Contains(NormalizeName(u.Name)))
            .GroupBy(u => NormalizeName(u.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var unused = declared
            .Where(name => !usedKeys.Contains(NormalizeName(name))
                           && !contextualKeys.Contains(NormalizeName(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ArgumentAudit(undeclared, unused);
    }

    /// <summary>Ключ порівняння імені аргументу.</summary>
    /// <param name="name">Ім'я з тексту виразу або зі списку.</param>
    /// <returns>Нормалізоване ім'я; регістр не знімається — його знімає компаратор.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> — <c>null</c>.</exception>
    /// <remarks>
    /// ⛔ Крапки зводяться до підкреслень, бо саме це робить чинна система —
    /// і робить <b>симетрично</b>: <c>Utilities.cs:220</c> застосовує
    /// <c>Regex.Replace(expr, @"(?&lt;!\d)\.(?!\d)", "_")</c> і до тексту
    /// формули, і до <c>FInfo_Arguments</c>. Порівнювати сирі рядки означало б
    /// оголосити невизначеними 2534 дотових посилання корпусу лише тому, що в
    /// списку вони записані іншою половиною тієї самої пари.
    /// <para>
    /// ⚠ Ціна названа: <c>A.B</c> і <c>A_B</c> тут той самий аргумент. У
    /// чинній системі вони теж той самий аргумент, тож нова розбіжність не
    /// з'являється — але два РІЗНІ імена, що відрізняються лише крапкою,
    /// перевірка не розрізнить.
    /// </para>
    /// <para>
    /// ⚠ Регістр: імена параметрів чинна система порівнює без урахування
    /// регістру (<c>Utilities.cs:186</c>), і перенесено це дослівно.
    /// </para>
    /// </remarks>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        if (trimmed.StartsWith('@'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.Replace('.', '_');
    }

    /// <summary>Обхід дерева зі збиранням <c>@Arg</c>.</summary>
    private static void Walk(AstNode node, List<ArgumentUsage> found)
    {
        switch (node)
        {
            case SymbolReferenceNode { Kind: SymbolKind.Argument } symbol:
                found.Add(new ArgumentUsage(symbol.Name, symbol.Position));
                return;

            case UnaryNode unary:
                Walk(unary.Operand, found);
                return;

            case BinaryNode binary:
                Walk(binary.Left, found);
                Walk(binary.Right, found);
                return;

            case ConditionalNode conditional:
                Walk(conditional.Condition, found);
                Walk(conditional.WhenTrue, found);
                Walk(conditional.WhenFalse, found);
                return;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    Walk(argument, found);
                }

                return;

            // ⚠ Посилання на комірку в діалекті методологій заборонене (02b
            // §3.4), але предикат рядка — це піддерево, і пропустити його
            // означало б не побачити токен, який туди сховався.
            case CellReferenceNode { Row: RowSelector.Predicate predicate }:
                Walk(predicate.Condition, found);
                return;

            default:
                return;
        }
    }
}

/// <summary>Одне вживання аргументу у виразі.</summary>
/// <param name="Name">Ім'я як його написано в тексті, без <c>@</c>.</param>
/// <param name="Position">Позиція в тексті виразу — щоб редактор підсвітив саме її.</param>
public sealed record ArgumentUsage(string Name, int Position);

/// <summary>Результат звірки тексту виразу з оголошеним списком аргументів.</summary>
/// <param name="Undeclared">
/// Токени, що є у виразі й відсутні в списку, — <b>помилка</b> публікації
/// (<c>ECR-CALC-0432</c>): збірка їх не підставить, і формула порахується з
/// невизначеним параметром.
/// </param>
/// <param name="UnusedDeclared">
/// Імена, оголошені в списку й відсутні у виразі, — <b>попередження</b>: чинна
/// система це допускала, і ламати міграцію корпусу через це не можна.
/// </param>
public sealed record ArgumentAudit(
    IReadOnlyList<ArgumentUsage> Undeclared,
    IReadOnlyList<string> UnusedDeclared);
