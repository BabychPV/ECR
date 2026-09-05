// tests/Ecr.Infrastructure.Tests/Security/PasswordHasherTests.cs
using System.Diagnostics;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>Хешування паролів локальних облікових записів (ФВ-6.5).</summary>
public sealed class PasswordHasherTests
{
    private const string Password = "Kashagan-2026-Winter!";

    private readonly PasswordHasher _hasher = new();

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.5")]
    public void Однаковий_пароль_дає_різні_хеші_через_різні_солі()
    {
        var first = _hasher.Hash(Password);
        var second = _hasher.Hash(Password);

        // ⚠ Без випадкової солі однакові паролі дають однакові рядки, і одного
        // погляду на sec.User досить, щоб побачити, у кого пароль той самий, —
        // а далі райдужна таблиця ламає обидва одразу.
        Assert.NotEqual(first, second);
        Assert.True(_hasher.Verify(Password, first));
        Assert.True(_hasher.Verify(Password, second));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перевірка_виконується_за_сталий_час()
    {
        var hash = _hasher.Hash(Password);

        // Пароль тієї самої довжини, що відрізняється ПЕРШИМ символом: саме на
        // ньому ранній вихід дав би найбільшу економію.
        var wrong = string.Concat("X", Password.AsSpan(1));

        var correct = Median(() => _hasher.Verify(Password, hash));
        var incorrect = Median(() => _hasher.Verify(wrong, hash));

        Assert.False(_hasher.Verify(wrong, hash));

        // Похідна функція має відпрацювати ПОВНІСТЮ в обох випадках. Порівняння
        // хешів на її тлі — шум, і це і є мета: за часом відповіді не можна
        // сказати, скільки байтів угадано.
        var ratio = (double)Math.Max(correct, incorrect) / Math.Max(1, Math.Min(correct, incorrect));
        Assert.True(ratio < 3.0, $"Час перевірки залежить від правильності пароля: відношення {ratio:F2}.");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4")]
    public void NeedsRehash_істинний_після_зміни_параметрів()
    {
        var current = _hasher.Hash(Password);
        Assert.False(_hasher.NeedsRehash(current));

        // Той самий формат, але ослаблені параметри — так виглядатиме рядок,
        // збережений до підняття вартості. Параметри лежать поруч із хешем саме
        // для того, щоб такий рядок можна було впізнати, а не мігрувати всіх.
        var parts = current.Split('.');
        var weaker = string.Join('.', parts[0], "1000", parts[2], parts[3]);

        Assert.True(_hasher.NeedsRehash(weaker));
        Assert.True(_hasher.Verify(Password, current));

        // Сміття теж «потребує перехешування»: інакше пошкоджений рядок тихо
        // лишився б назавжди.
        Assert.True(_hasher.NeedsRehash("не-хеш"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.11")]
    public void Пароль_не_потрапляє_в_текст_винятку()
    {
        var tooLong = new string('ж', 1024);

        var error = Assert.Throws<ArgumentException>(() => _hasher.Hash(tooLong));

        // ⛔ Ні повідомлення, ні стек не містять значення (ФВ-6.11). Виняток
        // потрапляє в лог і в трасування цілком, тому «показати, що прийшло»
        // тут дорівнює «записати пароль у файл».
        Assert.DoesNotContain(tooLong, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("256", error.Message, StringComparison.Ordinal);

        // Зіпсований хеш — це false, а не виняток: інакше один пошкоджений
        // рядок у базі перетворив би вхід усіх на помилку сервера.
        Assert.False(_hasher.Verify(Password, "1.210000.не-base64.теж-ні"));
        Assert.False(_hasher.Verify(Password, string.Empty));
    }

    /// <summary>Медіана з кількох вимірів; одне влучання GC не має вирішувати.</summary>
    private static long Median(Func<bool> action)
    {
        var samples = new long[5];
        for (var i = 0; i < samples.Length; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.ElapsedTicks;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }
}
