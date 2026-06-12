using Dm.filter;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Entity;

namespace Wcs.Desktop.Services
{
    public class ApiDataProvider : IDataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAuthState _authState;

        public ApiDataProvider(IHttpClientFactory httpClientFactory, IAuthState authState)
        {
            _httpClientFactory = httpClientFactory;
            _authState = authState;
        }

        #region 设置信息
        public string Url { get; set; } = "http://localhost:9991";
        public IAppHeader Header { get; set; }
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(30);

        public Dictionary<string, string> GetHeader()
        {
            return Header?.GetHeader() ?? new Dictionary<string, string>();
        }
        #endregion

        #region 初始化
        public void Init(string url, string appId, string appSecret, TimeSpan timeout)
        {
            var header = new AppSecretHeader(appId, appSecret);
            Url = url.TrimEnd('/');
            Header = (IAppHeader)header;
            TimeOut = timeout;
        }

        public void Init(string url, string userName, string password, int headMode, TimeSpan timeout)
        {
            var header = new AppTokenHeader(userName, password);
            Url = url.TrimEnd('/');
            Header = (IAppHeader)header;
            TimeOut = timeout;
        }

        private string BuildFullUrl(string url)
        {
            if (url.StartsWith("http://"))
                return url;
            return $"{Url}/{url.TrimStart('/')}";
        }
        #endregion

        #region Token 获取
        //[Logger]
        public async Task<CaptchaResult> getVierificationCode()
        {
            try
            {
                var url = $"{Url}/api/User/getVierificationCode";
                var responseBody = await GetStringAsync(url);
                var result = JsonConvert.DeserializeObject<CaptchaResult>(responseBody);
                return result ?? new CaptchaResult { Img = string.Empty, UUID = string.Empty };
            }
            catch (Exception ex)
            {
                return new CaptchaResult { Img = string.Empty, UUID = string.Empty };
            }
        }
        #endregion

        #region Token 获取
        //[Logger]
        public async Task<WebResponseContent<LoginData>> GetToken(LoginInfo info)
        {
            try
            {
                var result = await PostJsonAsync<WebResponseContent>(Url+"/api/user/login", info);
                // 反序列化为目标类型
                string LoginDatas = JsonConvert.SerializeObject(result.Data);
                var resulta = JsonConvert.DeserializeObject<LoginData>(LoginDatas);
                return new WebResponseContent<LoginData>
                {
                    Status = result.Status,
                    Code = result.Code,
                    Message = result.Message,
                    Data = resulta
                };
            }
            catch (Exception ex)
            {
                return new WebResponseContent<LoginData> { Status = false, Message = ex.Message };
            }
        }
        #endregion

