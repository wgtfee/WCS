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
    public string ItemTypeText => IsAssertion ? "预期检查" : "执行动作";
    public string OperationText => SimulationScenarioChineseFormatter.Operation(Kind, IsAssertion ? "预期结果" : "动作");
    public string BodySummary => SimulationScenarioChineseFormatter.DataSummary(BodyJson);

    partial void OnItemTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAssertion));
        OnPropertyChanged(nameof(BodyLabel));
        OnPropertyChanged(nameof(ItemTypeText));
        OnPropertyChanged(nameof(OperationText));
    }

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(OperationText));
    }

    partial void OnBodyJsonChanged(string value)
    {
        OnPropertyChanged(nameof(BodySummary));
    }

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
