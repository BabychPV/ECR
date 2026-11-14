// tests/Ecr.Api.Tests/YearLayoutCleanup.cs
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Tests;

/// <summary>B121: повертає версії шаблону, які тест зробив річними, до розкладки за замовчуванням.</summary>
/// <remarks>
/// ⚠ Сирим <c>UPDATE</c>, а не через домен: версія може бути опублікованою (сутність розкладку вже не міняє), а на
/// <c>cfg.TemplateVersion</c> тригера незмінності немає — заморожується структура (аркуші, таблиці, колонки), не рядок
/// версії. Решта даних тесту лишається: її ніхто не рахує, а річна розкладка вмикає роутер для ВСІХ наступних хостів.
/// </remarks>
internal static class YearLayoutCleanup
{
    /// <summary>Скидає розкладку переліченим версіям; порожній перелік — нічого.</summary>
    /// <param name="db">Контекст на спільну базу колекції.</param>
    /// <param name="templateVersionIds">Версії, які тест перевів у <see cref="TemplateLayout.YearWithMonthColumns"/>.</param>
    public static async Task ResetAsync(EcrDbContext db, IReadOnlyCollection<int> templateVersionIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(templateVersionIds);
        if (templateVersionIds.Count == 0)
        {
            return;
        }

        await db.TemplateVersions
            .Where(v => templateVersionIds.Contains(v.Id) && v.Layout == TemplateLayout.YearWithMonthColumns)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Layout, TemplateLayout.PerMonthCopies))
            .ConfigureAwait(false);
    }
}
