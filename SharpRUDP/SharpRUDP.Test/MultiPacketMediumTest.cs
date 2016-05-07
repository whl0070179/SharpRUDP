using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Text;
using System.Threading;

namespace SharpRUDP.Test
{
    [TestClass]
    public class MultiPacketMediumTest
    {
        [TestMethod, Timeout(30000)]
        public void MultiPacketMedium()
        {
            bool finished = false;

            RUDPConnection s = new RUDPConnection();
            RUDPConnection c = new RUDPConnection();
            s.Listen("127.0.0.1", 80);
            c.Connect("127.0.0.1", 80);
            while (c.State != ConnectionState.OPEN)
                Thread.Sleep(10);
            Assert.AreEqual(ConnectionState.OPEN, c.State);

            byte[] buf = new byte[16 * 1024];
            Random r = new Random(DateTime.Now.Second);
            r.NextBytes(buf);

            int counter = 0;
            s.OnPacketReceived += (RUDPPacket p) =>
            {
                Assert.IsTrue(p.Data.SequenceEqual(buf));
                counter++;
                if (counter >= 500)
                    finished = true;
            };

            for (int i = 0; i < 500; i++)
            {
                Thread.Sleep(3 * r.Next(0, 10));
                c.Send(c.RemoteEndPoint, RUDPPacketType.DAT, RUDPPacketFlags.NUL, buf);
            }

            while (!finished)
                Thread.Sleep(10);

            counter = 0;
            finished = false;
            for (int i = 0; i < 500; i++)
                c.Send(c.RemoteEndPoint, RUDPPacketType.DAT, RUDPPacketFlags.NUL, buf);

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
