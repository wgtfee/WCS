using System;
using System.Linq;


namespace Wcs.Core.PlcSubsystem.SignalMapper.S7
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public sealed class S7StringAttribute : Attribute
    {
        private readonly S7StringType type;
        private readonly int reservedLength;

        /// <summary>
        /// Initializes a new instance of the <see cref="S7StringAttribute"/> class.
        /// </summary>
        /// <param name="type">The string type.</param>
        /// <param name="reservedLength">Reserved length of the string in characters.</param>
        /// <exception cref="ArgumentException">Please use a valid value for the string type</exception>请为字符串类型使用有效值
        public S7StringAttribute(S7StringType type, int reservedLength)
        {
            if (!Enum.IsDefined(typeof(S7StringType), type))
                throw new ArgumentException("Please use a valid value for the string type");

            this.type = type;
            this.reservedLength = reservedLength;
        }

        /// <summary>
        /// Gets the type of the string.获取字符串的类型。
        /// </summary>
        /// <value>
        /// The string type.
        /// </value>
        public S7StringType Type => type;

        /// <summary>
        /// Gets the reserved length of the string in characters.获取字符串的保留长度（以字符为单位）。
        /// </summary>
        /// <value>
        /// The reserved length of the string in characters.字符串的保留长度（以字符为单位）。
        /// </value>
        public int ReservedLength => reservedLength;

        /// <summary>
        /// Gets the reserved length in bytes.获取保留的长度（以字节为单位）
        /// </summary>
        /// <value>
        /// The reserved length in bytes.保留的长度（以字节为单位）。
        /// </value>
        // public int ReservedLengthInBytes => type == S7StringType.S7String ? reservedLength + 2 : (reservedLength * 2) + 4;
        public int ReservedLengthInBytes => type == S7StringType.S7String ? reservedLength + 2 : reservedLength * 2 + 4;
    }


    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public sealed class S7Timer : Attribute
    {
        private readonly S7TimerType type;

        /// <summary>
        /// Initializes a new instance of the <see cref="S7Timer"/> class.
        /// </summary>
        /// <param name="type">The string type.</param>
        /// <param name="reservedLength">Reserved length of the string in characters.</param>
        /// <exception cref="ArgumentException">Please use a valid value for the string type</exception>请为字符串类型使用有效值
        public S7Timer(S7TimerType type)
        {
            if (!Enum.IsDefined(typeof(S7TimerType), type))
                throw new ArgumentException("Please use a valid value for the Timer type");

            this.type = type;
            //this.reservedLength = reservedLength;
        }

        /// <summary>
        /// Gets the type of the string.获取字符串的类型。
        /// </summary>
        /// <value>
        /// The string type.
        /// </value>
        public S7TimerType Type => type;

        /// <summary>
        /// Gets the reserved length of the string in characters.获取字符串的保留长度（以字符为单位）。
        /// </summary>
        /// <value>
        /// The reserved length of the string in characters.字符串的保留长度（以字符为单位）。
        /// </value>
        public int ReservedLength => Convert.ToInt32(Type);

        /// <summary>
        /// Gets the reserved length in bytes.获取保留的长度（以字节为单位）
        /// </summary>
        /// <value>
        /// The reserved length in bytes.保留的长度（以字节为单位）。
        /// </value>

        public int ReservedLengthInBytes => Convert.ToInt32(Type);//也可以把reservedLength写死为8。要是在加枚举类型就不能用三元运算符

    }





    /// <summary>
    /// String type.
    /// </summary>
    public enum S7TimerType
    {
        /// <summary>
        /// ASCII DTL.
        /// </summary>
        DTL = 12,

        /// <summary>
        /// ASCII Date_And_Time.
        /// </summary>
        Date_And_Time = 8,
        /// <summary>
        /// ASCII Time_of_Day.
        /// </summary>
        Time_Of_Day = 4,
        /// <summary>
        /// ASCII Date.
        /// </summary>
        Date = 2
    }


    /// <summary>
    /// String type.
    /// </summary>
    public enum S7StringType
    {
        /// <summary>
        /// ASCII string.
        /// </summary>
        S7String,

        /// <summary>
        /// Unicode string.
        /// </summary>
        S7WString
    }
}
