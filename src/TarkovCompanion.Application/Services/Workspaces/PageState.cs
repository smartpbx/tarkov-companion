namespace TarkovCompanion.Application.Services.Workspaces;

/// <summary>
/// [#902 P8] One page's remembered filters, chips and sorts, kept under one workspace-layout key.
/// </summary>
/// <remarks>
/// Every page used to hold its choices in private fields that started from a hard-coded default,
/// so a chip set on Keys was gone after a visit to Raid, and every choice was gone after a restart.
/// One key per page (<c>page.keys</c> = <c>filter=Sell;sort=Price</c>) keeps the layout store far
/// below its entry cap, where a key per control would not. The value is read back from the store
/// on every write, so two owners of one page (the Loot Scan's risk and its verdict chip) never
/// overwrite each other's field. A missing field is the default: a field set back to its default
/// is removed, so a later change of default reaches everybody who never touched it. Free-text
/// search is never stored here.
/// </remarks>
public sealed class PageState
{
    private readonly IWorkspaceLayoutStore? _store;

    public PageState(IWorkspaceLayoutStore? store, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _store = store;
        Key = key;
    }

    public string Key { get; }

    public string? Get(string field) => Parse(_store?.Get(Key)).GetValueOrDefault(field);

    /// <summary>Stores a field; null removes it, which means its default.</summary>
    public void Set(string field, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (_store is null)
        {
            return;
        }

        var fields = new Dictionary<string, string>(Parse(_store.Get(Key)), StringComparer.Ordinal);
        if (value is null)
        {
            fields.Remove(field);
        }
        else
        {
            fields[field] = value;
        }

        _store.Set(Key, Format(fields));
    }

    public TEnum Enum<TEnum>(string field, TEnum fallback)
        where TEnum : struct, Enum =>
        System.Enum.TryParse<TEnum>(Get(field), ignoreCase: false, out var value) &&
        System.Enum.IsDefined(value) &&
        !int.TryParse(Get(field), out _)
            ? value
            : fallback;

    public void SetEnum<TEnum>(string field, TEnum value, TEnum fallback)
        where TEnum : struct, Enum =>
        Set(field, EqualityComparer<TEnum>.Default.Equals(value, fallback) ? null : value.ToString());

    public bool Bool(string field, bool fallback) => Get(field) switch
    {
        "on" => true,
        "off" => false,
        _ => fallback,
    };

    public void SetBool(string field, bool value, bool fallback) =>
        Set(field, value == fallback ? null : value ? "on" : "off");

    public int Int(string field, int fallback, int minimum = int.MinValue, int maximum = int.MaxValue) =>
        int.TryParse(Get(field), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        value >= minimum && value <= maximum
            ? value
            : fallback;

    public void SetInt(string field, int value, int fallback) =>
        Set(field, value == fallback ? null : value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Reads <c>a=1;b=two</c>. Anything malformed is dropped rather than failing the page.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? stored)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(stored))
        {
            return fields;
        }

        foreach (var pair in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            try
            {
                fields[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
            catch (UriFormatException)
            {
                // A hand-edited file; the field falls back to its default.
            }
        }

        return fields;
    }

    public static string Format(IReadOnlyDictionary<string, string> fields) => string.Join(
        ';',
        fields
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
}
