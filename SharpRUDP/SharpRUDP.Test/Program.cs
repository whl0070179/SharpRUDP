using System;
using System.Threading;

namespace SharpRUDP.Test
{
    class Program
    {
        static void Main(string[] args)
        {
            RUDPServer s = new RUDPServer();
            s.Listen("127.0.0.1", 80);
            Console.WriteLine("Server started");

            Console.ReadKey();

            RUDPClient c = new RUDPClient();
            c.Connect("127.0.0.1", 80);
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

            for (int i = 0; i < 50; i++)
                c.Send(i.ToString());
            c.Send("LONGLONGLONG1");
            Thread.Sleep(315);
            c.Send("LONGLONGLONG2");
            Thread.Sleep(315);
            c.Send("LONGLONGLONG3");
            Thread.Sleep(315);
            c.Send("LONGLONGLONG4");
            Thread.Sleep(315);
            c.Send("LONGLONGLONG5");
            for (int i = 0; i < 5; i++)
                c.Send(i.ToString());

            Thread.Sleep(2000);
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
