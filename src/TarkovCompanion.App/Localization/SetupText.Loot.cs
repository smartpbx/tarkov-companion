namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string CoverageLootTitle => UiText.Get("Setup.Coverage.LootTitle");
    public static string CoverageLootNoData => UiText.Get("Setup.Coverage.LootNoData");
    public static string CoverageLootSummary(int positioned, int published, object? dataThrough, object? imported) => UiText.Format("Setup.Coverage.LootSummary", positioned, published, dataThrough, imported);
    public static string CoverageLootFailed(object? reason) => UiText.Format("Setup.Coverage.LootFailed", reason);
    public static string CoverageLootNoSnapshot => UiText.Get("Setup.Coverage.LootNoSnapshot");
    public static string CoverageLootLocalOnly => UiText.Get("Setup.Coverage.LootLocalOnly");
    public static string CoverageLootLastError(object? when, object? reason) => UiText.Format("Setup.Coverage.LootLastError", when, reason);
    public static string CoverageLootPositioned(int positioned, int published) => UiText.Format("Setup.Coverage.LootPositioned", positioned, published);
    public static string CoverageLootOnFloor(int count) => UiText.Format("Setup.Coverage.LootOnFloor", count);
    public static string CoverageLootMapOnly(int count) => UiText.Format("Setup.Coverage.LootMapOnly", count);
    public static string CoverageLootLeftOut(int count) => UiText.Format("Setup.Coverage.LootLeftOut", count);
    public static string LootScanReturnHeading => UiText.Get("Setup.LootScan.ReturnHeading");
    public static string LootScanReturnHint => UiText.Get("Setup.LootScan.ReturnHint");
    public static string LootScanTabletOnlyLabel => UiText.Get("Setup.LootScan.TabletOnlyLabel");
    public static string LootScanTabletOnlyHint => UiText.Get("Setup.LootScan.TabletOnlyHint");
    public static string LootScanLastHeading => UiText.Get("Setup.LootScan.LastHeading");
    public static string LootScanLastSummary(object? duration, object? time) => UiText.Format("Setup.LootScan.LastSummary", duration, time);
    public static string LootScanNoneYet => UiText.Get("Setup.LootScan.NoneYet");
    public static string LootScanReturnSeconds(int seconds) => UiText.Format("Setup.LootScan.ReturnSeconds", seconds);
    public static string LootScanReturnOff => UiText.Get("Setup.LootScan.ReturnOff");
}
