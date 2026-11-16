// tests/Ecr.Infrastructure.Tests/Persistence/WorkflowNoneCase.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B138/B144 (F13): як документ отримує версію шаблону за період — чотири випадки для тестів режиму «без подання й
/// затвердження» (<c>WorkflowMode.None</c>).
/// </summary>
/// <remarks>
/// Версія документа: без власного шаблону — <c>doc.fn_VersionOf(проєкт, період)</c> (основний шаблон проєкту); із
/// власним (B74, <c>doc.Document.TemplateId</c>) — <c>doc.fn_TemplateVersionOf(проєкт, шаблон, період)</c>. Коли власний
/// шаблон — той самий, що основний, обидві функції дають ОДНУ версію, і підміна однієї іншою тестом не ловиться.
/// Тому два випадки розводять їх: власний шаблон документа — ІНШИЙ шаблон зі своєю версією
/// (<c>doc.ProjectTemplate</c>), а «без погодження» стає або лише власна версія, або лише версія проєкту.
/// </remarks>
internal static class WorkflowNoneCase
{
    /// <summary>Без власного шаблону: версія проєкту стає None → документ без погодження.</summary>
    public const string Plain = "plain";

    /// <summary>Власний шаблон = основний шаблон проєкту: та сама версія стає None → документ без погодження.</summary>
    public const string SameTemplate = "sameTemplate";

    /// <summary>Власний шаблон ІНШИЙ; None стає ВЛАСНА версія, версія проєкту — ні → документ без погодження.</summary>
    public const string OwnNone = "ownNone";

    /// <summary>Власний шаблон ІНШИЙ; None стає версія ПРОЄКТУ, власна — ні → документ лишається з погодженням.</summary>
    public const string ProjectNone = "projectNone";

    /// <summary>Період допоміжного ланцюга (чужий шаблон) — окремий, щоб не потрапляти в лічильники періоду тесту.</summary>
    private const int OtherChainPeriod = 209912;

    /// <param name="NoneVersionId">Версія, яку тест перемикає в None.</param>
    /// <param name="DocumentBecomesNone">Чи стає документ «без погодження» після перемикання.</param>
    internal sealed record Binding(int NoneVersionId, bool DocumentBecomesNone);

    /// <summary>Прив'язує ВСІ документи проєкту <paramref name="doc"/> до шаблону за випадком <paramref name="mode"/>.</summary>
    public static async Task<Binding> BindAsync(string mode, string connectionString, EcrDbContext db, TestDocument doc)
    {
        switch (mode)
        {
            case Plain:
                return new Binding(doc.TemplateVersionId, DocumentBecomesNone: true);

            case SameTemplate:
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE doc.Document SET TemplateId = {doc.TemplateId} WHERE ProjectId = {doc.ProjectId}").ConfigureAwait(false);
                return new Binding(doc.TemplateVersionId, DocumentBecomesNone: true);

            case OwnNone:
            case ProjectNone:
                // Чужий шаблон зі своєю опублікованою версією — з окремого ланцюга будівника.
                var other = await new TestDocumentBuilder(connectionString)
                    .BuildAsync(periodKey: OtherChainPeriod).ConfigureAwait(false);
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({doc.ProjectId}, {other.TemplateId}, {other.TemplateVersionId})")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE doc.Document SET TemplateId = {other.TemplateId} WHERE ProjectId = {doc.ProjectId}").ConfigureAwait(false);
                return mode == OwnNone
                    ? new Binding(other.TemplateVersionId, DocumentBecomesNone: true)
                    : new Binding(doc.TemplateVersionId, DocumentBecomesNone: false);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Невідомий випадок версії.");
        }
    }

    /// <summary>Перемикає версію випадку в режим «без подання й затвердження».</summary>
    public static Task<int> SwitchAsync(EcrDbContext db, Binding binding)
        => db.Database.ExecuteSqlAsync(
            $"UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = {binding.NoneVersionId}");
}