        #region Menus 获取
        //[Logger]
        public async Task<WebResponseContent<List<MenuItemDto>>> GetMenus(int info)
        {
            try
            {
                var result = await PostJsonAsync<WebResponseContent>(Url+"/api/menu/getTreeItem", info);
                // 反序列化为目标类型
                string LoginDatas = JsonConvert.SerializeObject(result.Data);
                var resulta = JsonConvert.DeserializeObject<List<MenuItemDto>>(LoginDatas);
                return new WebResponseContent<List<MenuItemDto>>
                {
                    Status = result.Status,
                    Code = result.Code,
                    Message = result.Message,
                    Data = new List<MenuItemDto>
                    {
                        new MenuItemDto
                        {
                            Id = 0,
                            Name = string.Empty,
                            Url = string.Empty,
                            ParentId = 0,
                            Icon = string.Empty,
                            Enable = 0,
                            TableName = string.Empty,
                            Permission = string.Empty,
                            Children = new()
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return new WebResponseContent<List<MenuItemDto>> { Status = false, Message = ex.Message };
            }
        }
        #endregion

        #region 业务请求方法

        /// <summary>
        /// POST JSON 请求
        /// </summary>
        //[Logger]
        public async Task<ApiResult<T>> PostDataAsync<T>(string url, object data)
        {
            return await PostJsonAsync<ApiResult<T>>(url, data);
        }

        /// <summary>
        /// POST 表单请求
        /// </summary>
        //[Logger]
        public async Task<ApiResult<T>> PostFormAsync<T>(string url, Dictionary<string, string> data)
        {
            try
            {
                var fullUrl = BuildFullUrl(url);
                var formContent = new MultipartFormDataContent();

                if (data != null)
                {
                    foreach (var item in data)
                        formContent.Add(new StringContent(item.Value ?? string.Empty), item.Key);
                }

                var responseBody = await PostAsync(fullUrl, formContent);
                var result = JsonConvert.DeserializeObject<ApiResult<T>>(responseBody);
                return result ?? new ApiResult<T> { Status = false, Message = "解析响应失败" };
            }
            catch (Exception ex)
            {
                return new ApiResult<T> { Status = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// GET JSON 请求
        /// </summary>
        //[Logger]
        public async Task<ApiResult<T>> GetDataAsync<T>(string url, Dictionary<string, string> queryParams = null)
        {
            try
            {
                var fullUrl = BuildFullUrl(url);

                if (queryParams != null && queryParams.Count > 0)
                {
                    var queryString = await new FormUrlEncodedContent(queryParams).ReadAsStringAsync();
                    fullUrl = $"{fullUrl}?{queryString}";
                }

                var responseBody = await GetStringAsync(fullUrl);
                var result = JsonConvert.DeserializeObject<ApiResult<T>>(responseBody);
                return result ?? new ApiResult<T> { Status = false, Message = "解析响应失败" };
            }
            catch (Exception ex)
            {
                return new ApiResult<T> { Status = false, Message = ex.Message };
            }
        }

        #endregion

        #region 文件操作

        public async Task<ApiResult> UploadFile(string path, string fileName)
        {
            try
            {
                var data = new MultipartFormDataContent();
                var fileBytes = await File.ReadAllBytesAsync(path);
                data.Add(new ByteArrayContent(fileBytes), "file", fileName);

                var responseBody = await PostAsync(BuildFullUrl("/api/FileServer/SaveFile"), data);
                return JsonConvert.DeserializeObject<ApiResult>(responseBody)
                    ?? new ApiResult { Status = false, Message = "解析响应失败" };
            }
            catch (Exception ex)
            {
                return new ApiResult { Status = false, Message = ex.Message };
            }
        }

        public async Task<ApiResult<UploadResult>> UploadFileByForm(string path)
        {
            try
            {
                using var fStream = File.OpenRead(path);
                using var data = new MultipartFormDataContent();
                data.Add(new StreamContent(fStream), "file", Path.GetFileName(path));

                var responseBody = await PostAsync(BuildFullUrl("/Base_Manage/Upload/UploadFileByForm"), data);
                return JsonConvert.DeserializeObject<ApiResult<UploadResult>>(responseBody)
                    ?? new ApiResult<UploadResult> { Status = false, Message = "解析响应失败" };
            }
            catch (Exception ex)
            {
                return new ApiResult<UploadResult> { Status = false, Message = ex.Message };
            }
        }

        public async Task<ApiResult<UploadResult>> UploadFileChunck(string path, Action<double> progressAction)
        {
            try
            {
                using var fStream = File.OpenRead(path);
                int chunckSize = 2097152; // 2MB
                int totalChunks = (int)(fStream.Length / chunckSize);
                if (fStream.Length % chunckSize != 0)
                    totalChunks++;

                double progress = 0d;
                progressAction?.Invoke(progress);

                var tempDirectory = Guid.NewGuid().ToString("N");
                ApiResult<UploadResult> result = null;

                for (int i = 0; i < totalChunks; i++)
                {
                    long position = i * (long)chunckSize;
                    int toRead = (int)Math.Min(fStream.Length - position, chunckSize);
                    byte[] buffer = new byte[toRead];
                    await fStream.ReadAsync(buffer, 0, buffer.Length);

                    using var data = new MultipartFormDataContent();
                    data.Add(new StringContent(tempDirectory), "tempDirectory");
                    data.Add(new StringContent(i.ToString()), "index");
                    data.Add(new StringContent(totalChunks.ToString()), "total");
                    data.Add(new ByteArrayContent(buffer), "file", Path.GetFileName(path));

                    var responseBody = await PostAsync(BuildFullUrl("/Base_Manage/Upload/UploadFileChunck"), data);
                    result = JsonConvert.DeserializeObject<ApiResult<UploadResult>>(responseBody);

                    progress += 1d / totalChunks;
                    progressAction?.Invoke(progress);
                }

                return result ?? new ApiResult<UploadResult> { Status = false, Message = "上传失败" };
            }
            catch (Exception ex)
            {
                return new ApiResult<UploadResult> { Status = false, Message = ex.Message };
            }
        }

        public async Task<ApiResult> DownLoadFile(string fileUrl, string savePath)
        {
            try
            {
                var fileBytes = await GetByteArrayAsync(fileUrl);
                await File.WriteAllBytesAsync(savePath, fileBytes);
                return new ApiResult { Status = true, Message = "下载成功" };
            }
            catch (Exception ex)
            {
                return new ApiResult { Status = false, Message = ex.Message };
            }
        }

        #endregion

        #region HTTP 核心方法
        public async Task<string> GetAsync(string url, TimeSpan timeSpan, Dictionary<string, string>? header = null)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = timeSpan;

            if (header != null)
            {
                foreach (var item in header)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
                }
            }

            var response = await client.GetAsync(BuildUrl(url));

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<T> PostJsonAsync<T>(string url, object data)
        {
            var fullUrl = BuildFullUrl(url);
            var json = JsonConvert.SerializeObject(data);
            var responseBody = await PostJsonStringAsync(fullUrl, json);
            return JsonConvert.DeserializeObject<T>(responseBody);
        }

        private async Task<string> PostJsonStringAsync(string url, string json)
        {
            using var client = CreateClient();
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"服务器返回错误: {(int)response.StatusCode}");

            return responseBody;
        }

        private async Task<string> PostAsync(string url, HttpContent content)
        {
            using var client = CreateClient();

            var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"服务器返回错误: {(int)response.StatusCode}");

            return responseBody;
        }

        private async Task<string> GetStringAsync(string url)
        {
            using var client = CreateClient();

            var response = await client.GetAsync(url);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"服务器返回错误: {(int)response.StatusCode}");

            return responseBody;
        }

        private async Task<byte[]> GetByteArrayAsync(string url)
        {
            using var client = CreateClient();
            return await client.GetByteArrayAsync(url);
        }

        private HttpClient CreateClient()
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeOut;

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var headers = GetHeader();
            foreach (var item in headers)
            {
                if (!client.DefaultRequestHeaders.Contains(item.Key))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
            }

            return client;
        }

        // ========== 带认证的方法（推荐业务层使用） ==========

        /// <summary>
        /// 带认证的 GET（无参数）
        /// </summary>
        public async Task<string> GetWithAuthAsync(string url)
        {
            var headers = GetAuthHeaders();
            return await GetAsync(url, TimeOut, headers);
        }

        /// <summary>
        /// 带认证的 GET（URL 查询参数）
        /// </summary>
        public async Task<string> GetWithAuthAsync(string url, Dictionary<string, object> queryParams)
        {
            var fullUrl = BuildUrlWithQuery(url, queryParams);
            var headers = GetAuthHeaders();
            return await GetAsync(fullUrl, TimeOut, headers);
        }

        /// <summary>
        /// 带认证的 POST JSON（Body）
        /// </summary>
        public async Task<string> PostJsonWithAuthAsync(string url, string json)
        {
            var headers = GetAuthHeaders();
            return await PostAsyncJson(url, json, TimeOut, headers);
        }

        /// <summary>
        /// 带认证的 POST JSON（Body + URL 查询参数）
        /// </summary>
        public async Task<string> PostJsonWithAuthAsync(string url, string json, Dictionary<string, object> queryParams)
        {
            var fullUrl = BuildUrlWithQuery(url, queryParams);
            var headers = GetAuthHeaders();
            return await PostAsyncJson(fullUrl, json, TimeOut, headers);
        }

        /// <summary>
        /// 带认证的 POST 表单
        /// </summary>
        public async Task<string> PostFormWithAuthAsync(string url, Dictionary<string, string> formData)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeOut;

            // 添加认证头
            if (!string.IsNullOrEmpty(_authState.Token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authState.Token);
            }

            var content = new FormUrlEncodedContent(formData);
            var response = await client.PostAsync(BuildUrl(url), content);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> PostAsyncJson(string url, string json, TimeSpan timeSpan, Dictionary<string, string>? header = null)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = timeSpan;

            if (header != null)
            {
                foreach (var item in header)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
                }
            }

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return await response.Content.ReadAsStringAsync();
        }


        #endregion

        // ========== 私有辅助方法 ==========

        /// <summary>
        /// 获取认证头
        /// </summary>
        private Dictionary<string, string> GetAuthHeaders()
        {
            var headers = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(_authState.Token))
            {
                headers["Authorization"] = $"Bearer {_authState.Token}";
            }

            return headers;
        }

        /// <summary>
        /// 构建完整 URL（拼接基础地址）
        /// </summary>
        private string BuildUrl(string path)
        {
            if (path.StartsWith("http"))
                return path;

            var baseUrl = Url.EndsWith("/") ? Url : $"{Url}/";
            var cleanPath = path.StartsWith("/") ? path[1..] : path;

            return $"{baseUrl}{cleanPath}";
        }

        /// <summary>
        /// 构建带查询参数的 URL
        /// </summary>
        private string BuildUrlWithQuery(string path, Dictionary<string, object> queryParams)
        {
            var baseUrl = BuildUrl(path);

            if (queryParams == null || queryParams.Count == 0)
                return baseUrl;

            var query = string.Join("&", queryParams.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value?.ToString() ?? string.Empty)}"));

            return $"{baseUrl}?{query}";
        }
    }
}