using Snap7;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;


namespace Wcs.Core.PlcSubsystem.SignalMapper.S7
{
    #region 封装的实体类
    public static class Struct
    {
        /// <summary>
        /// Creates a struct of a specified type by an array of bytes.
        /// </summary>
        /// <param name="structType">The struct type</param>
        /// <param name="bytes">The array of bytes</param>
        /// <returns>The object depending on the struct type or null if fails(array-length != struct-length</returns>
        public static object FromBytes(Type structType, byte[] bytes, int Endcount, int startcount)
        {
            if (bytes == null)
                return null;

            // and decode it
            int bytePos = 0;
            int bitPos = 0;
            double numBytes = 0.0;
            numBytes = startcount;
            //使用与指定参数匹配程度最高的构造函数来创建指定类型的实例。
            object structValue = Activator.CreateInstance(structType);


            var infos = structValue.GetType()
#if NETSTANDARD1_3
                .GetTypeInfo().DeclaredFields;
#else
                .GetFields();
#endif

            foreach (var info in infos)
            {
                switch (info.FieldType.Name)
                {
                    case "Boolean":
                        // get the value
                        bytePos = (int)Math.Floor(numBytes);
                        bitPos = (int)((numBytes - (double)bytePos) / 0.125);
                        if ((bytes[bytePos] & (int)Math.Pow(2, bitPos)) != 0)
                            info.SetValue(structValue, true);
                        else
                            info.SetValue(structValue, false);
                        numBytes += 0.125;
                        break;
                    case "Byte":
                        numBytes = Math.Ceiling(numBytes);
                        info.SetValue(structValue, (bytes[(int)numBytes]));
                        numBytes++;
                        break;
                    case "Char":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        Int16 SInt16 = Convert.ToInt16(numBytes);
                        Int32 soureSInt16 = Snap7.S7.GetSIntAt(bytes, SInt16);
                        info.SetValue(structValue, soureSInt16);
                        numBytes++;
                        break;
                    case "Int16":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iInt16 = Convert.ToInt16(numBytes);
                        short sourceInt16 = (short)Snap7.S7.GetIntAt(bytes, iInt16);
                        //string  strInt16 = sourceInt16.ToString();
                        // info.SetValue(structValue, short.Parse(strInt16));
                        info.SetValue(structValue, sourceInt16);
                        numBytes += 2;
                        break;
                    case "UInt16":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iUInt16 = Convert.ToInt16(numBytes);
                        ushort soureUInt16 = Snap7.S7.GetWordAt(bytes, iUInt16);
                        info.SetValue(structValue, soureUInt16);
                        numBytes += 2;
                        break;
                    case "Int32":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iInt32 = Convert.ToInt16(numBytes);
                        Int32 sourceInt32 = Snap7.S7.GetDIntAt(bytes, iInt32);
                        info.SetValue(structValue, sourceInt32);
                        numBytes += 4;
                        break;
                    case "UInt32":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iUInt32 = Convert.ToInt16(numBytes);
                        UInt32 sourceUInt32 = Snap7.S7.GetDWordAt(bytes, iUInt32);
                        info.SetValue(structValue, sourceUInt32);
                        numBytes += 4;
                        break;
                    case "Single":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iSingle = Convert.ToInt16(numBytes);
                        float sourceSingle = Snap7.S7.GetRealAt(bytes, iSingle);
                        info.SetValue(structValue, sourceSingle);
                        numBytes += 4;
                        break;
                    case "Double":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        // get the value
                        Int16 iDouble = Convert.ToInt16(numBytes);
                        double sourceDouble = Snap7.S7.GetLRealAt(bytes, iDouble);
                        info.SetValue(structValue, sourceDouble);
                        numBytes += 8;
                        break;
                    case "String":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        //获取当前字符串开始位置
                        Int16 iString = Convert.ToInt16(numBytes);
                        // get the value
                        S7StringAttribute attribute = info.GetCustomAttributes<S7StringAttribute>().SingleOrDefault();
                        var sData = new byte[attribute.ReservedLengthInBytes];
                        Array.Copy(bytes, (int)numBytes, sData, 0, sData.Length);
                        string sourceString = Snap7.S7.GetStringAt(bytes, iString);
                        info.SetValue(structValue, sourceString);
                        //numBytes += sourceString.Length;
                        numBytes += sData.Length;
                        break;
                    case "String[]":
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        //获取当前字符串开始位置
                        Int16 iString2 = Convert.ToInt16(numBytes);
                        string sourceString2 = Snap7.S7.GetStringAt(bytes, iString2, 10);
                        info.SetValue(structValue, sourceString2);
                        numBytes += sourceString2.Length;
                        //numBytes += sData.Length;
                        break;
                    case "DateTime":
                        S7Timer attributetime = info.GetCustomAttributes<S7Timer>().SingleOrDefault();
                        if (attributetime == default(S7Timer))
                            throw new ArgumentException("Please add S7TimerAttribute to the time field");

                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        Int16 iDate = Convert.ToInt16(numBytes);
                        // get the value
                        var stime = new byte[attributetime.ReservedLengthInBytes];
                        DateTime dateTime;
                        switch (attributetime.Type)
                        {
                            case S7TimerType.Date_And_Time:
                                dateTime = Snap7.S7.GetDateTimeAt(bytes, iDate);
                                info.SetValue(structValue, dateTime);
                                break;
                            case S7TimerType.DTL:
                                dateTime = Snap7.S7.GetDTLAt(bytes, iDate);
                                info.SetValue(structValue, dateTime);
                                break;
                            case S7TimerType.Time_Of_Day:
                                dateTime = Snap7.S7.GetTODAt(bytes, iDate);
                                info.SetValue(structValue, dateTime);
                                break;
                            case S7TimerType.Date:
                                dateTime = Snap7.S7.GetDateAt(bytes, iDate);
                                info.SetValue(structValue, dateTime);
                                break;
                            default:
                                throw new ArgumentException("Please use a valid time type for the S7TimerAttribute");
                        }
                        numBytes += stime.Length;
                        break;

                    default:
                        var buffer = new byte[Endcount - startcount];
                        if (buffer.Length == 0)
                            continue;
                        Buffer.BlockCopy(bytes, (int)Math.Ceiling(numBytes), buffer, 0, buffer.Length);
                        info.SetValue(structValue, FromBytes(info.FieldType, buffer, Endcount, startcount));
                        numBytes += buffer.Length;
                        break;
                }
            }
            return structValue;
        }

