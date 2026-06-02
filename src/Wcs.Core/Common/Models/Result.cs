namespace Wcs.Core.Common.Models;

/// <summary>
/// 基础结果模型
/// </summary>
public class Result
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }

    public static Result Ok(string message = "操作成功")
    {
        return new Result { Success = true, Message = message };
    }

    public static Result<T> Ok<T>(T data, string message = "操作成功")
    {
        return new Result<T> { Success = true, Data = data, Message = message };
    }

    public static Result Fail(string message, string? errorCode = null)
    {
        return new Result { Success = false, Message = message, ErrorCode = errorCode };
    }

    public static Result<T> Fail<T>(string message, string? errorCode = null)
    {
        return new Result<T> { Success = false, Message = message, ErrorCode = errorCode };
    }
}

/// <summary>
/// 泛型结果模型
/// </summary>
public class Result<T> : Result
{
    public T? Data { get; set; }
}
