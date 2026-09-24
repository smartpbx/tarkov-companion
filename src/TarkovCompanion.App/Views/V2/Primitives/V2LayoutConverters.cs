using Avalonia.Data.Converters;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>Small layout converters shared by V2 views.</summary>
public static class V2LayoutConverters
{
    /// <summary>
    /// [#314] One column while a neighbour is shown (true); the whole row, given as the
    /// parameter, while it is not. Intel's flea price sat in half the card even with no trader
    /// price beside it, and "Not sold on flea" only just fitted there in English.
    /// </summary>
    public static readonly IValueConverter SpanUnlessBeside =
        new FuncValueConverter<bool, object?, int>((beside, all) =>
            beside ? 1 : all is int span ? span : int.TryParse(all?.ToString(), out var parsed) ? parsed : 1);
}
