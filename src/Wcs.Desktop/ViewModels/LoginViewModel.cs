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

        ErrorMessage = string.Empty;
        IsLoading = true;

        try
        {
            await Task.Delay(500);

            if (UserName == "admin" && Password == "123456")
            {
                _authState.Token = "mock-token-xxx";
                _authState.UserName = UserName;
                LoginSuccess?.Invoke();
            }
            else
            {
                ErrorMessage = "用户名或密码错误";
                GenerateCaptcha();
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
