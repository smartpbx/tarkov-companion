using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// A deliberately disconnected native gallery. Keeping it outside the legacy shell prevents the
/// root-scale workaround from becoming an accidental requirement for V2 primitives.
/// </summary>
public sealed partial class V2PrimitiveGallery : UserControl
{
    public V2PrimitiveGallery() => AvaloniaXamlLoader.Load(this);
}
