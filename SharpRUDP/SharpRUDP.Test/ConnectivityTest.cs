using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;

namespace SharpRUDP.Test
{
    [TestClass]
    public class Connectivity
    {
        [TestMethod, Timeout(5000)]
        public void ConnectAndDisconnect()
        {
            RUDPConnection s = new RUDPConnection();
            RUDPConnection c = new RUDPConnection();
            s.Listen("127.0.0.1", 80);
            c.Connect("127.0.0.1", 80);
            while (c.State != ConnectionState.OPEN)
                Thread.Sleep(10);
            Assert.AreEqual(ConnectionState.OPEN, c.State);
            s.Disconnect();
            c.Disconnect();
            while (c.State != ConnectionState.CLOSED && s.State != ConnectionState.CLOSED)
                Thread.Sleep(10);
            Assert.AreEqual(ConnectionState.CLOSED, s.State);
            Assert.AreEqual(ConnectionState.CLOSED, c.State);
        }

        public void ConnectAndDisconnectSync()
        {
            RUDPConnection s = new RUDPConnection();
            RUDPConnection c = new RUDPConnection();

            s.Listen("127.0.0.1", 80);
            c.Connect("127.0.0.1", 80);

            while(true)
            {
                if (c.State == ConnectionState.OPEN)
                    break;
                Thread.Sleep(1000);
            }

            Assert.AreEqual(ConnectionState.OPEN, c.State);

            s.Disconnect();
            c.Disconnect();

            while (true)
            {
                if (c.State == ConnectionState.CLOSED && s.State == ConnectionState.CLOSED)
                    break;
                Thread.Sleep(1000);
            }

            Assert.AreEqual(ConnectionState.CLOSED, s.State);
            Assert.AreEqual(ConnectionState.CLOSED, c.State);
        }
    }
}
