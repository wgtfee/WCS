using System;
using System.Linq;

namespace Wcs.Core.PlcSubsystem.SignalMapper.S7
{
    public class S7PLC_RW
    {
        public static string PLCname;
        public static short Solt;
        public static string IPaddress;

        public static Snap7.S7Client Client;


        //连接PLC,S7通讯
        public static int ConnectPLC(string IPAddress, ref string ErrorText)
        {
            Client = new Snap7.S7Client();
            int Rack = 0;//机架
            int Slot = 0;//插槽
            int Result = -1;
            if (Client != null)
            {
                Result = Client.ConnectTo(IPAddress, Rack, Slot);
                ErrorText = Client.ErrorText(Result);
                if (Result == 0)
                {
                    return 0;
                }
                else
                {
                    if (Client.Connect() == 0)
                    {
                        Result = Client.Connect();
                        return Result;
                    }
                }
            }
            return 1;
        }
        /// <summary>
        /// 断开PLC连接
        /// </summary>
        public static void DisConnectPLC()
        {
            if (Client.Connected())
            {
                Client.Disconnect();
            }
        }

        /// <summary>
        /// 数据读取
        /// </summary>
        /// <param name="count">读取数量</param>
        /// <param name="db">db块</param>
        ///  <param name="startByteAdr">起始读取位</param>
        public static Byte[] ReadPLCData(int db, int startByteAdr, int count, ref bool ConnectStatus, ref string ErrorText)
        {
            string ErrStr = string.Empty;
            var bytes = new byte[count];
            int Result = -1;
            if (Client.Connected())
            //if (Client.Connect()==0)
            {
                ConnectStatus = true;
                //提取数据，超过200字节
                //int Result = ReadMultipleBytes(count, db, bytes, startByteAdr);
                Result = Client.ReadArea(0x84, db, 0, count, 0x2, bytes);
                ErrorText = string.Format(Client.ErrorText(Result) + "\0" + "返回数值{0}", Result);
            }
            else
            {
                ConnectStatus = false;
            }
            return bytes;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="db">数据块</param>
        /// <param name="startByteAdr">写入数据起始位置</param>
        /// <param name="count">写入数据长度</param>
        /// <param name="WriteData">写入的对象</param>
        /// <param name="ConnectStatus">表示PLC连接状态</param>
        /// <returns>成功（0）或者失败（非0）</returns>
        public static int WritePLCData(int db, int startByteAdr, int count, Object WriteData, ref bool ConnectStatus, ref string ErrorText)
        {
            //string ErrStr = string.Empty;
            int Result = -1;
            if (Client.Connected())
            //if (Client.Connect() == 0)
            {
                ConnectStatus = true;
                byte[] bytes2 = new byte[count - startByteAdr];
                bytes2 = Struct.ToBytes(WriteData, startByteAdr, count);
                Result = Client.WriteArea(0x84, db, startByteAdr, count - startByteAdr, 0x2, bytes2);
                ErrorText = string.Format(Client.ErrorText(Result) + "\0" + "返回数值{0}", Result);
                if (Result == 0)
                {

                }
                else
                {
                    if (Client.Connect() == 0)
                    {
                        Result = Client.WriteArea(0x84, db, startByteAdr, count - startByteAdr, 0x2, bytes2);
                    }
                }
                ErrorText = string.Format(Client.ErrorText(Result) + "\0" + "返回数值{0}", Result);
            }
            else
            {
                ConnectStatus = false;
            }
            return Result;
        }

        public static int WritePLCDatas(int db, int startByteAdr, int count, byte[] WriteData, ref bool ConnectStatus, ref string ErrorText)
        {
            //string ErrStr = string.Empty;
            int Result = -1;
            if (Client.Connected())
            //if (Client.Connect() == 0)
            {
                ConnectStatus = true;
                byte[] bytes2 = new byte[count - startByteAdr];
                Result = Client.WriteArea(0x84, db, startByteAdr, count - startByteAdr, 0x2, WriteData);
                if (Result == 0)
                {

                }
                else
                {
                    if (Client.Connect() == 0)
                    {
                        Result = Client.WriteArea(0x84, db, startByteAdr, count - startByteAdr, 0x2, bytes2);
                    }
                }
                ErrorText = string.Format(Client.ErrorText(Result) + "\0" + "返回数值{0}", Result);

            }
            else
            {
                ConnectStatus = false;
            }
            return Result;

        }

        public static int WriteMultipleBytes(int numBytes, int db, byte[] bytes, int startByteAdr = 0)
        {
            //byte[] resultBytes = new byte[numBytes];
            //resultBytes = bytes;    

            int index = startByteAdr;
            int Result = 0;
            while (numBytes > 0)
            {   //写入小的数
                byte[] resultBytes = new byte[numBytes];
                var maxToRead = Math.Min(numBytes, 200);
                Array.Copy(bytes, index, resultBytes, 0, maxToRead);
                //byte[] bytes = ReadBytes(DataType.DataBlock, db, index, (int)maxToRead);
                //Result = Client.WriteArea(0x84, db, index, maxToRead, 0x2, resultBytes);
                Result = Client.WriteArea(0x84, db, index, maxToRead, 0x2, resultBytes);
                //if (bytes == null)
                // { Array.Copy(bytes, (int)numBytes, sData, 0, sData.Length); }

                numBytes -= maxToRead;
                index += maxToRead;
            }
            return Result;
        }


        /// <summary>
        /// 返回耗时时间
        /// </summary>
        /// <param name="numBytes"></param>
        /// <param name="db"></param>
        /// <param name="bytes"></param>
        /// <param name="startByteAdr"></param>
        /// <returns></returns>
        public static int ReadMultipleBytes(int numBytes, int db, byte[] bytes, int startByteAdr = 0)
        {
            // byte[] resultByte = new byte[numBytes];
            //resultBytes = bytes;    
            int num = numBytes;
            int index = startByteAdr;
            int Result = 0;
            while (numBytes > 0)
            {   //写入小的数
                byte[] resultByte1 = new byte[numBytes];
                byte[] resultByte2 = new byte[num];
                var maxToRead = Math.Min(numBytes, 900);
                //Array.Copy(bytes, index, resultBytes, 0, maxToRead);

                //byte[] bytes = ReadBytes(DataType.DataBlock, db, index, (int)maxToRead);
                //Result = Client.WriteArea(0x84, db, index, maxToRead, 0x2, resultBytes);
                Result = Client.ReadArea(0x84, db, index, maxToRead, 0x2, resultByte1);
                //if (bytes == null)
                // { Array.Copy(bytes, (int)numBytes, sData, 0, sData.Length); }
                Array.Copy(resultByte1, 0, bytes, index, maxToRead);
                numBytes -= maxToRead;
                index += maxToRead;
            }
            return Result;

        }



        public static bool PLC_BArea_status()
        {
            string ErrText = string.Empty;
            string IPAddress = string.Empty;
            IPAddress = "172.21.79.14";
            Snap7.S7Client s7Client = new Snap7.S7Client();
            int result = s7Client.ConnectTo(IPAddress, 0, 0);
            bool outresult = false;
            if (result == 0)
            {
                byte[] byteint = new byte[1];
                result = s7Client.ReadArea(0x84, 9, 0, 2, 0x2, byteint);
                if (result == 0)
                    outresult = Snap7.S7.GetBitAt(byteint, 0, 0);
            }
            s7Client.Disconnect();
            return outresult;
        }

    }
}
