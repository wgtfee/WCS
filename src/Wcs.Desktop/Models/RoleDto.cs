using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wcs.Desktop.Models
{
    public class RoleDto 
    {
        private string _Name;
        public string Name
        {
            get { return _Name; }
            set {  _Name = value; }
        }

        private int _Sort;
        public int Sort
        {
            get { return _Sort; }
            set { _Sort = value; }

        }
    }
}
