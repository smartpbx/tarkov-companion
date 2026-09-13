using System.Text;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class AppendedLineReaderTests
{
    /// <summary>
    /// The case that was broken, and the reason this class exists.
    /// </summary>
    /// <remarks>
    /// Every group notification and every userMatchOver is multi-kilobyte single-line JSON.
    /// StreamReader.ReadLineAsync handed back the unterminated first half as though it were a
    /// line and left the stream past it, so the remainder arrived as a second line: two
    /// fragments, both of which fail JsonDocument.Parse, and the event was gone with no rewind.
    /// </remarks>
    [Fact]
    public async Task ANotificationWrittenInTwoHalvesArrivesOnceAndWhole()
    {
        var clock = new Clock();
        var reader = new AppendedLineReader(clock, TimeSpan.FromSeconds(5));
        var file = new MemoryStream();
        var whole = """{"type":"groupMatchRaidReady","profileId":"abc","members":[{"Nickname":"Geo"}]}""";
        var half = whole.Length / 2;

        Append(file, whole[..half]);
        Assert.Empty(await reader.ReadAsync("app.log", file, default));

        Append(file, whole[half..] + "\n");
        var lines = await reader.ReadAsync("app.log", file, default);

        Assert.Equal([whole], lines);
    }

    /// <summary>A character split across two polls is rejoined, not replaced.</summary>
    /// <remarks>
    /// Why the split is done on bytes and only complete lines are decoded. Decoding each read
    /// separately would turn a multi-byte character cut down the middle into two replacement
    /// characters, which is a corrupted line that parses rather than a missing one that does
    /// not — the worse of the two failures.
    /// </remarks>
    [Fact]
    public async Task AMultiByteCharacterSplitBetweenPollsIsRejoined()
    {
        var reader = new AppendedLineReader(new Clock(), TimeSpan.FromSeconds(5));
        var file = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes("Раид окончен\n");
        // Mid-way through "Раид" — byte 3 sits inside a two-byte Cyrillic character.
        file.Write(bytes.AsSpan(0, 3));
        file.Position = 0;

        Assert.Empty(await reader.ReadAsync("app.log", file, default));

        file.Position = file.Length;
        file.Write(bytes.AsSpan(3));
        var lines = await reader.ReadAsync("app.log", file, default);

        Assert.Equal(["Раид окончен"], lines);
        Assert.DoesNotContain('�', lines[0]);
    }

    [Fact]
    public async Task CarriageReturnsAreNotPartOfTheLine()
    {
        var reader = new AppendedLineReader(new Clock());
        var file = new MemoryStream();
        Append(file, "first\r\nsecond\r\n");

        Assert.Equal(["first", "second"], await reader.ReadAsync("app.log", file, default));
    }

    /// <summary>
    /// A tail that has stopped growing is eventually taken as a whole line.
    /// </summary>
    /// <remarks>
    /// Otherwise the last line of a log the game has stopped writing — a rolled file, an ended
    /// session — is held forever and never parsed.
    /// </remarks>
    [Fact]
    public async Task AnUnterminatedTailIsFlushedOnceItStopsChanging()
    {
        var clock = new Clock();
        var reader = new AppendedLineReader(clock, TimeSpan.FromSeconds(5));
        var file = new MemoryStream();
        Append(file, "a finished line\nan unterminated one");

        Assert.Equal(["a finished line"], await reader.ReadAsync("app.log", file, default));

        // Still inside the window: it might be mid-write.
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(await reader.ReadAsync("app.log", file, default));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(["an unterminated one"], await reader.ReadAsync("app.log", file, default));

        // And only once.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(await reader.ReadAsync("app.log", file, default));
    }

    /// <summary>A line still being written restarts the clock rather than being cut.</summary>
    [Fact]
    public async Task ATailThatKeepsGrowingIsNeverFlushedEarly()
    {
        var clock = new Clock();
        var reader = new AppendedLineReader(clock, TimeSpan.FromSeconds(5));
        var file = new MemoryStream();

        Append(file, "{\"type\":");
        Assert.Empty(await reader.ReadAsync("app.log", file, default));

        for (var write = 0; write < 4; write++)
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            Append(file, "more,");
            Assert.Empty(await reader.ReadAsync("app.log", file, default));
        }

        Append(file, "}\n");
        Assert.Equal(["{\"type\":more,more,more,more,}"], await reader.ReadAsync("app.log", file, default));
    }

    /// <summary>A rolled file starts again rather than seeking past its end.</summary>
    [Fact]
    public async Task AFileThatShrankIsReadFromTheBeginning()
    {
        var reader = new AppendedLineReader(new Clock());
        var file = new MemoryStream();
        Append(file, "old line one\nold line two\n");
        Assert.Equal(2, (await reader.ReadAsync("app.log", file, default)).Count);

        var rolled = new MemoryStream();
        Append(rolled, "new\n");
        Assert.Equal(["new"], await reader.ReadAsync("app.log", rolled, default));
    }

    [Fact]
    public async Task StartingAtTheEndSkipsWhatIsAlreadyThere()
    {
        var reader = new AppendedLineReader(new Clock());
        var file = new MemoryStream();
        Append(file, "a session that already finished\n");
        reader.StartAtEnd("app.log", file.Length);

        Assert.Empty(await reader.ReadAsync("app.log", file, default));

        Append(file, "something new\n");
        Assert.Equal(["something new"], await reader.ReadAsync("app.log", file, default));
    }

    private static void Append(MemoryStream stream, string text)
    {
        stream.Position = stream.Length;
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
