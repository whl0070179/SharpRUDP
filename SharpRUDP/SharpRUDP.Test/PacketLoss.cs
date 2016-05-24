using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace SharpRUDP.Test
{
    [TestClass]
    public class PacketLoss
    {

        [TestMethod]
        public void PacketLossTestSync()
        {
            RUDPConnection s = new RUDPConnection();
            RUDPConnection c = new RUDPConnection();

            s.AsyncMode = false;
            c.AsyncMode = false;

            s.Listen("127.0.0.1", 80);
            c.Connect("127.0.0.1", 80);

            while (true)
            {
                c.Tick();
                s.Tick();
                if (c.State == ConnectionState.OPEN)
                    break;
                Thread.Sleep(10);
            }

            Assert.AreEqual(ConnectionState.OPEN, c.State);

            int counter = 0;
            int maxPackets = 5;
            bool finished = false;

            s.OnPacketReceived += (RUDPPacket p) =>
            {
                Assert.AreEqual(counter, int.Parse(Encoding.ASCII.GetString(p.Data)));
                counter++;
                if (counter >= maxPackets)
                    finished = true;
            };

            counter = 0;
            finished = false;
            for (int i = 0; i < maxPackets; i++)
                c.Send(i.ToString());

            bool packetLoss = false;

            while (!finished)
            {
                c.Tick();
                if(!packetLoss)
                {
                    List<RUDPPacket> newPackets = new List<RUDPPacket>();
                    while (s._recvQueue.Count > 0)
                        newPackets.Add(s._recvQueue.Dequeue());
                    newPackets.RemoveAt(0);
                    s._recvQueue = new Queue<RUDPPacket>(newPackets);
                    packetLoss = true;
                }
                Console.ReadKey();
                s.Tick();
                Console.ReadKey();
            }

            s.Disconnect();
            c.Disconnect();

            while (true)
            {
                c.Tick();
                s.Tick();
                if (c.State == ConnectionState.CLOSED && s.State == ConnectionState.CLOSED)
                    break;
                Thread.Sleep(10);
            }

            Assert.AreEqual(ConnectionState.CLOSED, s.State);
            Assert.AreEqual(ConnectionState.CLOSED, c.State);
        }

    }
}
