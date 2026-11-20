using System.Text;

namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>Друк речень <c>rows from … where … active when …</c> (план, розд. 8.2).</summary>
public static class RowsClauseWriter
{
    /// <summary>Друкує одне речення (з крапкою з комою й переведенням рядка в кінці).</summary>
    /// <param name="clause">Речення.</param>
    /// <exception cref="ConversionException">Немає таблиць або правил, чи ім'я колонки не записується.</exception>
    public static string Write(SourceRowsClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);

        if (clause.TableCodes.Count == 0)
        {
            throw new ConversionException("rowsNoTables", "A rows clause names no tables.");
        }

        if (clause.Rules.Count == 0)
        {
            throw new ConversionException(
                "rowsNoRules",
                "A rows clause has no active rules: the old engine's behaviour for a binding without rules is not reproduced.");
        }

        var sb = new StringBuilder("rows from ").Append(string.Join(", ", clause.TableCodes));

        // Порожнє правило {} — «усі рядки»: «або» з ним є істиною, тож where немає.
        var catchAll = clause.Rules.Any(r => r.Pairs.Count == 0);
        if (!catchAll)
        {
            var rules = clause.Rules.Select(WriteRule).ToList();
            sb.Append("\n    where ");
            if (rules.Count == 1)
            {
                sb.Append(rules[0]);
            }
            else
            {
                sb.Append(string.Join("\n       || ", rules.Select(r => "(" + r + ")")));
            }
        }

        if (clause.ActiveColumns.Count > 0)
        {
            sb.Append("\n    active when ")
              .Append(string.Join(" && ", clause.ActiveColumns.Select(c =>
                  Column(c.ColumnCode) + " == " + Entry(c.RegistryCode, c.EntryCode))));
        }

        return sb.Append(";\n").ToString();
    }

    private static string WriteRule(SourceRule rule)
        => string.Join(" && ", rule.Pairs.Select(p => Column(p.ColumnCode) + " == " + Value(p.Value)));

    private static string Column(string code)
    {
        if (!ScriptText.IsName(code))
        {
            throw new ConversionException("invalidName", $"Column «{code}» cannot be written as row.<name>.");
        }

        return "row." + code;
    }

    private static string Value(SourceMatchValue value)
        => value.IsEntry ? Entry(value.RegistryCode!, value.EntryCode!) : ScriptText.Quote(value.Text ?? string.Empty);

    private static string Entry(string registry, string entry)
        => "entry(" + ScriptText.Quote(registry) + ", " + ScriptText.Quote(entry) + ")";
}
