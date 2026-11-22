// src/Ecr.Application/Calculations/Scripting/ScriptReadFields.cs
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>✎ D-568: поле власної таблиці, яке читає скрипт.</summary>
/// <param name="FieldCode">Ім'я поля рядка (<c>row.X</c>).</param>
/// <param name="OtherMonths">Місячне поле читається за інший місяць, ніж свій (чи індекс невідомий).</param>
public sealed record ScriptReadField(string FieldCode, bool OtherMonths);

/// <summary>
/// ✎ D-568: поля власної таблиці, які читає скомпільований скрипт (<see cref="CompiledScript.RowFields"/>), з ознакою «місячне
/// поле читається за інший місяць» — для переліку <c>calc.MethodologyReadField</c>, за яким прохід читача знаходить скрипти,
/// що читають число іншого місяця.
/// </summary>
/// <remarks>
/// «Свій місяць» — лише дослівний індекс <c>row.X[period.Month]</c> (у дереві <c>$month('X', [Period].Sequence, …)</c>); будь-який
/// інший індекс (стала, змінна, вираз, цикл) — «інший» (консервативно). Дерево, яке не вдалося обійти до кінця, — усі місячні
/// поля «інший». Помилитися можна лише в бік «читає».
/// </remarks>
public static class ScriptReadFields
{
    /// <summary>Поля власної таблиці, які читає скрипт.</summary>
    /// <param name="script">Скомпільований скрипт.</param>
    /// <returns>Поле й ознака «інший місяць»; без повторів (без регістру).</returns>
    public static IReadOnlyList<ScriptReadField> Of(CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var fields = script.RowFields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Name, Monthly: g.Any(f => f.IsMonthly)))
            .ToList();
        if (fields.Count == 0)
        {
            return [];
        }

        var (text, complete) = fields.Exists(f => f.Monthly) ? Print(script) : (string.Empty, true);
        return
        [
            .. fields.Select(f => new ScriptReadField(
                f.Name,
                f.Monthly && (!complete || Count(text, f.Name, own: false) > Count(text, f.Name, own: true)))),
        ];
    }

    private static int Count(string text, string field, bool own)
    {
        var needle = own ? $"$month('{field}', [Period].Sequence, " : $"$month('{field}', ";
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    /// <summary>Друк усіх виразів дерева скрипта; <c>false</c> — обхід неповний (стеля глибини, невідомий тип).</summary>
    private static (string Text, bool Complete) Print(CompiledScript script)
    {
        var printed = new System.Text.StringBuilder();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var complete = true;

        void Walk(object? value, int depth)
        {
            if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum
                || value is decimal or DateTime or DateOnly or TimeSpan or Guid
                || !visited.Add(value))
            {
                return;
            }

            if (depth > 64)
            {
                complete = false;
                return;
            }

            if (value is Ecr.Expressions.Ast.AstNode node)
            {
                printed.Append(Ecr.Expressions.Ast.AstPrinter.Print(node)).AppendLine();
                return;
            }

            if (value is System.Collections.IDictionary dictionary)
            {
                foreach (var item in dictionary.Values)
                {
                    Walk(item, depth + 1);
                }

                return;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Walk(item, depth + 1);
                }

                return;
            }

            var type = value.GetType();
            if (type.Namespace?.StartsWith("Ecr.", StringComparison.Ordinal) != true)
            {
                complete = false;
                return;
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    Walk(property.GetValue(value), depth + 1);
                }
            }
        }

        Walk(script, 0);
        return (printed.ToString(), complete);
    }
}
