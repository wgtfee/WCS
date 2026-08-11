namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class SimulationTimelineEditorItem : ObservableObject
{
    [ObservableProperty] private string _itemType = "Action";
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _atMillisecondsText = "0";
    [ObservableProperty] private string _durationMillisecondsText = "0";
    [ObservableProperty] private string _kind = "state.set";
    [ObservableProperty] private string _target = "state.demo";
    [ObservableProperty] private string _bodyJson = "{}";
    [ObservableProperty] private int _order;

    public bool IsAssertion => string.Equals(ItemType, "Assertion", StringComparison.OrdinalIgnoreCase);
    public string BodyLabel => IsAssertion ? "Expected" : "Payload";

    public SimulationTimelineEditorItem Clone() => new()
    {
        ItemType = ItemType,
        Id = Id,
        AtMillisecondsText = AtMillisecondsText,
        DurationMillisecondsText = DurationMillisecondsText,
        Kind = Kind,
        Target = Target,
        BodyJson = BodyJson,
        Order = Order
    };
}
