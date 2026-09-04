namespace Ecr.Application.Security;

/// <summary>Хешування паролів локальних облікових записів.</summary>
public interface IPasswordHasher
{
    /// <summary>Хешує пароль. Результат містить сіль і параметри алгоритму.</summary>
    string Hash(string password);

    /// <summary>Перевіряє пароль. Час виконання не має залежати від правильності.</summary>
    bool Verify(string password, string hash);

    /// <summary>Чи потрібно перехешувати через зміну параметрів алгоритму.</summary>
    bool NeedsRehash(string hash);
}
