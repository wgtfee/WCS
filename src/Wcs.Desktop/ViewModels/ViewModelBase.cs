using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wcs.Desktop.Interface;

namespace Wcs.Desktop.ViewModels
{
    public abstract class ViewModelBase : ObservableObject, IAsyncInitializable
    {
        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            if (IsInitialized)
                return;

            await OnInitializeAsync();

            IsInitialized = true;
        }

        protected virtual Task OnInitializeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
