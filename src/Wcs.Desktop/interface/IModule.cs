using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;


namespace Wcs.Desktop.Interface
{

    public interface IModule
    {
        string ModuleName { get; }

        string Icon { get; }

        int Order { get; }

        void RegisterServices(IServiceCollection services);

        IEnumerable<IModulePage> GetPages();
    }

    public interface IModulePage
    {
        string Route { get; }

        string Title { get; }

        Type ViewModelType { get; }

        bool ShowInMenu { get; }
    }
}