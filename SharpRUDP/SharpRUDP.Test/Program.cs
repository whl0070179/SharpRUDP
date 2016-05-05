using System;
using System.Threading;

namespace SharpRUDP.Test
{
    class Program
    {
        static void Main(string[] args)
        {
            RUDPLogger.LogLevel = RUDPLogger.RUDPLoggerLevel.Info;

            RUDPConnection s = new RUDPConnection();
            s.Listen("127.0.0.1", 80);
            Console.WriteLine("Server started");

            Console.ReadKey();

            RUDPConnection c = new RUDPConnection();
            c.Connect("127.0.0.1", 80);

            //Console.ReadKey(); c.Send(":D"); c.Send(":D");

            /*
            Console.WriteLine("====================================");
            Console.WriteLine("COMMON SEND");
            Console.WriteLine("====================================");
            for (int i = 0; i < 2; i++)
                c.Send(i.ToString());

            Console.ReadKey();

            Console.WriteLine("====================================");
            Console.WriteLine("KEEPALIVE");
            Console.WriteLine("====================================");
            s.SendKeepAlive();

            Console.ReadKey();

            Console.WriteLine("====================================");
            Console.WriteLine("OUT OF ORDER SIMULATION");
            Console.WriteLine("====================================");
            Thread.Sleep(2000);

            s.Disconnect();
            s.Listen("127.0.0.1", 80);

            Thread.Sleep(1000);
            for (int i = 0; i < 5; i++)
                c.Send(i.ToString());

            Console.ReadKey();

            Console.WriteLine("====================================");
            Console.WriteLine("PACKET OVERFLOW SIMULATION");
            Console.WriteLine("====================================");
            Thread.Sleep(2000);

            for (int i = 0; i < 20; i++)
                c.Send(i.ToString());

            Console.ReadKey();
            Console.WriteLine("====================================");
            Console.WriteLine("PACKET OVERFLOW SIMULATION (PART 2)");
            Console.WriteLine("====================================");

            for (int i = 0; i < 5; i++)
                c.Send(i.ToString());
            */
            Console.ReadKey();
            Console.WriteLine("====================================");
            Console.WriteLine("MULTI SPLIT PACKET");
            Console.WriteLine("====================================");

            //byte[] buffer = new byte[1 * 1024];
            c.MTU = 8;
            byte[] buffer = new byte[512];
            Random r = new Random();
            r.NextBytes(buffer);

            Console.WriteLine("{0}", c.RemoteEndPoint);
            c.Send(c.RemoteEndPoint, RUDPPacketType.DAT, RUDPPacketFlags.NUL, buffer);

            Console.ReadKey();
            Console.WriteLine("====================================");
            Console.WriteLine("END OF TESTS, PRESS ANY KEY");
            Console.WriteLine("====================================");
            Console.ReadKey();

            s.Disconnect();
            c.Disconnect();

            // TODO:
            // Split by channel
            // Channel Sequential / Unordered yet Reliable / Unreliable

            Console.WriteLine("Finished");
            Console.ReadKey();
        }
    }
}
