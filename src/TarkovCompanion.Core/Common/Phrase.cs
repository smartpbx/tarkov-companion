namespace TarkovCompanion.Core.Common;

/// <summary>
/// Words the Application decides but does not say (#314): a code and its arguments. The App puts
/// it into the interface language through its string table.
/// </summary>
/// <remarks>
/// <para>
/// The code is a value of an enum marked <see cref="PhraseCodesAttribute"/>; the attribute names
/// the table's key prefix, so <c>LoadoutFinding.NoArmor</c> under prefix <c>Plan.Loadout.Finding</c>
/// is the key <c>Plan.Loadout.Finding.NoArmor</c>. Arguments are data the words are built around:
/// a number (formatted by the table's placeholder in the current culture), a name from the game
/// data (said as it is), another <see cref="Phrase"/> (said first), or a code of another marked enum.
/// </para>
/// <para>
/// A phrase is not stored or sent anywhere. Where a record that is persisted or relayed carries a
/// sentence, it keeps its fixed English beside the phrase, so what is stored never depends on the
/// language of the machine that stored it.
/// </para>
/// </remarks>
public sealed class Phrase : IEquatable<Phrase>
{
    public Phrase(Enum code, params object?[] arguments)
        : this(code, null, arguments)
    {
    }

    private Phrase(Enum code, long? count, object?[] arguments)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Count = count;
        Arguments = arguments ?? [];
    }

    public Enum Code { get; }

    /// <summary>Set when the words change with a count; the count is argument {0}, the rest follow it.</summary>
    public long? Count { get; }

    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>A phrase whose words change with <paramref name="count"/> ("1 raid", "3 raids").</summary>
    public static Phrase Counted(Enum code, long count, params object?[] arguments) => new(code, count, arguments);

    public bool Equals(Phrase? other) =>
        other is not null &&
        Equals(Code, other.Code) &&
        Count == other.Count &&
        Arguments.SequenceEqual(other.Arguments);

    public override bool Equals(object? obj) => Equals(obj as Phrase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Code);
        hash.Add(Count);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }

    /// <summary>For logs and test failures only; the player never reads this.</summary>
    public override string ToString() =>
        $"{Code.GetType().Name}.{Code}({string.Join(", ", Arguments)})";
}

/// <summary>Marks an enum whose values are <see cref="Phrase"/> codes, and names their key prefix in the App's string table.</summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class PhraseCodesAttribute(string keyPrefix) : Attribute
{
    public string KeyPrefix { get; } = keyPrefix;
}
