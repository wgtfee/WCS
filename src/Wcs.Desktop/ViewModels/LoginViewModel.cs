using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wcs.Desktop.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    [ObservableProperty]
    private string _userName = "admin";

    [ObservableProperty]
    private string _password = "123456";

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
                LoginSuccess?.Invoke();
            }
            else
            {
                ErrorMessage = "用户名或密码错误";
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
