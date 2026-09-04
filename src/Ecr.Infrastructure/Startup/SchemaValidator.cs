using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Перевірки при старті (ФВ-7.9). Мета — **впасти зрозуміло**, а не працювати
/// на несумісному середовищі й з'ясувати це на першому записі.
/// </summary>
public sealed class SchemaValidator(EcrDbContext db, ISqlCapabilities capabilities)
{
    /// <summary>Виконує послідовність перевірок.</summary>
    /// <param name="startupMode"><c>Validate</c> у прод, <c>Migrate</c> у dev/test.</param>
    /// <exception cref="InvalidOperationException">
    /// Середовище непридатне; повідомлення пояснює, що саме і як виправити.
    /// </exception>
    public Task ValidateAsync(string startupMode, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — послідовність із B01 §6.3:\n" +
            "1) retry-очікування доступності БД (БД піднімається довше застосунку);\n" +
            "2) у БД є міграція, якої немає у збірці → ФАТАЛЬНО (відкат версії застосунку);\n" +
            "3) Validate: pending.Any() → ФАТАЛЬНО зі списком; Migrate: sp_getapplock → " +
            "   Migrate() → release (щоб два інстанси не мігрували одночасно);\n" +
            "4) ⛔ Standard із ProductMajorVersion < 13 → ЗУПИНКА СТАРТУ: немає партиціонування, " +
            "   columnstore і компресії, тобто модель архівації не працює в принципі (АРХ-7);\n" +
            "5) RCSI вимкнено → Critical у health і запис у журнал (вмикання — операція DBA);\n" +
            "6) відсутні файлові групи або схеми партиціонування → ЗУПИНКА з інструкцією, " +
            "   який скрипт виконати;\n" +
            "7) немає запасу партицій на наступний період → Warning у health.");
}
