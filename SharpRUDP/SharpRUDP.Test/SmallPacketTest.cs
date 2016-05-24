using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Text;
using System.Threading;

namespace SharpRUDP.Test
{
    [TestClass]
    public class SmallPacketTest
    {
        [TestMethod, Timeout(10000)]
        public void SmallPacket()
        {
            int maxPackets = 500;
            bool finished = false;

            RUDPConnection s = new RUDPConnection();
            RUDPConnection c = new RUDPConnection();
            s.Listen("127.0.0.1", 80);
            c.Connect("127.0.0.1", 80);
            while (c.State != ConnectionState.OPEN)
                Thread.Sleep(10);
            Assert.AreEqual(ConnectionState.OPEN, c.State);

            int counter = 0;
            s.OnPacketReceived += (RUDPPacket p) =>
            {
                Assert.AreEqual(counter, int.Parse(Encoding.ASCII.GetString(p.Data)));
                counter++;                
                if (counter >= maxPackets)
                    finished = true;
            };

            Random r = new Random(DateTime.Now.Second);
            for (int i = 0; i < maxPackets; i++)
            {
                Thread.Sleep(1 * r.Next(0, 10));
                c.Send(i.ToString());
            }

            while (!finished)
                Thread.Sleep(10);

            counter = 0;
            finished = false;
            for (int i = 0; i < maxPackets; i++)
                c.Send(i.ToString());

            while (!finished)
                Thread.Sleep(10);

            s.Disconnect();
            c.Disconnect();
            while (c.State != ConnectionState.CLOSED && s.State != ConnectionState.CLOSED)
                Thread.Sleep(10);
            Assert.AreEqual(ConnectionState.CLOSED, s.State);
            Assert.AreEqual(ConnectionState.CLOSED, c.State);
        }
    }
}
