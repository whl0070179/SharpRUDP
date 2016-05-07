using System;

namespace SharpRUDP
{
    public static class RUDPLogger
    {
        public enum RUDPLoggerLevel
        {
            Trace = 0,
            Info = 1,
            None = 99,
        }

        private static object _debugMutex = new object();
        public static RUDPLoggerLevel LogLevel = RUDPLoggerLevel.Info;

        public static void Trace(string prefix, object obj, params object[] args)
        {
            if (LogLevel <= RUDPLoggerLevel.Trace)
                Write(prefix, obj, args);
        }

        public static void Info(string prefix, object obj, params object[] args)
        {
            if (LogLevel <= RUDPLoggerLevel.Info)
                Write(prefix, obj, args);
        }

        private static void Write(string prefix, object obj, params object[] args)
        {
            lock (_debugMutex)
            {
                if (obj.GetType() == typeof(string))
                    Console.WriteLine(string.Format("{0} {1}", prefix, string.Format((string)obj, args)));
                else
                    Console.WriteLine(string.Format("{0} {1}", prefix, obj.ToString()));
            }
        }
    }
}
