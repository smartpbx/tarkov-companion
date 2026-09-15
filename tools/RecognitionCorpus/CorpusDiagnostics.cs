namespace TarkovCompanion.RecognitionCorpus;

/// <summary>
/// Everything this tool prints reaches a terminal, a shell transcript, or CI output. The CLI used
/// to print the message of any caught I/O exception, and the runtime's message names the path it
/// failed on, so a manifest locked by another process reached stderr as "The process cannot
/// access the file '/home/.../manifest.json'". A hostile document can do the same through an id,
/// an enum value, or a property name that a diagnostic echoes. So an exception's own text is printed only when this
/// tool wrote it and marked it path-free; every other failure becomes a fixed message, and
/// document content enters a diagnostic only once it already has the shape of an opaque id,
/// token, or identifier.
/// </summary>
public static class CorpusDiagnostics
{
    public const string NotFoundMessage = "A recognition corpus file or directory does not exist.";
    public const string AccessDeniedMessage = "Access to a recognition corpus file or directory was denied.";
    public const string PathTooLongMessage = "A recognition corpus path is too long.";
    public const string InputOutputMessage = "A recognition corpus file could not be read or written.";
    public const string InvalidArgumentMessage = "A recognition corpus argument or path is invalid.";
    public const string UnexpectedMessage = "The recognition corpus command failed unexpectedly.";

    private const string PathFreeKey = "TarkovCompanion.RecognitionCorpus.PathFree";

    /// <summary>The single line the CLI prints for a failure; never a runtime message.</summary>
    public static string DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[PathFreeKey] is true
            ? exception.Message
            : exception switch
            {
                FileNotFoundException or DirectoryNotFoundException => NotFoundMessage,
                UnauthorizedAccessException => AccessDeniedMessage,
                PathTooLongException => PathTooLongMessage,
                IOException => InputOutputMessage,
                ArgumentException or NotSupportedException => InvalidArgumentMessage,
                _ => UnexpectedMessage,
            };
    }

    /// <summary>Marks an exception whose message this tool composed from fixed text and sanitized values.</summary>
    internal static TException PathFree<TException>(TException exception) where TException : Exception
    {
        exception.Data[PathFreeKey] = true;
        return exception;
    }

    /// <summary>An id is echoed only when it is already opaque; anything else could be a name or a path.</summary>
    internal static string Id(string? value) => CorpusValidation.OpaqueId(value) ? value! : "<non-opaque id>";

    /// <summary>An enum-like value is echoed only as a short lowercase token.</summary>
    internal static string Token(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? value
            : "<unrecognized value>";

    /// <summary>A property name is echoed in a JSON path only as a short ASCII identifier.</summary>
    internal static string PropertySegment(string name) =>
        name is { Length: >= 1 and <= 64 } && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? name
            : "<property>";
}
