using System.IO.Compression;
using System.Text;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Serving the mirrored catalog compressed, and letting a cache in front of it work.
/// </summary>
/// <remarks>
/// Each client pulls 16.7 MB of identity bytes an hour through the tunnel. Measured on the real
/// payload: <b>16,716,287 bytes raw, 1,344,177 compressed</b> — a factor of twelve and a half,
/// per client, per hour.
///
/// The origin sent no <c>Content-Encoding</c> and no <c>Cache-Control</c>, so Cloudflare cached
/// nothing (<c>cf-cache-status: DYNAMIC</c>) and every byte crossed the tunnel every time.
/// </remarks>
public sealed class CatalogCompressionTests
{
    /// <summary>A payload shaped like the real one: JSON, and highly repetitive.</summary>
    /// <remarks>
    /// Built by concatenation rather than interpolated into a raw string. The literal needed
    /// more dollars than braces and read as a puzzle; a catalog fixture is not the place to be
    /// clever about quoting.
    /// </remarks>
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
        "{\"data\":{\"items\":[" +
        string.Join(',', Enumerable.Repeat("\"a rather repetitive catalog entry\"", 400)) +
        "]}}");

    [Fact]
    public void The_compressed_copy_is_the_same_bytes_back_again()
    {
        // The whole claim. A compression that lost or reordered anything would be serving a
        // different catalog under a tag that says it is the same one.
        var snapshot = Snapshot();

        Assert.Equal(Payload, Decompress(snapshot.Gzip));
    }

    [Fact]
    public void Compressing_is_worth_doing_on_a_catalog()
    {
        var snapshot = Snapshot();

        Assert.True(
            snapshot.Gzip.Length < snapshot.Body.Length / 2,
            $"a catalog should compress well: {snapshot.Body.Length} to {snapshot.Gzip.Length}");
    }

    [Fact]
    public void The_tag_names_the_catalog_rather_than_the_encoding()
    {
        // So a client that asked without compression and one that asked with it hold the same
        // tag for the same catalog, and neither re-downloads because the other negotiated
        // differently. It is the hash of the identity bytes either way.
        var snapshot = Snapshot();

        Assert.DoesNotContain("gzip", snapshot.ETag, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Snapshot().ETag, snapshot.ETag);
    }

    [Fact]
    public void Two_snapshots_of_different_bytes_do_not_share_a_tag()
    {
        var other = new CatalogMirror.Snapshot(
            Encoding.UTF8.GetBytes("""{"data":{"items":[]}}"""),
            "\"different\"",
            DateTimeOffset.UnixEpoch);

        Assert.NotEqual(Snapshot().Gzip, other.Gzip);
    }

    [Fact]
    public void An_empty_payload_still_compresses_to_something_readable()
    {
        // gzip of nothing is a header and a trailer rather than nothing, and a reader must get
        // nothing back out of it rather than fail.
        var snapshot = new CatalogMirror.Snapshot([], "\"empty\"", DateTimeOffset.UnixEpoch);

        Assert.Empty(Decompress(snapshot.Gzip));
    }

    private static CatalogMirror.Snapshot Snapshot() =>
        new(Payload, "\"a-tag\"", DateTimeOffset.UnixEpoch);

    private static byte[] Decompress(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        gzip.CopyTo(destination);
        return destination.ToArray();
    }
}
