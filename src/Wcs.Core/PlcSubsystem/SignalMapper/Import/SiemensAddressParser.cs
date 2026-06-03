namespace Wcs.Core.PlcSubsystem.SignalMapper.Import;

/// <summary>
/// 西门子 PLC 地址解析器
/// 将 "DB1.DBX0.0" / "DB1.DBW2" / "DB1.DBD4" 格式解析为 BlockNumber/ByteOffset/BitOffset
/// </summary>
public static class SiemensAddressParser
{
    /// <summary>
    /// 解析西门子地址字符串
    /// </summary>
    /// <param name="address">地址，如 "DB1.DBX0.0", "DB1.DBW2", "DB1.DBD4"</param>
    /// <returns>解析结果，失败返回 null</returns>
    public static PlcAddressResult? Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        address = address.Trim().ToUpperInvariant();

        // 匹配模式: DB{number}.DBX{byte}.{bit} 或 DB{number}.DBW{byte} 或 DB{number}.DBD{byte}
        try
        {
            // 先提取 DB 块号
            if (!address.StartsWith("DB"))
                return null;

            var afterDb = address.Substring(2);
            var dotIndex = afterDb.IndexOf('.');
            if (dotIndex < 0) return null;

            if (!int.TryParse(afterDb.Substring(0, dotIndex), out var blockNumber))
                return null;

            var afterDot = afterDb.Substring(dotIndex + 1); // DBX0.0 或 DBB0 或 DBW2 或 DBD4

            // DBX 格式: DBX{byte}.{bit}
            if (afterDot.StartsWith("DBX"))
            {
                var parts = afterDot.Substring(3).Split('.');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var byteOffset)
                    && int.TryParse(parts[1], out var bitOffset)
                    && bitOffset >= 0 && bitOffset <= 7)
                {
                    return new PlcAddressResult
                    {
                        BlockNumber = blockNumber,
                        ByteOffset = byteOffset,
                        BitOffset = bitOffset,
                        DataType = "bool"
                    };
                }
            }

            // DBB 格式: DBB{byte}
            if (afterDot.StartsWith("DBB"))
            {
                if (int.TryParse(afterDot.Substring(3), out var byteOffset))
                {
                    return new PlcAddressResult
                    {
                        BlockNumber = blockNumber,
                        ByteOffset = byteOffset,
                        BitOffset = -1,
                        DataType = "byte"
                    };
                }
            }

            // DBW 格式: DBW{byte} (2 字节)
            if (afterDot.StartsWith("DBW"))
            {
                if (int.TryParse(afterDot.Substring(3), out var byteOffset))
                {
                    return new PlcAddressResult
                    {
                        BlockNumber = blockNumber,
                        ByteOffset = byteOffset,
                        BitOffset = -1,
                        DataType = "int"
                    };
                }
            }

            // DBD 格式: DBD{byte} (4 字节)
            if (afterDot.StartsWith("DBD"))
            {
                if (int.TryParse(afterDot.Substring(3), out var byteOffset))
                {
                    return new PlcAddressResult
                    {
                        BlockNumber = blockNumber,
                        ByteOffset = byteOffset,
                        BitOffset = -1,
                        DataType = "dword"
                    };
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}

/// <summary>
/// PLC 地址解析结果
/// </summary>
public class PlcAddressResult
{
    public int BlockNumber { get; set; }
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; } = -1;
    public string DataType { get; set; } = "bool";
}
