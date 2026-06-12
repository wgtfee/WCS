using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;

namespace Wcs.Desktop.Services;

/// <summary>
/// 数据提供者 - 临时实现，后续接入真实后端 API
/// </summary>
public class DataProvider : IDataProvider
{
    public string Url { get; set; } = "http://localhost:9991";
    public IAppHeader Header { get; set; } = null!;
    public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(30);

    public void Init(string url, string appId, string appSecret, TimeSpan timeout) { }
    public void Init(string url, string userName, string password, int headMode, TimeSpan timeout) { }

    public Dictionary<string, string> GetHeader() => new();

    public async Task<WebResponseContent<LoginData>> GetToken(LoginInfo info)
    {
        await Task.Delay(500);
        return new WebResponseContent<LoginData>
        {
            Status = true,
            Data = new LoginData
            {
                token = "mock-token-xxx",
                userName = info.UserName
            }
        };
    }

    public async Task<CaptchaResult> getVierificationCode()
    {
        await Task.CompletedTask;
        return new CaptchaResult
        {
            UUID = Guid.NewGuid().ToString(),
            Img = ""
        };
    }

    public async Task<ApiResult<T>> PostDataAsync<T>(string url, object data)
    {
        await Task.CompletedTask;
        return new ApiResult<T> { Status = true };
    }

    public async Task<ApiResult<T>> PostFormAsync<T>(string url, Dictionary<string, string> data)
    {
        await Task.CompletedTask;
        return new ApiResult<T> { Status = true };
    }

    public async Task<ApiResult<T>> GetDataAsync<T>(string url, Dictionary<string, string>? queryParams = null)
    {
        await Task.CompletedTask;
        return new ApiResult<T> { Status = true };
    }

    public Task<ApiResult> UploadFile(string path, string fileName) => Task.FromResult(new ApiResult { Status = true });
    public Task<ApiResult<UploadResult>> UploadFileByForm(string path) => Task.FromResult(new ApiResult<UploadResult> { Status = true });
    public Task<ApiResult<UploadResult>> UploadFileChunck(string path, Action<double> progressAction) => Task.FromResult(new ApiResult<UploadResult> { Status = true });
    public Task<ApiResult> DownLoadFile(string fileUrl, string savePath) => Task.FromResult(new ApiResult { Status = true });
}
