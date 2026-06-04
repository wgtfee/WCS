namespace Wcs.Simulator.PlcSimulatorEngine;

/// <summary>
/// 3 PLC 9 DB 块模拟数据生成器
/// 生成符合 Struct.FromBytes 格式的 byte[]（大端，S7 兼容）
/// </summary>
public static class PlcSimulatorEngine
{
    private static readonly Random _rng = new();

    // ==================== PLC1: 输送线 ====================

    /// <summary>PLC1.DB1: 输送线状态 (40 字节, 10 站 × 4 字节)</summary>
    public static byte[] GeneratePlc1_Status()
    {
        var data = new byte[40];
        for (int i = 0; i < 10; i++)
        {
            int offset = i * 4;
            data[offset] = 0x01; // DriveReady
            if (_rng.NextDouble() > 0.7) data[offset] |= 0x02; // PalletArrived
            if (_rng.NextDouble() > 0.95) data[offset] |= 0x04; // Fault
            if (_rng.NextDouble() > 0.8) data[offset] |= 0x08; // Busy
            var spd = BitConverter.GetBytes((short)_rng.Next(500, 2000));
            if (BitConverter.IsLittleEndian) Array.Reverse(spd);
            data[offset + 2] = spd[0];
            data[offset + 3] = spd[1];
        }
        return data;
    }

    /// <summary>PLC1.DB2: 输送线请求 (20 字节)</summary>
    public static byte[] GeneratePlc1_Requests()
    {
        var data = new byte[20];
        for (int i = 0; i < 10; i++)
        {
            int offset = i * 2;
            if (_rng.NextDouble() > 0.85) data[offset] |= 0x01; // RequestOut
            if (_rng.NextDouble() > 0.90) data[offset] |= 0x02; // RequestIn
            data[offset + 1] = (byte)_rng.Next(1, 11); // TargetStation
        }
        return data;
    }

    /// <summary>PLC1.DB3: 输送线报警 (20 字节, 10 站 × 2 字节)</summary>
    public static byte[] GeneratePlc1_Alarms()
    {
        var data = new byte[20];
        for (int i = 0; i < 10; i++)
        {
            int offset = i * 2;
            if (_rng.NextDouble() > 0.92)
            {
                data[offset] = 0x01; // Alarm
                data[offset + 1] = (byte)_rng.Next(1, 10); // AlarmCode
            }
        }
        return data;
    }

    // ==================== PLC2: 堆垛机 ====================

