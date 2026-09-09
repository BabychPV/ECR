// tests/Ecr.Infrastructure.Tests/Jobs/ConsistencyCheckJobTests.cs
using System.Text.RegularExpressions;
using Ecr.Application.Registries;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Нічна перевірка інваріантів (ФВ-7.7). **Знахідка — баг, а не шум**: якщо
/// перевірка регулярно щось знаходить і це вважають нормою, вона перестає
/// працювати як сигнал.
/// </summary>
public sealed partial class ConsistencyCheckJobTests
{
    // ⛔ Q-184: три тести, що були тут (осиротілі комірки, порушений FK,
    // звірка архіву), робили лише `File.ReadAllText` + `Assert.Contains` —
    // доведено мутацією, що заміна анти-джойну виявлення на завжди-хибний
    // лишала перевірений підрядок незмінним. Перенесено в
    // `ConsistencyCheckJobDetectionTests` — реальний запуск
    // `ConsistencyCheckJob.ExecuteAsync` проти живих рядків на SQLEXPRESS.

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.16")]
    public void Ставить_і_знімає_IsOrphaned_в_обидва_боки()
    {
        // ⚠ Механізм СИМЕТРИЧНИЙ: те, що ставить ознаку, її ж і знімає.
        // Асиметрія тут не половина функції, а пастка — виправлення довідника
        // не розблокувало б Submit, і користувач лишився б із помилкою,
        // причину якої вже усунуто (ФВ-8.13a).
        var flag = OrphanScanPlan.Plan(
            [new OrphanCandidate(1, PeriodState.Open, IsOrphaned: false, ReferenceIsValid: false)]);

        var clear = OrphanScanPlan.Plan(
            [new OrphanCandidate(1, PeriodState.Open, IsOrphaned: true, ReferenceIsValid: true)]);

        Assert.Equal([1L], flag.ToFlag);
        Assert.Empty(flag.ToClear);

        Assert.Equal([1L], clear.ToClear);
        Assert.Empty(clear.ToFlag);

        // Задача робить це ОДНИМ проходом сканера, а не власною копією
        // правила: дві реалізації розійшлися б, і нічний прохід скасовував би
        // те, що зробив денний.
        Assert.Contains("scanner.ScanAllAsync", Source(), StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.8")]
    public void Не_чіпає_закриті_періоди()
    {
        var decision = OrphanScanPlan.Plan(
        [
            new OrphanCandidate(1, PeriodState.Open, IsOrphaned: false, ReferenceIsValid: false),
            new OrphanCandidate(2, PeriodState.Closed, IsOrphaned: false, ReferenceIsValid: false),
            new OrphanCandidate(3, PeriodState.Closed, IsOrphaned: true, ReferenceIsValid: true),
        ]);

        // ⛔ Закритий період не чіпається в ОБИДВА боки: ні поставити, ні
        // зняти. Його дані вже подані й погоджені — ознака нічого не
        // розблокує і нічого не заборонить, зате перепише рядок, що входить у
        // контрольну суму зрізу подання.
        Assert.Equal([1L], decision.ToFlag);
        Assert.Empty(decision.ToClear);

        // ⚠ Але ЗНАХІДКУ в закритому періоді записують — там вона
        // найнебезпечніша, бо дані вже подані. Різниця в тому, ЩО робиться:
        // похідну ознаку не перераховують, а порушення посилання фіксують.
        var source = Source();
        Assert.Contains("ORPHANED_CELL", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodState.Closed", source, StringComparison.Ordinal);
    }

    private static string Source()
        => File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Jobs", "ConsistencyCheckJob.cs"));

    /// <summary>Коментарі коду — те, що не виконується.</summary>
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Корінь репозиторію.</summary>
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
