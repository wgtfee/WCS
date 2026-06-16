using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wcs.Desktop.Models.Log
{
    public class TrackingLogItem
    {
        public long Id { get; set; }

        public string EventName { get; set; } = "";

        public string EventType { get; set; } = "";

        public string UserName { get; set; } = "";


        public string PageUrl { get; set; }

        public string UserIP { get; set; } = "";
        public string Browser { get; set; } = "";

        public string OS { get; set; } = "";

         public DateTimeOffset EventTime { get; set; }
    }   
}
