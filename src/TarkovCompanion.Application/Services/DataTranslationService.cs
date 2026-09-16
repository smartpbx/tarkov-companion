using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovCompanion.Application.Services;

public sealed class DataTranslationService
{
    public string Apply(string envelopeJson, string translationEnvelopeJson)
        => ApplyCore(envelopeJson, translationEnvelopeJson, maximumUtf8Bytes: null);

    /// <summary>Applies translations without allowing the merged document to exceed its transport budget.</summary>
    /// <remarks>
    /// The source and translation envelopes can each be within their individual limits while a
    /// short key repeated throughout the source expands to the same large translated value many
    /// times. A cumulative mutation budget bounds inserted DOM values, and a bounded writer then
    /// prevents the final merged string from crossing the same response limit.
    /// </remarks>
    public string Apply(string envelopeJson, string translationEnvelopeJson, long maximumUtf8Bytes)
    {
        if (maximumUtf8Bytes is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        }

        return ApplyCore(envelopeJson, translationEnvelopeJson, maximumUtf8Bytes);
    }

    private static string ApplyCore(
        string envelopeJson,
        string translationEnvelopeJson,
        long? maximumUtf8Bytes)
    {
        var envelope = JsonNode.Parse(envelopeJson) as JsonObject
            ?? throw new JsonException("The data envelope must be a JSON object.");
        if (!envelope.ContainsKey("data"))
        {
            throw new JsonException("The data envelope is missing the required 'data' property.");
        }

        var translationEnvelope = JsonNode.Parse(translationEnvelopeJson) as JsonObject
            ?? throw new JsonException("The translation envelope must be a JSON object.");
        var translations = translationEnvelope["data"] as JsonObject
            ?? throw new JsonException("The translation envelope is missing the required 'data' object.");

        if (envelope["translations"] is not JsonArray paths)
        {
            return Serialize(envelope, maximumUtf8Bytes);
        }

        var replacementBudget = maximumUtf8Bytes is null ? null : new TranslationReplacementBudget(maximumUtf8Bytes.Value);
        foreach (var pathNode in paths)
        {
            var path = pathNode?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            ApplyPath(envelope, ParsePath(path), 0, translations, replacementBudget);
        }

        return Serialize(envelope, maximumUtf8Bytes);
    }

    private static string Serialize(JsonObject envelope, long? maximumUtf8Bytes)
    {
        if (maximumUtf8Bytes is null)
        {
            return envelope.ToJsonString();
        }

        using var output = new BoundedWriteStream(maximumUtf8Bytes.Value);
        using (var writer = new Utf8JsonWriter(output))
        {
            envelope.WriteTo(writer);
        }

        return output.GetUtf8String();
    }

    private static void ApplyPath(
        JsonNode? node,
        IReadOnlyList<PathSegment> path,
        int index,
        JsonObject translations,
        TranslationReplacementBudget? replacementBudget)
    {
        if (node is null)
        {
            return;
        }

        if (index == path.Count)
        {
            return;
        }

        var segment = path[index];
        if (segment.Kind == PathSegmentKind.Property && node is JsonObject objectNode)
        {
            if (!objectNode.TryGetPropertyValue(segment.Name!, out var child) || child is null)
            {
                return;
            }

            if (index == path.Count - 1)
            {
                objectNode[segment.Name!] = TranslateValue(child, translations, replacementBudget);
                return;
            }

            ApplyPath(child, path, index + 1, translations, replacementBudget);
            return;
        }

        if (segment.Kind == PathSegmentKind.ObjectWildcard && node is JsonObject wildcardObject)
        {
            foreach (var property in wildcardObject.ToArray())
            {
                if (index == path.Count - 1)
                {
                    wildcardObject[property.Key] = TranslateValue(property.Value, translations, replacementBudget);
                }
                else
                {
                    ApplyPath(property.Value, path, index + 1, translations, replacementBudget);
                }
            }

            return;
        }

        if (segment.Kind == PathSegmentKind.ArrayWildcard && node is JsonArray array)
        {
            for (var itemIndex = 0; itemIndex < array.Count; itemIndex++)
            {
                if (index == path.Count - 1)
                {
                    array[itemIndex] = TranslateValue(array[itemIndex], translations, replacementBudget);
                }
                else
                {
                    ApplyPath(array[itemIndex], path, index + 1, translations, replacementBudget);
                }
            }
        }
    }

    private static JsonNode? TranslateValue(
        JsonNode? node,
        JsonObject translations,
        TranslationReplacementBudget? replacementBudget)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var translationKey))
        {
            return node;
        }

        // A blank translation is missing data, not a real name. json.tarkov.dev currently
        // maps one item's short name and thirty-one objective descriptions to "", and
        // substituting those blanked a required persistence field, which aborted the entire
        // item refresh and left the application with no item catalog at all. Keeping the
        // untranslated value is always more useful than an empty string.
        if (!translations.TryGetPropertyValue(translationKey, out var translated) ||
            translated is not JsonValue translatedValue ||
            !translatedValue.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return node;
        }

        replacementBudget?.Consume(text);
        return JsonValue.Create(text);
    }

    private static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported translation path '{path}'.");
        }

        var segments = new List<PathSegment>();
        foreach (var token in path[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "*")
            {
                segments.Add(new(PathSegmentKind.ObjectWildcard));
            }
            else if (token == "[*]")
            {
                segments.Add(new(PathSegmentKind.ArrayWildcard));
            }
            else if (token.EndsWith("[*]", StringComparison.Ordinal))
            {
                segments.Add(new(PathSegmentKind.Property, token[..^3]));
                segments.Add(new(PathSegmentKind.ArrayWildcard));
            }
            else
            {
                segments.Add(new(PathSegmentKind.Property, token));
            }
        }

        return segments;
    }

    private enum PathSegmentKind
    {
        Property,
        ObjectWildcard,
        ArrayWildcard,
    }

    private sealed record PathSegment(PathSegmentKind Kind, string? Name = null);

    /// <summary>
    /// Caps values inserted into the mutable DOM before final serialization gets a chance to
    /// enforce the output limit. The source and translation DOMs are each input-bounded; this
    /// third bound prevents one translation string repeated across many paths from multiplying
    /// resident replacement data without limit.
    /// </summary>
    private sealed class TranslationReplacementBudget(long maximumBytes)
    {
        private long _consumedBytes;

        public void Consume(string text)
        {
            // JsonEncodedText uses the same default escaping policy as Utf8JsonWriter. Include
            // the surrounding quotes so even empty replacements make bounded progress.
            var encodedBytes = checked(JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length + 2L);
            if (_consumedBytes > maximumBytes - encodedBytes)
            {
                throw new InvalidDataException(
                    $"The translated catalog response exceeds the {maximumBytes:N0}-byte response budget.");
            }

            _consumedBytes += encodedBytes;
        }
    }

    /// <summary>A write-only buffer that rejects the next chunk before it crosses the limit.</summary>
    private sealed class BoundedWriteStream(long maximumBytes) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public string GetUtf8String() => Encoding.UTF8.GetString(
            _buffer.GetBuffer(),
            0,
            checked((int)_buffer.Length));

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureFits(count);
            _buffer.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureFits(buffer.Length);
            _buffer.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void EnsureFits(int count)
        {
            if (_buffer.Length > maximumBytes - count)
            {
                throw new InvalidDataException(
                    $"The translated catalog response exceeds the {maximumBytes:N0}-byte response budget.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _buffer.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _buffer.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
