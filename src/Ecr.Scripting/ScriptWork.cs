namespace Ecr.Scripting;

/// <summary>
/// Лічильник роботи компілятора для детермінованих тестів лінійності: кожен крок лексера, парсера й опускання виразів
/// та кожне звернення binder'а до словника/множини (перевірка імені, дубль таблиці, дубль поля тощо) додає одиницю.
/// Замість настінного часу (хиткий на навантаженому раннері) тест порівнює роботу на вході N і 2N: лінійний алгоритм
/// дає ~2×, квадратичний — ~4×. Лічильник по потоку (компіляція синхронна), тож паралельні тести не заважають одне одному.
/// </summary>
internal static class ScriptWork
{
    [ThreadStatic]
    private static long steps;

    /// <summary>Накопичена робота поточного потока.</summary>
    internal static long Steps => steps;

    /// <summary>Додає одиницю роботи.</summary>
    internal static void Step() => steps++;

    /// <summary>Додає <paramref name="count"/> одиниць роботи (там, де алгоритм робить кілька порівнянь за раз).</summary>
    internal static void Add(long count) => steps += count;

    /// <summary>Скидає лічильник поточного потока.</summary>
    internal static void Reset() => steps = 0;
}
