using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.App.Localization;

/// <summary>The after-raid card's words (#712 0-8): the one outcome question and the recap.</summary>
public static partial class DebriefText
{
    public static string AfterRaid => UiText.Get("Debrief.AfterRaid");
    public static string AfterRaidQuestion => UiText.Get("Debrief.AfterRaidQuestion");
    public static string AnswerLater => UiText.Get("Debrief.AnswerLater");
    public static string CloseAfterRaid => UiText.Get("Debrief.CloseAfterRaid");
    public static string OutcomeNotRecorded => UiText.Get("Debrief.OutcomeNotRecorded");
    public static string Recap => UiText.Get("Debrief.Recap");
    public static string RecapExtractUnknown => UiText.Get("Debrief.RecapExtractUnknown");
    public static string RecapNoLoot => UiText.Get("Debrief.RecapNoLoot");
    public static string RecapNoTasks => UiText.Get("Debrief.RecapNoTasks");

    /// <summary>The button label for one answer; the stored text stays fixed English (RaidOutcomeQuestion.StoredText).</summary>
    public static string Answer(RaidOutcomeBucket bucket) => bucket switch
    {
        RaidOutcomeBucket.Survived => UiText.Get("Debrief.AnswerSurvived"),
        RaidOutcomeBucket.Died => UiText.Get("Debrief.AnswerDied"),
        RaidOutcomeBucket.RunThrough => UiText.Get("Debrief.AnswerRanThrough"),
        RaidOutcomeBucket.Mia => UiText.Get("Debrief.AnswerMia"),
        _ => OutcomeNotRecorded,
    };

    public static string OutcomeAnswered(string outcome, string time) => UiText.Format("Debrief.OutcomeAnswered", outcome, time);
    public static string OutcomeAlreadyRecorded(string outcome) => UiText.Format("Debrief.OutcomeAlreadyRecorded", outcome);
    public static string OutcomeNotSaved(string reason) => UiText.Format("Debrief.OutcomeNotSaved", reason);
    public static string RaidEndedAt(string time) => UiText.Format("Debrief.RaidEndedAt", time);
    public static string RecapInRaid(string duration, string map) => UiText.Format("Debrief.RecapInRaid", duration, map);
    public static string RecapSide(string side) => UiText.Format("Debrief.RecapSide", side);
    public static string RecapRaidClock(double usedMinutes, double lengthMinutes) => UiText.Format("Debrief.RecapRaidClock", usedMinutes, lengthMinutes);
    public static string RecapExtractUsed(string extract) => UiText.Format("Debrief.RecapExtractUsed", extract);
    public static string RecapEndedNear(string extract) => UiText.Format("Debrief.RecapEndedNear", extract);
    public static string RecapLoot(string roubles, string scans) => UiText.Format("Debrief.RecapLoot", roubles, scans);
    public static string RecapTasks(string tasks) => UiText.Format("Debrief.RecapTasks", tasks);
    public static string RecapSquad(string names) => UiText.Format("Debrief.RecapSquad", names);
    public static string RecapMore(string shown, int more) => UiText.Format("Debrief.RecapMore", shown, more);
    public static string RecapHandIn(string quests) => UiText.Format("Debrief.RecapHandIn", quests);
}
