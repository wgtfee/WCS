using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wcs.Desktop.Models;

    public class UserDto 
    {
        private string _Name;
        public string Name
        {
            get { return _Name; }
            set {_Name = value; }
        }

        private string _Password;
        public string Password
        {
            get { return _Password; }
            set { _Password = value ; }
        }

        private string _JobNumber;
        public string JobNumber
        {
            get { return _JobNumber; }
            set { _JobNumber = value; }
        }

        private string _Department;
        public string Department
        {
            get { return _Department; }
            set { _Department = value; }
        }

        private RoleDto? _Role;
        public RoleDto? Role
        {
            get { return _Role; }
            set { _Role = value; }
        }

        public int RoleId { get; set; }
    }

