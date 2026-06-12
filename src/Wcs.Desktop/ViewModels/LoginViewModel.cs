using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlSugar;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Wcs.Desktop.ViewModels;

    public partial class LoginViewModel : ViewModelBase
    {
        private string _userName = string.Empty;
        private string _password = string.Empty;
        private string _captcha = string.Empty;
        private string _errorMessage = string.Empty;
        private bool _isLoading = false;
        private bool _isRmembered = false;
        public Bitmap? _captchaImage;
        public string UserName
        {
            get => _userName;
            set => SetProperty(ref _userName, value);
        }

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        public string Captcha
        {
            get => _captcha;
            set => SetProperty(ref _captcha, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public Bitmap? CaptchaImage
        {
            get => _captchaImage;
            set => SetProperty(ref _captchaImage, value);
        }

        private string _captchaKey = string.Empty;

        //对应服务器的IP
        private string _serverIP = "http://loacalhost:9991";
        public string ServerIP
        {
            get { return _serverIP; }
            set
            {
                SetProperty(ref _serverIP, value);
            }
        }

        public LoginViewModel()
        {
            // 通过容器获取服务
            var container = ContainerLocator.Container;
            //_userService = container.Resolve<ISysUserClientService>();
            _messageService = container.Resolve<IMessageManagerService>();
            _dataProvider = container.Resolve<IDataProvider>();
            _authState = container.Resolve<IAuthState>();
            RefreshCaptcha();
            #if DEBUG

                        UserName = "admin";
                        Password = "123456";
            #endif
        }



        [RelayCommand]
        private async Task RefreshCaptcha()
        {
            try
            {
                // 获取验证码  /api/User/getVierificationCode
                var result = await _dataProvider.getVierificationCode();

                if (result != null)
                {
                    // 方式1：如果 DataProvider 已经返回 Bitmap

                    _captchaKey = result.UUID;
                    var imgData = result.Img;
                    // 去掉 data:image/png;base64, 前缀，只保留纯 Base64
                    if (imgData.StartsWith("data:image/png;base64,"))
                    {
                        imgData = imgData.Substring("data:image/png;base64,".Length);
                    }
                    else if (imgData.StartsWith("data:image/jpeg;base64,"))
                    {
                        imgData = imgData.Substring("data:image/jpeg;base64,".Length);
                    }

                    // Base64 转 Bitmap
                    if (!string.IsNullOrEmpty(imgData))
                    {
                        var bytes = Convert.FromBase64String(imgData);
                        using var stream = new MemoryStream(bytes);
                        CaptchaImage = new Bitmap(stream);
                    }
                }
                else
                {
                    ErrorMessage =  "获取验证码失败";
                }
            

            }
            catch (Exception ex)
            {
                ErrorMessage = $"获取验证码失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }



        [RelayCommand]
        private async Task LoginAsync()
        {
            if (string.IsNullOrWhiteSpace(UserName))
            {
                ErrorMessage = "请输入用户名";
                return;
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "请输入密码";
                return;
            }
            if (string.IsNullOrWhiteSpace(Captcha))
            {
                ErrorMessage = "请输入验证码";
                return;
            }

            ErrorMessage = string.Empty;
            IsLoading = true;
            bool success = false;
            try
            {
                // 构建登录参数（和后端对应）
                var loginData = new LoginInfo
                {
                    UserName = UserName,
                    Password = Password,
                    VerificationCode = Captcha,      // 用户输入的验证码
                    UUID = _captchaKey    // 验证码唯一标识
                };
                //获取token
 
                var token = await _dataProvider.GetToken(loginData);
                if (!token.Status && token.Data != null)
                {
   
                     ErrorMessage =  "登录失败";
                    // 刷新验证码
                    await RefreshCaptcha();

                    throw new Exception(token.Message);
                }
                else
                {
                    // ✅ 保存 Token
                    _authState.Token = token.Data.token;
                    _authState.UserName = token.Data.userName;
                    success = true;
                    //这里需要修改为获取用户信息的接口，暂时先用token.Data模拟
                    //UserInfo.User = token.Data;
                    UserInfo.UserName = UserName;
                    //LocalSetting.SetAppSetting("Servers", string.Join(";", _serverIP));//把新服务器保存起来
           
                    // 触发登录成功事件，让App.axaml.cs处理窗口切换
                    LoginSuccess?.Invoke();
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"登录异常: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }

 
        }

        public event Action? LoginSuccess;
    }


