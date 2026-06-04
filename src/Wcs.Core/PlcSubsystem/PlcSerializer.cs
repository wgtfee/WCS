namespace Wcs.Core.PlcSubsystem;

using System.Reflection;

public static class PlcSerializer
{
    public static byte[] Serialize(object obj, int bufferSize)
    {
        var buffer = new byte[bufferSize];
        var type = obj.GetType();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<PlcOffsetAttribute>();
            if (attr == null) continue;
            WriteValue(buffer, attr.ByteOffset, attr.BitOffset, prop.PropertyType, prop.GetValue(obj));
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = field.GetCustomAttribute<PlcOffsetAttribute>();
            if (attr == null) continue;
            WriteValue(buffer, attr.ByteOffset, attr.BitOffset, field.FieldType, field.GetValue(obj));
        }
        return buffer;
    }

    public static int CalculateBufferSize<T>() => CalculateBufferSize(typeof(T));
    public static int CalculateBufferSize(Type type)
    {
        var maxEnd = 0;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<PlcOffsetAttribute>();
            if (attr == null) continue;
            maxEnd = Math.Max(maxEnd, attr.ByteOffset + GetTypeSize(prop.PropertyType));
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = field.GetCustomAttribute<PlcOffsetAttribute>();
            if (attr == null) continue;
            maxEnd = Math.Max(maxEnd, attr.ByteOffset + GetTypeSize(field.FieldType));
        }
        return maxEnd;
    }

    private static void WriteValue(byte[] buffer, int byteOffset, int bitOffset, Type type, object? value)
    {
        if (value == null) return;
        if (type == typeof(bool))
        {
            if (bitOffset >= 0 && bitOffset <= 7)
            {
                if ((bool)value) buffer[byteOffset] |= (byte)(1 << bitOffset);
                else buffer[byteOffset] &= (byte)~(1 << bitOffset);
            }
            else buffer[byteOffset] = (bool)value ? (byte)1 : (byte)0;
            return;
        }
        if (type == typeof(byte)) { buffer[byteOffset] = (byte)value; return; }
        if (type == typeof(short) || type == typeof(ushort))
        {
            if (byteOffset + 2 <= buffer.Length)
            {
                var b = BitConverter.GetBytes((short)value);
                buffer[byteOffset] = b[0]; buffer[byteOffset + 1] = b[1];
            }
            return;
        }
        if (type == typeof(int) || type == typeof(uint))
        {
            if (byteOffset + 4 <= buffer.Length)
                Array.Copy(BitConverter.GetBytes((int)value), 0, buffer, byteOffset, 4);
            return;
        }
    }

    private static int GetTypeSize(Type type)
    {
        if (type == typeof(bool)) return 1;
        if (type == typeof(byte)) return 1;
        if (type == typeof(short) || type == typeof(ushort)) return 2;
        if (type == typeof(int) || type == typeof(uint)) return 4;
        return 1;
    }
}
