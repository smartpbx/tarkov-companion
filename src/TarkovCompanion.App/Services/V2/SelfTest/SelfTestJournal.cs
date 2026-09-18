namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// The last self-test result, kept so a problem report can carry it.
/// </summary>
/// <remarks>
/// A single value rather than a history. The question a support conversation asks is "what did
/// it say when you pressed it", and keeping every run would mean deciding which one to send.
///
/// Held here rather than on the view model because the two things that need it sit on opposite
/// sides of the shell: Setup's self-test panel writes it, and Copy diagnostics — which belongs
/// to the V1 Settings view model the whole application still shares — reads it.
/// </remarks>
public sealed class SelfTestJournal
{
    private SelfTestSummary? _last;

    public SelfTestSummary? Last => Volatile.Read(ref _last);

    public void Record(SelfTestSummary summary) =>
        Volatile.Write(ref _last, summary ?? throw new ArgumentNullException(nameof(summary)));
}
