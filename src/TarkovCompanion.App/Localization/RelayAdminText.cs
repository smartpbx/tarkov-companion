namespace TarkovCompanion.App.Localization;

/// <summary>[#920] The relay owner's admin panel in Team › Devices, from Localization/Strings.</summary>
public static class RelayAdminText
{
    public static string Heading => UiText.Get("RelayAdmin.Heading");
    public static string Hint => UiText.Get("RelayAdmin.Hint");
    public static string Refresh => UiText.Get("RelayAdmin.Refresh");
    public static string ClearDrawings => UiText.Get("RelayAdmin.ClearDrawings");
    public static string ClearMarks => UiText.Get("RelayAdmin.ClearMarks");
    public static string ResetRoom => UiText.Get("RelayAdmin.ResetRoom");
    public static string Remove => UiText.Get("RelayAdmin.Remove");
    public static string Confirm => UiText.Get("RelayAdmin.Confirm");
    public static string Cancel => UiText.Get("RelayAdmin.Cancel");
    public static string NoRooms => UiText.Get("RelayAdmin.NoRooms");
    public static string NoActivity => UiText.Get("RelayAdmin.NoActivity");
    public static string NoMembers => UiText.Get("RelayAdmin.NoMembers");
    public static string NotOwner => UiText.Get("RelayAdmin.NotOwner");
    public static string Unsupported => UiText.Get("RelayAdmin.Unsupported");
    public static string Unreachable => UiText.Get("RelayAdmin.Unreachable");
    public static string Loading => UiText.Get("RelayAdmin.Loading");
    public static string RoomNamed(string hashStart) => UiText.Format("RelayAdmin.RoomNamed", hashStart);
    public static string Refused(int status) => UiText.Format("RelayAdmin.Refused", status);
    public static string Done(int cleared) => UiText.Plural("RelayAdmin.Done", cleared, cleared);
    public static string Members(int count) => UiText.Plural("RelayAdmin.Members", count, count);
    public static string Marks(int count) => UiText.Plural("RelayAdmin.Marks", count, count);
    public static string Drawings(int count) => UiText.Plural("RelayAdmin.Drawings", count, count);
    public static string LastActivity(string ago) => UiText.Format("RelayAdmin.LastActivity", ago);
    public static string RemovedNames(string names) => UiText.Format("RelayAdmin.RemovedNames", names);
    public static string ConfirmClearDrawings(string room) => UiText.Format("RelayAdmin.ConfirmClearDrawings", room);
    public static string ConfirmClearMemberDrawings(string member) => UiText.Format("RelayAdmin.ConfirmClearMemberDrawings", member);
    public static string ConfirmClearMarks(string room) => UiText.Format("RelayAdmin.ConfirmClearMarks", room);
    public static string ConfirmRemove(string member) => UiText.Format("RelayAdmin.ConfirmRemove", member);
    public static string ConfirmReset(string room) => UiText.Format("RelayAdmin.ConfirmReset", room);
}