    /// <summary>PLC2.DB1: 堆垛机状态 (24 字节, 4 台 × 6 字节)</summary>
    public static byte[] GeneratePlc2_Status()
    {
        var data = new byte[24];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 6;
            if (_rng.NextDouble() > 0.7) data[offset] |= 0x01; // Busy
            if (_rng.NextDouble() > 0.95) data[offset] |= 0x02; // Fault
            data[offset] |= 0x04; // AutoMode
            if (_rng.NextDouble() > 0.8) data[offset] |= 0x08; // PositionArrived
            var col = BitConverter.GetBytes((short)_rng.Next(1, 100));
            var row = BitConverter.GetBytes((short)_rng.Next(1, 20));
            if (BitConverter.IsLittleEndian) { Array.Reverse(col); Array.Reverse(row); }
            data[offset + 2] = col[0]; data[offset + 3] = col[1];
            data[offset + 4] = row[0]; data[offset + 5] = row[1];
        }
        return data;
    }

    /// <summary>PLC2.DB2: 堆垛机请求 (24 字节, 4 台 × 6 字节)</summary>
    public static byte[] GeneratePlc2_Requests()
    {
        var data = new byte[24];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 6;
            if (_rng.NextDouble() > 0.85) data[offset] |= 0x01; // StoreReq
            if (_rng.NextDouble() > 0.90) data[offset] |= 0x02; // RetrieveReq
            var col = BitConverter.GetBytes((short)_rng.Next(1, 100));
            var row = BitConverter.GetBytes((short)_rng.Next(1, 20));
            if (BitConverter.IsLittleEndian) { Array.Reverse(col); Array.Reverse(row); }
            data[offset + 2] = col[0]; data[offset + 3] = col[1];
            data[offset + 4] = row[0]; data[offset + 5] = row[1];
        }
        return data;
    }

    /// <summary>PLC2.DB3: 堆垛机报警 (14 字节)</summary>
    public static byte[] GeneratePlc2_Alarms()
    {
        var data = new byte[14];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4; // 2B header + 2B fault
            if (_rng.NextDouble() > 0.90)
            {
                data[offset] = 0x01;
                data[offset + 1] = (byte)_rng.Next(1, 20);
                var fd = BitConverter.GetBytes((short)_rng.Next(100, 9999));
                if (BitConverter.IsLittleEndian) Array.Reverse(fd);
                data[offset + 2] = fd[0]; data[offset + 3] = fd[1];
            }
        }
        return data;
    }

    // ==================== PLC3: 机器人 ====================

    /// <summary>PLC3.DB1: 机器人状态 (16 字节, 4 台 × 4 字节)</summary>
    public static byte[] GeneratePlc3_Status()
    {
        var data = new byte[16];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4;
            if (_rng.NextDouble() > 0.7) data[offset] |= 0x01; // Busy
            if (_rng.NextDouble() > 0.9) data[offset] |= 0x02; // Gripped
            if (_rng.NextDouble() > 0.95) data[offset] |= 0x04; // Fault
            if (_rng.NextDouble() > 0.6) data[offset] |= 0x08; // PalletPresent
            var pos = BitConverter.GetBytes((short)_rng.Next(0, 360));
            if (BitConverter.IsLittleEndian) Array.Reverse(pos);
            data[offset + 2] = pos[0]; data[offset + 3] = pos[1];
        }
        return data;
    }

    /// <summary>PLC3.DB2: 机器人请求 (16 字节)</summary>
    public static byte[] GeneratePlc3_Requests()
    {
        var data = new byte[16];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4;
            if (_rng.NextDouble() > 0.85) data[offset] |= 0x01; // GripReq
            if (_rng.NextDouble() > 0.90) data[offset] |= 0x02; // ReleaseReq
            if (_rng.NextDouble() > 0.88) data[offset] |= 0x04; // MoveReq
            var pos = BitConverter.GetBytes((short)_rng.Next(0, 360));
            if (BitConverter.IsLittleEndian) Array.Reverse(pos);
            data[offset + 2] = pos[0]; data[offset + 3] = pos[1];
        }
        return data;
    }

    /// <summary>PLC3.DB3: 机器人报警 (8 字节)</summary>
    public static byte[] GeneratePlc3_Alarms()
    {
        var data = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 2;
            if (_rng.NextDouble() > 0.92)
            {
                data[offset] = 0x01;
                data[offset + 1] = (byte)_rng.Next(1, 15);
            }
        }
        return data;
    }

    /// <summary>生成所有 9 个 DB 块的数据</summary>
    public static Dictionary<string, byte[]> GenerateAll()
    {
        return new()
        {
            ["PLC1.DB1"] = GeneratePlc1_Status(),
            ["PLC1.DB2"] = GeneratePlc1_Requests(),
            ["PLC1.DB3"] = GeneratePlc1_Alarms(),
            ["PLC2.DB1"] = GeneratePlc2_Status(),
            ["PLC2.DB2"] = GeneratePlc2_Requests(),
            ["PLC2.DB3"] = GeneratePlc2_Alarms(),
            ["PLC3.DB1"] = GeneratePlc3_Status(),
            ["PLC3.DB2"] = GeneratePlc3_Requests(),
            ["PLC3.DB3"] = GeneratePlc3_Alarms(),
        };
    }
}
