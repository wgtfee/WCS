using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wcs.Desktop.Controls;

/// <summary>
/// 概览数字卡片控件
/// </summary>
public partial class OverviewCard : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<OverviewCard, string>(nameof(Label));

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<OverviewCard, string>(nameof(Value));

    public static readonly StyledProperty<string> BackgroundColorProperty =
        AvaloniaProperty.Register<OverviewCard, string>(nameof(BackgroundColor), "#2A2A2A");

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string BackgroundColor
    {
        get => GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public OverviewCard()
    {
        InitializeComponent();
        BackgroundColorProperty.Changed.AddClassHandler<OverviewCard>(OnBackgroundColorChanged);
    }

    private static void OnBackgroundColorChanged(OverviewCard card, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is string hex && !string.IsNullOrEmpty(hex))
            card.CardBorder.Background = new SolidColorBrush(Color.Parse(hex));
    }
}
