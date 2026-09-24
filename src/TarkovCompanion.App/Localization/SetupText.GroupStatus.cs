using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] The group's status line: the service's code in words, or a fixture's own line.</summary>
    public static string GroupStatus(GroupSnapshot group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Status is { } status ? PhraseText.Say(status) : group.Detail;
    }
}
