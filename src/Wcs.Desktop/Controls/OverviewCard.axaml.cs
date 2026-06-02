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

    public static readonly StyledProperty<IBrush> BackgroundColorProperty =
        AvaloniaProperty.Register<OverviewCard, IBrush>(nameof(BackgroundColor),
            new SolidColorBrush(Color.Parse("#2A2A2A")));

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

    public IBrush BackgroundColor
    {
        get => GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public OverviewCard()
    {
        InitializeComponent();
    }
}
