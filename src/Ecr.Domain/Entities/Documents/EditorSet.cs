// src/Ecr.Domain/Entities/Documents/EditorSet.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Іменований набір виконавців власника (EA-1, <c>doc.EditorSet</c>): склад запам'ятовується, щоб
/// повторити передачу наступного місяця одним кліком.
/// </summary>
/// <remarks>
/// ⚠ Набір — ЗНІМОК при застосуванні: зміна набору не змінює вже створені передачі мовчки
/// (<c>EditAssignment.AppliedSetId/AppliedSetName</c>). Не плутати з «Мої групи» (AD-діагностика).
/// </remarks>
public sealed class EditorSet : Entity<int>
{
    /// <summary>Стеля наборів на власника.</summary>
    public const int MaxPerOwner = 50;

    /// <summary>Стеля членів набору.</summary>
    public const int MaxMembers = 500;

    /// <summary>Межа довжини назви набору.</summary>
    public const int MaxNameLength = 100;

    private readonly List<EditorSetMember> _members = [];

    private EditorSet() { }

    /// <summary>Створює порожній набір власника.</summary>
    public EditorSet(int ownerUserId, string name, DateTime utcNow)
    {
        OwnerUserId = ownerUserId;
        Name = NormalizeName(name);
        CreatedAt = utcNow;
        ModifiedAt = utcNow;
    }

    /// <summary>Власник набору.</summary>
    public int OwnerUserId { get; private set; }

    /// <summary>Назва; унікальна в межах власника.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Коли створено (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Коли змінено (UTC).</summary>
    public DateTime ModifiedAt { get; private set; }

    /// <summary>Мітка оптимістичної конкурентності.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Члени набору.</summary>
    public IReadOnlyList<EditorSetMember> Members => _members;

    /// <summary>Додає члена; повторне додавання ігнорується. Повертає <c>true</c>, якщо додано.</summary>
    /// <exception cref="InvalidOperationException">Перевищено <see cref="MaxMembers"/>.</exception>
    public bool AddMember(int userId)
    {
        if (_members.Exists(m => m.UserId == userId))
        {
            return false;
        }

        if (_members.Count >= MaxMembers)
        {
            throw new InvalidOperationException($"У наборі не може бути більше {MaxMembers} членів.");
        }

        _members.Add(new EditorSetMember(Id, userId));
        return true;
    }

    /// <summary>Перейменовує набір (унікальність у межах власника — на стороні сховища).</summary>
    public void Rename(string name, DateTime utcNow)
    {
        Name = NormalizeName(name);
        ModifiedAt = utcNow;
    }

    /// <summary>Замінює склад набору (знімаються відсутні у <paramref name="userIds"/>, додаються нові).</summary>
    /// <exception cref="InvalidOperationException">Перевищено <see cref="MaxMembers"/>.</exception>
    public void ReplaceMembers(IEnumerable<int> userIds, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var ids = userIds.Distinct().ToList();
        if (ids.Count > MaxMembers)
        {
            throw new InvalidOperationException($"У наборі не може бути більше {MaxMembers} членів.");
        }

        _members.RemoveAll(m => !ids.Contains(m.UserId));
        foreach (var id in ids)
        {
            AddMember(id);
        }

        ModifiedAt = utcNow;
    }
    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Назва набору — 1..{MaxNameLength} символів без керувальних.", nameof(name));
        }

        return trimmed;
    }
}

/// <summary>Член набору виконавців (EA-1, <c>doc.EditorSetMember</c>).</summary>
public sealed class EditorSetMember
{
    private EditorSetMember() { }

    /// <summary>Створює члена набору.</summary>
    public EditorSetMember(int setId, int userId)
    {
        SetId = setId;
        UserId = userId;
    }

    /// <summary>Набір.</summary>
    public int SetId { get; private set; }

    /// <summary>Користувач (<c>sec.User</c>).</summary>
    public int UserId { get; private set; }
}
