using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Які довідники читає формула ШАБЛОНУ через форми <c>REG*</c> (<c>REGFIND</c>, <c>REGONE</c>,
/// <c>REGSUM</c>, <c>REGAVG</c>, <c>REGMIN</c>, <c>REGMAX</c>, <c>REGCOUNT</c>).
/// </summary>
/// <remarks>
/// ⛔ LP-40. Шаблонний контекст перерахунку знімка довідників не мав: <c>REGFIND</c> у формулі
/// колонки завжди давав <c>#REF</c> (<c>RegistryForms.RegFind</c>), тож усі мости на довідники
/// (HSE400, NAE-групи) лишались порожніми. Цей клас дає дві речі: (1) гейт — чи віддавати формулі
/// знімок (формули з <c>REGFIELD([Lookup], …)</c> без <c>REG*</c> лишаються на вузькому словнику
/// полів Lookup-комірок, джерело читання в них не змінюється); (2) перелік кодів довідників із
/// літералів — як <c>GenericCalculationModule.RegistryCodes</c> для методологій.
/// </remarks>
internal static class TemplateRegistryUse
{
    /// <summary>Обходить вираз; повертає <c>true</c>, якщо в ньому є форма <c>REG*</c> діалекту шаблону.</summary>
    /// <param name="root">Корінь розібраного виразу.</param>
    /// <param name="codes">Куди складати коди довідників із літералів; <c>null</c> — лише гейт.</param>
    public static bool Scan(AstNode root, ISet<string>? codes)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = false;
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case FunctionNode function:
                    if (RegistryForms.Handles(function.Name, ExpressionDialect.Template))
                    {
                        found = true;

                        if (codes is not null
                            && function.Arguments.Count > 0
                            && ReferenceResolver.RegistryCodeLiteral(function.Arguments[0]) is { } code)
                        {
                            codes.Add(code);
                        }
                    }

                    foreach (var argument in function.Arguments)
                    {
                        pending.Push(argument);
                    }

                    break;

                case BinaryNode binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.Condition);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;

                default:
                    break;
            }
        }

        return found;
    }
}
