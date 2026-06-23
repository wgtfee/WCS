using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 物体追踪 ViewModel
/// </summary>
public partial class ObjectsViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    public ObservableCollection<ObjectItem> Objects { get; } = new();

    [ObservableProperty] private bool _isLoading;

    public ObjectsViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.ObjectMoved += msg =>
        {
            var existing = Objects.FirstOrDefault(o => o.ObjectId == msg.ObjectId);
            if (existing is not null)
            {
                existing.CurrentPosition = msg.NewPos;
                existing.UpdateTime = DateTime.UtcNow;
            }
        };
    }

    protected override async Task OnInitializeAsync()
    {
        await LoadAsync();
    }
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var objects = await _api.GetObjectsAsync();
            Objects.Clear();
            foreach (var o in objects)
            {
                Objects.Add(new ObjectItem
                {
                    ObjectId = o.ObjectId,
                    CurrentPosition = o.CurrentPosition,
                    TargetPosition = o.TargetPosition,
                    Status = o.Status.ToString(),
                    UpdateTime = o.UpdateTime
                });
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();
}
