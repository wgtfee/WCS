using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;
using Wcs.Desktop.Interface;

namespace Wcs.Desktop.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthState _authState;
    private static readonly Random _rng = new();

    private readonly IDataProvider _dataProvider;

    public LoginViewModel(IAuthState authState, IDataProvider dataProvider)
    {
        _authState = authState;
        _dataProvider = dataProvider;
        GenerateCaptcha();
    }

    [ObservableProperty]
    private string _userName = "admin";

    [ObservableProperty]
    private string _password = "123456";

    [ObservableProperty]
    private string _captcha = string.Empty;

    [ObservableProperty]
    private string _captchaKey = string.Empty;

    [ObservableProperty]
    private Bitmap? _captchaImage;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public event Action? LoginSuccess;

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    [RelayCommand]
    private async Task GenerateCaptcha()
    {
        try
        {
            var result = await _dataProvider.getVierificationCode();

            if (result != null)
            {
                _captchaKey = result.UUID;
                var imgData = result.Img;
                if (imgData.StartsWith("data:image/png;base64,"))
                {
                    imgData = imgData.Substring("data:image/png;base64,".Length);
                }
                else if (imgData.StartsWith("data:image/jpeg;base64,"))
                {
                    imgData = imgData.Substring("data:image/jpeg;base64,".Length);
                }

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

        ErrorMessage = string.Empty;
        IsLoading = true;
        bool success = false;
        try
        {
            var loginData = new LoginInfo
            {
                UserName = UserName,
                Password = Password,
                VerificationCode = Captcha,
                UUID = _captchaKey
            };

            var token = await _dataProvider.GetToken(loginData);
            if (!token.Status && token.Data != null)
            {
                ErrorMessage =  "登录失败，用户名或密码错误";
                await GenerateCaptcha();
                throw new Exception(token.Message);
            }
            else
            {
                _authState.Token = token.Data.token;
                _authState.UserName = token.Data.userName;
                success = true;

                // 临时模拟用户信息，后续替换为真实接口
                UserInfo.User = new UserDto
                {
                    Name = token.Data.userName,
                    RoleId = 1
                };
                UserInfo.UserName = UserName;
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
}
