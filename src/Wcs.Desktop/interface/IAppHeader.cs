using System.Collections.Generic;

namespace Wcs.Desktop.Interface
{
    public interface IAppHeader
    {
        Dictionary<string, string> GetHeader();
    }
}
