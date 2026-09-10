using TarkovCompanion.Core.Domain.Input;

namespace TarkovCompanion.UnitTests;

public sealed class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+S", "Ctrl + Alt + S")]
    [InlineData("ctrl + shift + f9", "Ctrl + Shift + F9")]
    [InlineData("Control-Alt-NumPad5", "Ctrl + Alt + NumPad5")]
    [InlineData("Win+Shift+D", "Shift + Win + D")]
    [InlineData("F13", "F13")]
    public void ParsesForgivinglyAndFormatsCanonically(string text, string expected)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding));
        Assert.Equal(expected, binding.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+NotAKey")]
    [InlineData("Ctrl+S+D")]
    public void RejectsTextThatDoesNotNameOneKey(string text) =>
        Assert.False(HotkeyBinding.TryParse(text, out _));

    [Fact]
    public void RoundTripsThroughItsOwnDisplayText()
    {
        var original = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "F9");

        Assert.True(HotkeyBinding.TryParse(original.DisplayName, out var reparsed));
        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void RequiresAModifierForOrdinaryKeys()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.None, "S");

        Assert.False(binding.IsValid(out var reason));
        Assert.Contains("modifier", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsAnUnmodifiedFunctionKey()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.None, "F9");

        Assert.True(binding.IsValid(out _));
    }

    [Fact]
    public void RejectsWindowsOnItsOwn()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Windows, "S");

        Assert.False(binding.IsValid(out var reason));
        Assert.Contains("reserved", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildsAGestureWithTheExpectedModifierAndKeyCodes()
    {
        var gesture = new HotkeyBinding(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            "S").ToGesture();

        // MOD_NOREPEAT | MOD_SHIFT | MOD_CONTROL | MOD_ALT, and 'S'.
        Assert.Equal(0x4000u | 0x0004u | 0x0002u | 0x0001u, gesture.Modifiers);
        Assert.Equal((uint)'S', gesture.VirtualKey);
        Assert.Equal("Ctrl + Alt + Shift + S", gesture.DisplayName);
    }

    [Fact]
    public void NeverRepeatsWhileTheKeyIsHeld()
    {
        // A held shortcut must not queue a scan per keyboard repeat.
        var gesture = HotkeyBinding.DefaultScan.ToGesture();

        Assert.Equal(0x4000u, gesture.Modifiers & 0x4000u);
    }

    [Fact]
    public void RefusesToBuildAGestureFromAnInvalidBinding() =>
        Assert.Throws<InvalidOperationException>(() => new HotkeyBinding(HotkeyModifiers.None, "S").ToGesture());

    [Fact]
    public void DefaultScanShortcutIsUsable()
    {
        Assert.True(HotkeyBinding.DefaultScan.IsValid(out _));
        Assert.Equal("Ctrl + Alt + S", HotkeyBinding.DefaultScan.DisplayName);
    }

    [Fact]
    public void EveryOfferedKeyResolvesToADistinctCode()
    {
        var codes = new Dictionary<uint, string>();
        foreach (var key in HotkeyBinding.AvailableKeys)
        {
            Assert.True(HotkeyKeys.TryGetVirtualKey(key, out var code), key);
            Assert.False(codes.TryGetValue(code, out var existing), $"{key} collides with {existing}.");
            codes[code] = key;
        }

        Assert.NotEmpty(codes);
    }
}