        /// <summary>
        /// Creates a byte array depending on the struct type.
        /// </summary>
        /// <param name="structValue">The struct object</param>
        /// <returns>A byte array or null if fails.</returns>
        public static byte[] ToBytes(object structValue, int startcount, int endcount)
        {
            //Type type = structValue.GetType();
            int size = endcount - startcount;
            byte[] bytes = new byte[size];
#pragma warning disable CS0219 // 变量“bytes2”已被赋值，但从未使用过它的值
            byte[] bytes2 = null;
#pragma warning restore CS0219 // 变量“bytes2”已被赋值，但从未使用过它的值

            int bytePos = 0;
            int bitPos = 0;
            double numBytes = 0.0;
            numBytes = startcount;
            var infos = structValue.GetType()
#if NETSTANDARD1_3
               .GetTypeInfo().DeclaredFields;
#else
               .GetFields();
#endif

            foreach (var info in infos)
            {
                // bytes2 = null;
                //switch (info.FieldType.Name)
                switch (info.FieldType.Name)
                {
                    case "Boolean":
                        // get the value
                        bytePos = (int)Math.Floor(numBytes);
                        bitPos = (int)((numBytes - (double)bytePos) / 0.125);
                        if ((bool)info.GetValue(structValue))
                            bytes[bytePos] |= (byte)Math.Pow(2, bitPos);            // is true
                        else
                            bytes[bytePos] &= (byte)(~(byte)Math.Pow(2, bitPos));   // is false
                        numBytes += 0.125;
                        break;
                    case "Byte":
                        numBytes = (int)Math.Ceiling(numBytes);
                        bytePos = (int)numBytes;
                        bytes[bytePos] = (byte)info.GetValue(structValue);
                        numBytes++;
                        break;
                    case "Char":
                        numBytes = (int)Math.Ceiling(numBytes);
                        bytePos = (int)numBytes;
                        Snap7.S7.SetSIntAt(bytes, bytePos, (char)info.GetValue(structValue));
                        numBytes++;
                        break;
                    case "Int16":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetIntAt(bytes, bytePos, (Int16)info.GetValue(structValue));
                        numBytes += 2;
                        break;
                    case "UInt16":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetWordAt(bytes, bytePos, (UInt16)info.GetValue(structValue));
                        numBytes += 2;
                        break;
                    case "Int32":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetDIntAt(bytes, bytePos, (Int32)info.GetValue(structValue));
                        numBytes += 4;
                        break;
                    case "UInt32":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetDWordAt(bytes, bytePos, (UInt32)info.GetValue(structValue));
                        numBytes += 4; ;
                        break;
                    case "Single":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetRealAt(bytes, bytePos, (float)info.GetValue(structValue));
                        numBytes += 4;
                        break;
                    case "Double":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        Snap7.S7.SetLRealAt(bytes, bytePos, (Double)info.GetValue(structValue));
                        numBytes += 8;
                        break;
                    case "String":
                        numBytes = (int)Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        S7StringAttribute attribute = info.GetCustomAttributes<S7StringAttribute>().SingleOrDefault();
                        if (attribute == default(S7StringAttribute))
                            throw new ArgumentException("Please add S7StringAttribute to the string field");
                        var sData = new byte[attribute.ReservedLengthInBytes];
                        Array.Copy(bytes, (int)numBytes, sData, 0, sData.Length);
                        string SetString = (string)info.GetValue(structValue);
                        Snap7.S7.SetStringAt(bytes, bytePos, sData.Length, (string)info.GetValue(structValue));
                        //numBytes += ((string)info.GetValue(structValue)).Length;
                        numBytes += sData.Length;
                        break;
                    case "DateTime":
                        S7Timer attributetime = info.GetCustomAttributes<S7Timer>().SingleOrDefault();
                        if (attributetime == default(S7Timer))
                            throw new ArgumentException("Please add S7TimerAttribute to the time field");
                        numBytes = Math.Ceiling(numBytes);
                        if ((numBytes / 2 - Math.Floor(numBytes / 2.0)) > 0)
                            numBytes++;
                        bytePos = (int)numBytes;
                        // get the value
                        var stime = new byte[attributetime.ReservedLengthInBytes];
                        switch (attributetime.Type)
                        {
                            case S7TimerType.Date_And_Time:
                                Snap7.S7.SetDateTimeAt(bytes, bytePos, (DateTime)info.GetValue(structValue));
                                break;
                            case S7TimerType.DTL:
                                Snap7.S7.SetDTLAt(bytes, bytePos, (DateTime)info.GetValue(structValue));
                                break;
                            case S7TimerType.Time_Of_Day:
                                Snap7.S7.SetTODAt(bytes, bytePos, (DateTime)info.GetValue(structValue));
                                break;
                            case S7TimerType.Date:
                                Snap7.S7.SetDateAt(bytes, bytePos, (DateTime)info.GetValue(structValue));
                                break;
                            default:
                                throw new ArgumentException("Please use a valid time type for the S7TimerAttribute");
                        }
                        numBytes += stime.Length;
                        break;

                }

            }
            return bytes;
        }

        public static int GetStructSize<T>()
        {
            int size = 0;

            foreach (var field in typeof(T).GetFields())
            {
                if (field.FieldType == typeof(string))
                {
                    // 获取 S7String 特性
                    var attribute = (S7StringAttribute)Attribute.GetCustomAttribute(field, typeof(S7StringAttribute));
                    if (attribute != null && attribute.Type == S7StringType.S7String)
                    {
                        size += attribute.ReservedLengthInBytes; // 添加字符串的长度
                    }
                }
                else
                {
                    size += Marshal.SizeOf(field.FieldType); // 添加其他字段的长度
                }
            }

            return size;
        }
    }
    #endregion

}
