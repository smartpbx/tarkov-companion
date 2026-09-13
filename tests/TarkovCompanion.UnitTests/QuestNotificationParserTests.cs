using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Reading the game's own word about the player's own quests.
/// </summary>
/// <remarks>
/// Every quest on the page read Unknown because the stored progress was created empty and
/// nothing ever wrote to it again. The game has been announcing each quest starting, failing
/// and being handed in the whole time, in the same files the flea sales come from.
/// </remarks>
public sealed class QuestNotificationParserTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 13, 2, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, RecordedTaskState.Active)]
    [InlineData(11, RecordedTaskState.Failed)]
    [InlineData(12, RecordedTaskState.Completed)]
    public void TheThreeQuestMessagesAreRead(int messageType, RecordedTaskState expected)
    {
        var observation = QuestNotificationParser.ParseLine(Line(messageType), Observed);

        Assert.NotNull(observation);
        Assert.Equal("5936d90786f7742b1420ba5b", observation.TaskId);
        Assert.Equal(expected, observation.State);
        Assert.Equal("msg-1", observation.EventId);
        Assert.Equal(Observed, observation.ObservedUtc);
    }

    /// <summary>
    /// The same notification carries flea sales, insurance and ordinary chat.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(13)]
    public void EveryOtherMessageTypeIsLeftAlone(int messageType) =>
        Assert.Null(QuestNotificationParser.ParseLine(Line(messageType), Observed));

    /// <summary>
    /// A short first word is a system message, not a quest id.
    /// </summary>
    [Fact]
    public void ATemplateThatIsNotAQuestIdIsRejected() =>
        Assert.Null(QuestNotificationParser.ParseLine(Line(12, templateId: "welcome description"), Observed));

    [Fact]
    public void ALineThatIsNotAChatMessageIsRejectedWithoutParsing() =>
        Assert.Null(QuestNotificationParser.ParseLine(
            "2026-09-13 02:30:00.000 +00:00|NOTIFICATION|[{\"type\":\"RagfairOfferSold\",\"offerId\":\"o1\"}]",
            Observed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a log line at all")]
    [InlineData("ChatMessageReceived but no payload")]
    public void NonsenseIsNotAQuest(string? line) =>
        Assert.Null(QuestNotificationParser.ParseLine(line, Observed));

    /// <summary>A truncated notification must not interrupt observation.</summary>
    [Fact]
    public void ATruncatedPayloadIsIgnored() =>
        Assert.Null(QuestNotificationParser.ParseLine(
            "ChatMessageReceived|[{\"type\":\"new_message\",\"message\":{\"type\":12,\"templ",
            Observed));

    /// <summary>
    /// A message with no id of its own still has to be told apart from the next one.
    /// </summary>
    [Fact]
    public void AMessageWithoutAnIdGetsOneFromWhatItSays()
    {
        var observation = QuestNotificationParser.ParseLine(
            "ChatMessageReceived|[{\"type\":\"new_message\",\"message\":" +
            "{\"type\":12,\"templateId\":\"5936d90786f7742b1420ba5b done\"}}]",
            Observed);

        Assert.NotNull(observation);
        Assert.Equal("5936d90786f7742b1420ba5b:Completed", observation.EventId);
    }

    /// <summary>
    /// The message's own words are not read, because they are the same on all three.
    /// </summary>
    /// <remarks>
    /// Every one of these says "quest started", including the hand-in and the failure. So does
    /// the template id's suffix, which is "description" on a start, "successMessageText" on a
    /// completion, and "0" on a failure. Either would have marked every quest as started.
    /// </remarks>
    [Fact]
    public void AHandInThatSaysItStartedIsStillAHandIn()
    {
        var observation = QuestNotificationParser.ParseLine(
            Line(12, templateId: "5a27ba1c86f77461ea5a3c56 successMessageText"),
            Observed);

        Assert.NotNull(observation);
        Assert.Equal(RecordedTaskState.Completed, observation.State);
        Assert.Equal("5a27ba1c86f77461ea5a3c56", observation.TaskId);
    }

    /// <summary>A failure's template id ends in "0", which says nothing about the kind.</summary>
    [Fact]
    public void AFailureWithAMeaninglessSuffixIsStillAFailure()
    {
        var observation = QuestNotificationParser.ParseLine(
            Line(11, templateId: "68fa00bda5ef093c440fb0ba 0"),
            Observed);

        Assert.NotNull(observation);
        Assert.Equal(RecordedTaskState.Failed, observation.State);
        Assert.Equal("68fa00bda5ef093c440fb0ba", observation.TaskId);
    }

    /// <summary>
    /// A notification line shaped like the ones the game writes.
    /// </summary>
    /// <remarks>
    /// Built by concatenation rather than as a raw interpolated string. The payload ends in
    /// two closing braces of its own, which a raw string reads as the end of an interpolation
    /// and refuses to compile.
    /// </remarks>
    /// <summary>
    /// A notification line shaped like the ones the game actually writes.
    /// </summary>
    /// <remarks>
    /// The line announces itself as ChatMessageReceived and the payload inside calls itself
    /// "new_message". That difference is what an earlier version of the parser got wrong, so
    /// the fixture carries both exactly as the logs do.
    ///
    /// Built by concatenation rather than as a raw interpolated string, because the payload
    /// ends in two closing braces of its own and a raw string reads those as the end of an
    /// interpolation.
    /// </remarks>
    private static string Line(int messageType, string templateId = "5936d90786f7742b1420ba5b description") =>
        "2026-09-13 02:30:00.000 +00:00|NOTIFICATION|6aa4bd43d4a840ddb8130198|ChatMessageReceived|" +
        "[{\"type\":\"new_message\",\"eventId\":\"e1\",\"dialogId\":\"5935c25fb3acc3127c3d8cd9\"," +
        "\"message\":{\"_id\":\"msg-1\",\"uid\":\"5935c25fb3acc3127c3d8cd9\",\"type\":" +
        messageType.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"text\":\"quest started\",\"templateId\":\"" + templateId + "\"}}]";
}
