using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Filee.App.Tests;

public class LocalizationResourceTests
{
    [AvaloniaFact]
    public void Strings_are_available_as_application_resources()
    {
        TestServices.EnsureInitialized("ko");
        Assert.True(Application.Current!.TryGetResource("home.title", null, out var value));
        Assert.Equal("홈", value);
    }

    [AvaloniaFact]
    public void DynamicResource_in_xaml_resolves_and_follows_language_changes()
    {
        TestServices.EnsureInitialized("en");
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, text.GetResourceObservable("home.title"));
        var toggle = new ToggleSwitch();
        var window = new Window { Content = new StackPanel { Children = { text, toggle } } };
        window.Show();
        Assert.Equal("Home", text.Text);

        TestServices.EnsureInitialized("ko");
        Assert.Equal("홈", text.Text);
        TestServices.EnsureInitialized("es"); // Mandarina: Filee's "ja" is not offered
        Assert.Equal("Inicio", text.Text);
        window.Close();
    }
}
