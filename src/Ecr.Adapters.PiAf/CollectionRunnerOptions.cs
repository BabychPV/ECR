using System.Globalization;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Налаштування збирача (D-462): довжина шматка читання і видалення сирих точок, яких більше немає в джерелі.
/// </summary>
/// <param name="ChunkSize">
/// Довжина шматка, на які ріжеться кожен інтервал читання; <c>null</c> — інтервал читається цілком (як до D-462).
/// </param>
/// <param name="DeleteMissingPoints">
/// Чи видаляти сирі точки, яких джерело не віддало за ПОВНІСТЮ прочитаний шматок запитаного діапазону.
/// </param>
/// <remarks>
/// ⚠ Ключі читає <c>Program.cs</c> і передає в <see cref="DependencyInjection.AddPiAfAdapters"/>: адаптер не
/// бере залежності від конфігурації, як і решта його налаштувань (секрети — за іменем, запити — через
/// <c>ISecretProvider</c>).
/// </remarks>
public sealed record CollectionRunnerOptions(TimeSpan? ChunkSize, bool DeleteMissingPoints)
{
    /// <summary>Ключ довжини шматка в добах; <c>0</c> — без нарізки.</summary>
    public const string ChunkDaysKey = "Collection:ChunkDays";

    /// <summary>Ключ перемикача видалення зниклих точок.</summary>
    public const string DeleteMissingPointsKey = "Collection:DeleteMissingPoints";

    /// <summary>Типова довжина шматка — 7 діб (рішення власника продукту, D-462).</summary>
    /// <remarks>
    /// ⛔ Нарізка НЕ змінює кількості запитів звичайного прогону: діапазон, що вміщається в шматок, читається
    /// одним інтервалом, як до D-462. Сім діб — типове перекриття розкладу (<c>LookbackDays</c> = 7), тож плановий
    /// прогін за замовчуванням — один запит на тег. Нарізаються лише довші діапазони (ручний збір за місяці,
    /// довга прогалина) — заради продовження з місця зупинки. Розклад із <c>LookbackDays</c> понад
    /// <c>Collection:ChunkDays</c> нарізався б — для нього треба підняти ключ.
    /// </remarks>
    public const int DefaultChunkDays = 7;

    /// <summary>
    /// Стеля міток одного атрибута в одному шматку, за якої видалення зниклих точок ще ведеться.
    /// </summary>
    /// <remarks>
    /// Мільйон міток — 200 сторінок (<see cref="CollectionRunner.DefaultMaxPagesPerRead"/>), тобто вся стеля
    /// читання атрибута за прогін; понад неї шматок не видаляє нічого.
    /// </remarks>
    public const int MaxTrackedTimestamps = 1_000_000;

    /// <summary>Поведінка до D-462: без нарізки і без видалення.</summary>
    public static CollectionRunnerOptions Off { get; } = new(null, false);

    /// <summary>Типові значення продукту: доба, видалення ввімкнено.</summary>
    public static CollectionRunnerOptions Default { get; } =
        new(TimeSpan.FromDays(DefaultChunkDays), DeleteMissingPoints: true);

    /// <summary>Налаштування з рядкових значень конфігурації; порожнє — типове.</summary>
    /// <param name="chunkDays">Значення <see cref="ChunkDaysKey"/>.</param>
    /// <param name="deleteMissingPoints">Значення <see cref="DeleteMissingPointsKey"/>.</param>
    /// <returns>Налаштування збирача.</returns>
    /// <exception cref="FormatException">Значення не читається (перевірку старту обійдено).</exception>
    public static CollectionRunnerOptions From(string? chunkDays, string? deleteMissingPoints)
    {
        var days = string.IsNullOrWhiteSpace(chunkDays)
            ? DefaultChunkDays
            : int.Parse(chunkDays, NumberStyles.Integer, CultureInfo.InvariantCulture);

        var delete = string.IsNullOrWhiteSpace(deleteMissingPoints) || bool.Parse(deleteMissingPoints);

        return new(days > 0 ? TimeSpan.FromDays(days) : null, delete);
    }
}
