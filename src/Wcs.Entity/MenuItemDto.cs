using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wcs.Entity;


    /// <summary>
    /// 菜单项 Dto（从后端 API 获取的扁平数据转换为树）
    /// </summary>
    public partial class MenuItemDto 
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;      // 对应的路由或 View 标识
        public int ParentId { get; set; }
        public string Icon { get; set; } = string.Empty;
        public int Enable { get; set; }
        public string TableName { get; set; }
        public object Permission { get; set; }
        public ObservableCollection<MenuItemDto> Children { get; set; } = new();
    }

