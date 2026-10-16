using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orbitale.App.Controls;

/// <summary>The image area. For now an empty dark canvas with a centred hint.</summary>
public sealed partial class LiveImageView : UserControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(LiveImageView), new PropertyMetadata(""));

    public LiveImageView() => InitializeComponent();

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
