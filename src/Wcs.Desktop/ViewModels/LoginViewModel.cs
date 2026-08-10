using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;
using Wcs.Desktop.Interface;

namespace Wcs.Desktop.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthState _authState;
    private readonly IDataProvider _dataProvider;
    private readonly IDesktopIamAuthService _iamAuth;

    public LoginViewModel(
        IAuthState authState,
        IDataProvider dataProvider,
        IDesktopIamAuthService iamAuth,
        IOptions<DesktopIamOptions> iamOptions)
    {
        _authState = authState;
        _dataProvider = dataProvider;
        _iamAuth = iamAuth;
        UseIam = iamOptions.Value.Enabled;
        if (!UseIam)
            _ = GenerateCaptcha();
    }

    public bool UseIam { get; }
    public bool UseLocalLogin => !UseIam;
    public string LoginModeText => UseIam ? "统一身份认证（IAM + PKCE）" : "本地兼容登录";
    public string LoginButtonText => UseIam ? "在浏览器中登录" : "登 录";

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
        if (UseIam) return;
        try
        {
            var result = await _dataProvider.getVierificationCode();

            if (result != null)
            {
                CaptchaKey = result.UUID;
                var imgData = result.Img;
                if (imgData.StartsWith("data:image/png;base64,"))
                    imgData = imgData["data:image/png;base64,".Length..];
                else if (imgData.StartsWith("data:image/jpeg;base64,"))
                    imgData = imgData["data:image/jpeg;base64,".Length..];

                if (!string.IsNullOrEmpty(imgData))
                {
                    var bytes = Convert.FromBase64String(imgData);
                    using var stream = new MemoryStream(bytes);
                    CaptchaImage = new Bitmap(stream);
                }
            }
            else
            {
                ErrorMessage = "获取验证码失败";
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
        if (!UseIam && string.IsNullOrWhiteSpace(UserName))
        {
            ErrorMessage = "请输入用户名";
            return;
        }

        if (!UseIam && string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "请输入密码";
            return;
        }

        if (!UseIam && string.IsNullOrWhiteSpace(Captcha))
        {
            ErrorMessage = "请输入验证码";
            return;
        }

        ErrorMessage = string.Empty;
        IsLoading = true;
        try
        {
            if (UseIam)
            {
                await LoginWithIamAsync();
                return;
            }

            await LoginWithLocalCompatibilityAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"登录异常: {ex.Message}";
            if (!UseIam)
                await GenerateCaptcha();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoginWithIamAsync()
    {
        var result = await _iamAuth.LoginAsync();
        if (!result.Success || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            ErrorMessage = result.Error ?? "IAM 登录失败";
            return;
        }

        _authState.Token = result.AccessToken;
        var resolvedUserName = result.UserName ?? result.DisplayName ?? "IAM User";
        _authState.UserName = resolvedUserName;
        UserInfo.User = new UserDto
        {
            Name = result.DisplayName ?? resolvedUserName,
            RoleId = 0,
        };
        UserInfo.UserName = resolvedUserName;
        LoginSuccess?.Invoke();
    }

    private async Task LoginWithLocalCompatibilityAsync()
    {
        var loginData = new LoginInfo
        {
            UserName = UserName,
            Password = Password,
            VerificationCode = Captcha,
            UUID = CaptchaKey
        };

        var token = await _dataProvider.GetToken(loginData);
        if (!token.Status || token.Data is null || string.IsNullOrWhiteSpace(token.Data.token))
        {
            ErrorMessage = string.IsNullOrWhiteSpace(token.Message)
                ? "登录失败，用户名、密码或验证码错误"
                : token.Message;
            await GenerateCaptcha();
            return;
        }

        _authState.Token = token.Data.token;
        _authState.UserName = token.Data.userName;
        UserInfo.User = new UserDto
        {
            Name = token.Data.userName,
            RoleId = token.Data.Role_Id,
        };
        UserInfo.UserName = token.Data.userName;
        LoginSuccess?.Invoke();
    }
}
