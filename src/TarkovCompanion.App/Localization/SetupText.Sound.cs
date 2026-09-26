namespace TarkovCompanion.App.Localization;

/// <summary>[#712 0-10] Setup › Notifications › Sound, and the lines sound speaks.</summary>
public static partial class SetupText
{
    public static string SoundHeading => UiText.Get("Setup.Sound.Heading");
    public static string SoundIntro => UiText.Get("Setup.Sound.Intro");
    public static string SoundMaster => UiText.Get("Setup.Sound.Master");
    public static string SoundMasterHint => UiText.Get("Setup.Sound.MasterHint");
    public static string SoundUnavailable => UiText.Get("Setup.Sound.Unavailable");
    public static string SoundCueDeadline => UiText.Get("Setup.Sound.CueDeadline");
    public static string SoundCueDeadlineHint => UiText.Get("Setup.Sound.CueDeadlineHint");
    public static string SoundCuePing => UiText.Get("Setup.Sound.CuePing");
    public static string SoundCuePingHint => UiText.Get("Setup.Sound.CuePingHint");
    public static string SoundCueLoot => UiText.Get("Setup.Sound.CueLoot");
    public static string SoundCueLootHint => UiText.Get("Setup.Sound.CueLootHint");
    public static string SoundCueOutcome => UiText.Get("Setup.Sound.CueOutcome");
    public static string SoundCueOutcomeHint => UiText.Get("Setup.Sound.CueOutcomeHint");
    public static string SoundSpeak => UiText.Get("Setup.Sound.Speak");
    public static string SoundSpeakHint => UiText.Get("Setup.Sound.SpeakHint");
    public static string SoundVolume => UiText.Get("Setup.Sound.Volume");
    public static string SoundVolumePercent(int percent) => UiText.Format("Setup.Sound.VolumePercent", percent);
    public static string SoundDevice => UiText.Get("Setup.Sound.Device");
    public static string SoundDefaultDevice => UiText.Get("Setup.Sound.DefaultDevice");
    public static string SoundTest => UiText.Get("Setup.Sound.Test");
    public static string SoundTestLine => UiText.Get("Setup.Sound.TestLine");
    public static string SoundLineKeep(object? name) => UiText.Format("Sound.Line.Keep", name);
    public static string SoundLineSellFlea(object? name) => UiText.Format("Sound.Line.SellFlea", name);
    public static string SoundLineSellTrader(object? name) => UiText.Format("Sound.Line.SellTrader", name);
    public static string SoundLineLeave(object? name) => UiText.Format("Sound.Line.Leave", name);
    public static string SoundLineUse(object? name) => UiText.Format("Sound.Line.Use", name);
    public static string SoundLineDoNotUse(object? name) => UiText.Format("Sound.Line.DoNotUse", name);
    public static string SoundLineItems(int count) => UiText.Format("Sound.Line.Items", count);
    public static string SoundLineWithValue(object? what, object? value) => UiText.Format("Sound.Line.WithValue", what, value);
    public static string SoundLineNoValue(object? what) => UiText.Format("Sound.Line.NoValue", what);
    public static string SoundMoneyMillions(object? amount) => UiText.Format("Sound.Money.Millions", amount);
    public static string SoundMoneyThousands(object? amount) => UiText.Format("Sound.Money.Thousands", amount);
    public static string SoundMoneyRoubles(object? amount) => UiText.Format("Sound.Money.Roubles", amount);
}
