namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed partial class V2SetupWorkspaceViewModel
{
    /// <summary>[#314] Setup › Diagnostics' feature flags, or null in a shell built without them.</summary>
    public SetupFeatureFlagsViewModel? FeatureFlags { get; private set; }

    public bool HasFeatureFlags => FeatureFlags is not null;

    /// <summary>Hands this page the feature flags, for the reason AttachSelfTest gives.</summary>
    public void AttachFeatureFlags(SetupFeatureFlagsViewModel featureFlags)
    {
        FeatureFlags = featureFlags ?? throw new ArgumentNullException(nameof(featureFlags));
        OnPropertyChanged(nameof(FeatureFlags));
        OnPropertyChanged(nameof(HasFeatureFlags));
    }
}
