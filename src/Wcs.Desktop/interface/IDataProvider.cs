using Wcs.Desktop.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wcs.Entity;

namespace Wcs.Desktop.Interface
{
    public interface IDataProvider
    {
        #region 设置信息
        string Url { get; set; }
        IAppHeader Header { get; set; }
        TimeSpan TimeOut { get; set; }
        Dictionary<string, string> GetHeader();
        #endregion

        #region 初始化
        void Init(string url, string appId, string appSecret, TimeSpan timeout);
        void Init(string url, string userName, string password, int headMode, TimeSpan timeout);
        #endregion

        #region Token
        Task<WebResponseContent<LoginData>> GetToken(LoginInfo info);
        #endregion

        #region POST
        Task<ApiResult<T>> PostDataAsync<T>(string url, object data);
        Task<ApiResult<T>> PostFormAsync<T>(string url, Dictionary<string, string> data);
        #endregion

        #region GET
        Task<ApiResult<T>> GetDataAsync<T>(string url, Dictionary<string, string> queryParams = null);
        #endregion

        #region 文件操作
        Task<ApiResult> UploadFile(string path, string fileName);
        Task<ApiResult<UploadResult>> UploadFileByForm(string path);
        Task<ApiResult<UploadResult>> UploadFileChunck(string path, Action<double> progressAction);
        Task<ApiResult> DownLoadFile(string fileUrl, string savePath);
        Task<CaptchaResult> getVierificationCode();
        Task<WebResponseContent<List<MenuItemDto>>> GetMenus(int info);
        #endregion
    }
}