using System;

namespace SharpRUDP
{
    public static class RUDPLogger
    {
        public enum RUDPLoggerLevel
        {
            Info = 1,
            Trace = 0
        }

        public static bool IsServer = false;
        private static object _debugMutex = new object();
        public static RUDPLoggerLevel LogLevel = RUDPLoggerLevel.Info;

        public static void Trace(object obj, params object[] args)
        {
            if (LogLevel <= RUDPLoggerLevel.Trace)
                Write(obj, args);
        }

        public static void Info(object obj, params object[] args)
        {
            if (LogLevel <= RUDPLoggerLevel.Info)
                Write(obj, args);
        }

        private static void Write(object obj, params object[] args)
        {
            lock (_debugMutex)
            {
                if (obj.GetType() == typeof(string))
                    Console.WriteLine(string.Format("{0} {1}", IsServer ? "[S]" : "[C]", string.Format((string)obj, args)));
                else
                    Console.WriteLine(string.Format("{0} {1}", IsServer ? "[S]" : "[C]", obj.ToString()));
            }
        }
    }
}
