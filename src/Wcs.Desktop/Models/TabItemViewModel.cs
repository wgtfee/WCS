using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wcs.Desktop.Models
{
    public class TabItem
    {
        public string Header { get; init; } = string.Empty;

        public object Content { get; init; } = null!;

        public bool CanClose { get; init; } = true;
    }
}
