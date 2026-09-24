namespace TarkovCompanion.Core.Common;

/// <summary>
/// Holds the <see cref="Phrase"/> a record carries beside its fixed English, without taking part
/// in the record's equality (#314).
/// </summary>
/// <remarks>
/// A record compares every field. The words are said again from the same facts on every run, but
/// a record read back from storage or the relay has none, so a phrase that compared would make a
/// stored reason unequal to the fresh one it was saved from. This field always compares equal.
/// </remarks>
internal readonly struct PhraseAside(Phrase? value) : IEquatable<PhraseAside>
{
    public Phrase? Value { get; } = value;

    public bool Equals(PhraseAside other) => true;

    public override bool Equals(object? obj) => obj is PhraseAside;

    public override int GetHashCode() => 0;
}
