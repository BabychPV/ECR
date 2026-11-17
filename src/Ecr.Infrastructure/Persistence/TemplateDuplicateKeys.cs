// src/Ecr.Infrastructure/Persistence/TemplateDuplicateKeys.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ⛔ B158 (b): програш гонки «перевір, потім встав» у шаблонах, версіях, структурі версії, політиках і календарі
/// періодів — у ту саму відмову, що й послідовний шлях, а не сирий <see cref="DbUpdateException"/>
/// (500 <c>ECR-SYS-0500</c>).
/// </summary>
/// <remarks>
/// Мапиться за ІМЕНЕМ індексу в лапках (<see cref="SqlConflict.ViolatesIndex"/> — перевірка входження: без лапок
/// <c>UQ_Period</c> збігся б і з <c>UQ_PeriodPolicy</c>, а <c>UQ_TemplateVersion</c> — з будь-яким довшим ім'ям) і
/// лише для відомих індексів; решту викликач перекидає як є.
/// <para>
/// Спільне для <see cref="UnitOfWork"/> і <see cref="TemplateVersionStore"/>: створення шаблону й версії (і клон)
/// зберігає напряму через контекст, і його виняток до одиниці роботи не доходить.
/// </para>
/// </remarks>
internal static class TemplateDuplicateKeys
{
    /// <summary>
    /// b3–b8: індекси структури версії, які пишуть upsert-обробники (аркуш, таблиця, колонка, рядок, поле шапки,
    /// правило перевірки). Рішення «новий чи наявний» вони приймають ДО блоку рядка версії, тож другий із двох
    /// однакових запитів чекає на блок і все одно вставляє. Послідовний повтор — оновлення без помилки, тож програш
    /// гонки — загальний конфлікт «дані змінилися»: повторний запит уже оновить рядок.
    /// </summary>
    private static readonly string[] StructureUpsertIndexes =
    [
        "'UQ_SheetDef'",
        "'UQ_TableDef'",
        "'UQ_ColumnDef'",
        "'UQ_RowDef'",
        "'UQ_HeaderFieldDef'",
        "'UQ_ValidationRule'",
    ];

    /// <summary>Відмова для відомого індексу шаблонів/періодів або <c>null</c>.</summary>
    /// <param name="ex">Виняток збереження (порушення унікальності).</param>
    /// <returns>Виняток, який треба кинути замість <paramref name="ex"/>, або <c>null</c>.</returns>
    public static Exception? TryMap(DbUpdateException ex)
    {
        // b1: два `POST /templates` з тим самим кодом — та сама відмова, що в `CreateTemplateAsync`.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_Template_Code'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is Template)
                is { Entity: Template template })
        {
            return new BusinessRuleException(
                "ECR-TMPL-0409",
                $"Шаблон з кодом «{template.Code}» уже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.templateCodeTaken",
                    ["code"] = template.Code,
                });
        }

        // b2: дві нові версії (або клони) з тим самим номером — та сама відмова, що в `EnsureVersionNumberFreeAsync`.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_TemplateVersion'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is TemplateVersion)
                is { Entity: TemplateVersion version })
        {
            return new BusinessRuleException(
                "ECR-TMPL-0409",
                $"Версія {version.Version} у цьому шаблоні вже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.versionNumberTaken",
                    ["version"] = version.Version,
                });
        }

        // b9: дві однакові політики періодів — та сама відмова, що в `CreatePeriodPolicyHandler`.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_PeriodPolicy'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is PeriodPolicy)
                is { Entity: PeriodPolicy policy })
        {
            return new BusinessRuleException(
                ErrorCodes.PeriodPolicyDuplicate,
                $"Політика з кодом «{policy.Code}» уже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4091.code",
                    ["code"] = policy.Code,
                });
        }

        // b3–b8: upsert структури версії.
        foreach (var index in StructureUpsertIndexes)
        {
            if (SqlConflict.ViolatesIndex(ex, index))
            {
                return ConcurrentChange();
            }
        }

        // b10, страховка: календар добудовується під блоком рядка проєкту (`PeriodStore.LockProjectCalendarAsync`),
        // тож сюди доходить лише шлях, що пише періоди без цього блоку, — загальний конфлікт, а не 500.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_Period'"))
        {
            return ConcurrentChange();
        }

        return null;
    }

    private static ConcurrencyConflictException ConcurrentChange()
        => new(
            "ECR-CELL-0409",
            "Дані щойно змінив інший запит: перечитайте їх і повторіть дію.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
            });
}
