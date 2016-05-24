using System;

namespace SharpRUDP.LiveTest
{
    class Program
    {
        static void Main(string[] args)
        {
            new Test.Connectivity().ConnectAndDisconnect(); Wait();
            new Test.SmallPacketTest().SmallPacket(); Wait();
            new Test.MediumPacketTest().MediumPacket(); Wait();
            new Test.MultiPacketSmallTest().MultiPacketSmall(); Wait();
            new Test.MultiPacketMediumTest().MultiPacketMedium(); Wait();
        }

        static void Wait()
        {
            //Console.ReadLine();
        }
    }
}
