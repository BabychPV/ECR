namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Ідемпотентний seed. Без нього застосунок не стартує: немає ані мов, ані
/// прав, ані базових одиниць.
/// </summary>
public sealed class SeedRunner(EcrDbContext db)
{
    /// <summary>Виконує seed. Повторний запуск не створює дублікатів.</summary>
    public Task RunAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: виконати MERGE-скрипти з 02a-db-schema.md#seed у порядку:\n" +
            "1) sys_ecr.Language; 2) sec.Permission (повний каталог); 3) sec.Role (7 вбудованих);\n" +
            "4) sec.PasswordPolicy; 5) uom.Dimension (11); 6) uom.Unit базові; 7) uom.Unit похідні;\n" +
            "8) оновити Dimension.BaseUnitId; 9) doc.PeriodPolicy 'ECR-Standard'.\n" +
            "⚠ Небезпечні права (Calculation.EditScript/Publish, Security.*, Integration.Manage, " +
            "System.RunJob) у вбудовані ролі НЕ додавати: вони видаються іменованим особам " +
            "окремо (ФВ-6.12). Порожні за ними ролі — це навмисно, а не пропуск.\n" +
            "⚠ FactorToBase наявних одиниць змінювати заборонено: на них спираються фікстури.");
}
