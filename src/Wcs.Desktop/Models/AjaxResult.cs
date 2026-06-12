using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace Wcs.Desktop.Models
{
    public class LoginInfo
    {


        [Display(Name = "用户名")]
        [MaxLength(50)]
        [Required(ErrorMessage = "用户名不能为空")]
        public string UserName { get; set; }
        [MaxLength(50)]
        [Display(Name = "密码")]
        [Required(ErrorMessage = "密码不能为空")]
        public string Password { get; set; }
        [MaxLength(6)]
        [Display(Name = "验证码")]
        [Required(ErrorMessage = "验证码不能为空")]
        public string VerificationCode { get; set; }
        [Required(ErrorMessage = "参数不完整")]
        /// <summary>
        /// 2020.06.12增加验证码
        /// </summary>
        public string UUID { get; set; }
    }

    public class CaptchaResult
    {
        public string Img { get; set; } = string.Empty;   // 已带 data:image/png;base64, 前缀
        public string UUID { get; set; } = string.Empty;   // Guid 字符串
    }
    /// <summary>
    /// 无 Data 的返回模型
    /// </summary>
    public class ApiResult : ApiResult<object> { }
    /// <summary>
    /// 统一返回模型
    /// </summary>
    public class ApiResult<T>
    {
        public bool Status { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        //public string Message { get; set; }
        public T Data { get; set; }
    }

    public class UploadResult
    {
        public int index { get; set; }
        public string name { get; set; }
        public string status { get; set; }
        public string thumbUrl { get; set; }
        public string url { get; set; }
        public int uploaded { get; set; }
       
    }

    /// <summary>
    /// 后端 WebResponseContent 的 data 字段
    /// </summary>
    public class LoginData
    {
        public string token { get; set; } = string.Empty;
        public string userName { get; set; } = string.Empty;
        public string? img { get; set; }

        public static implicit operator LoginData?(WebResponseContent<LoginData>? v)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 后端 WebResponseContent 完整结构
    /// </summary>
    public class WebResponseContent
    {
        public bool Status { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        //public string Message { get; set; }
        public object? Data { get; set; }
    }

    public class AuthState : IAuthState
    {
        public string? Token { get; set; }
        public string? UserName { get; set; }
    }

    public interface IAuthState
    {
        string? Token { get; set; }
        string? UserName { get; set; }
        bool IsAuthenticated => !string.IsNullOrEmpty(Token);
    }
    public class WebResponseContent<T> : WebResponseContent
    {
        public new T? Data
        {
            get => base.Data == null ? default : (T?)Convert.ChangeType(base.Data, typeof(T));
            set => base.Data = value;
        }
    }

}
