using System.Collections.Concurrent;
using System.Reflection;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.Localization;

/// <summary>Says a <see cref="Phrase"/> in the interface language (#314).</summary>
public static class PhraseText
{
    private static readonly ConcurrentDictionary<Type, string?> Prefixes = new();

    /// <summary>The words for a phrase; nested phrases and marked codes among its arguments are said first.</summary>
    public static string Say(Phrase phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var key = Key(phrase.Code);
        var arguments = phrase.Arguments.Select(Argument).ToArray();
        return phrase.Count is { } count
            ? UiText.Plural(key, count, arguments)
            : UiText.Format(key, arguments);
    }

    /// <summary>The words for a code with no arguments.</summary>
    public static string Say(Enum code) => Say(new Phrase(code));

    /// <summary>Each phrase said, then joined by <paramref name="separator"/>.</summary>
    public static string Join(string separator, IEnumerable<Phrase> phrases) =>
        string.Join(separator, phrases.Select(Say));

    /// <summary>The table key for a code, e.g. <c>Plan.Loadout.Finding.NoArmor</c>.</summary>
    public static string Key(Enum code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var prefix = Prefix(code.GetType())
            ?? throw new ArgumentException($"{code.GetType().Name} is not marked [PhraseCodes].", nameof(code));
        return $"{prefix}.{code}";
    }

    /// <summary>The key prefix an enum is marked with, or null when it is not a phrase code.</summary>
    public static string? Prefix(Type type) =>
        Prefixes.GetOrAdd(type, static type => type.GetCustomAttribute<PhraseCodesAttribute>()?.KeyPrefix);

    private static object? Argument(object? argument) => argument switch
    {
        Phrase nested => Say(nested),
        Enum code when Prefix(code.GetType()) is not null => Say(code),
        _ => argument,
    };
}
