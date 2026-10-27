using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class HotkeyTextTests
{
    [Theory]
    [InlineData("Alt+Shift+C", 0x1u | 0x4u, (uint)'C')]
    [InlineData("ctrl + alt + 9", 0x2u | 0x1u, (uint)'9')]
    [InlineData("F9", 0u, 0x78u)]
    [InlineData("Win+F12", 0x8u, 0x7Bu)]
    [InlineData("Shift+PageUp", 0x4u, 0x21u)]
    public void Parses(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal((modifiers, key), (m, k));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C")]          // would fire while typing
    [InlineData("Alt+Shift")]  // no key
    [InlineData("Alt+C+D")]    // two keys
    [InlineData("Alt+Banana")]
    [InlineData("F25")]
    public void Rejects(string text) => Assert.False(HotkeyText.TryParse(text, out _, out _));
}
