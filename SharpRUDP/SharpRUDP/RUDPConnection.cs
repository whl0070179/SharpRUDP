using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SharpRUDP
{
    public class RUDPConnection : RUDPSocket
    {
        public bool IsServer { get; set; }
        public ConnectionState State { get; set; }
        public Queue<RUDPPacket> SendQueue { get; set; }
        public Queue<RUDPPacket> RecvQueue { get; set; }
        public Dictionary<string, RUDPSequence> Sequences { get; set; }
        public Queue<RUDPPacket> Unconfirmed { get; set; }
        public List<int> Confirmed { get; set; }
        public Dictionary<string, IPEndPoint> ConnectedClients { get; set; }
        public List<string> PendingReset { get; set; }

        private bool IsAlive = false;
        private bool ResetFlag = false;
        private object _ackMutex = new object();
        private object _rstMutex = new object();
        private object _sendMutex = new object();
        private object _recvMutex = new object();
        private object _debugMutex = new object();
        private object _clientMutex = new object();
        private object _sequenceMutex = new object();
        private byte[] _packetHeader = { 0xDE, 0xAD, 0xBE, 0xEF };
        private JavaScriptSerializer _js = new JavaScriptSerializer();

        private Thread _thSend;
        private Thread _thRecv;

        public void Debug(object obj, params object[] args)
        {
            lock (_debugMutex)
            {
                Console.ForegroundColor = IsServer ? ConsoleColor.Cyan : ConsoleColor.Green;
                if (obj.GetType() == typeof(string))
                    Console.WriteLine(string.Format("{0} {1}", IsServer ? "[S]" : "[C]", string.Format((string)obj, args)));
                else
                    Console.WriteLine(string.Format("{0} {1}", IsServer ? "[S]" : "[C]", obj.ToString()));
                Console.ResetColor();
            }
        }

        public void Connect(string address, int port)
        {
            IsServer = false;
            State = ConnectionState.OPENING;
            Client(address, port);
            Initialize();
            Send(RemoteEndPoint, RUDPPacketType.SYN);
        }

        public void Listen(string address, int port)
        {
            IsServer = true;
            Server(address, port);
            State = ConnectionState.LISTEN;
            Initialize();
        }

        public virtual void Initialize()
        {
            IsAlive = true;
            ResetFlag = false;
            Confirmed = new List<int>();
            PendingReset = new List<string>();
            SendQueue = new Queue<RUDPPacket>();
            RecvQueue = new Queue<RUDPPacket>();
            Unconfirmed = new Queue<RUDPPacket>();
            Sequences = new Dictionary<string, RUDPSequence>();
            ConnectedClients = new Dictionary<string, IPEndPoint>();

            _thSend = new Thread(() =>
            {
                while (IsAlive && !ResetFlag)
                {
                    ProcessSendQueue();
                    Thread.Sleep(10);
                }
            });
            _thRecv = new Thread(() =>
            {
                while (IsAlive && !ResetFlag)
                {
                    ProcessRecvQueue();
                    Thread.Sleep(10);
                }
                if (ResetFlag)
                {
                    new Thread(() =>
                    {
                        Debug("RESET");
                        Thread.Sleep(1000);
                        ResetConnection();
                    }).Start();
                }
            });
            _thSend.Start();
            _thRecv.Start();
        }

        public void Disconnect()
        {
            IsAlive = false;
            _thSend.Abort();
            _thRecv.Abort();
            if (IsServer)
                _socket.Close();
            while (_thSend.IsAlive)
                Thread.Sleep(10);
            while (_thRecv.IsAlive)
                Thread.Sleep(10);
        }

        public override void PacketReceive(IPEndPoint ep, byte[] data, int length)
        {
            base.PacketReceive(ep, data, length);
            if (length > _packetHeader.Length && data.Take(_packetHeader.Length).SequenceEqual(_packetHeader))
            {
                RUDPPacket p = _js.Deserialize<RUDPPacket>(Encoding.ASCII.GetString(data.Skip(_packetHeader.Length).ToArray()));
                p.Src = IsServer ? ep : RemoteEndPoint;
                p.Received = DateTime.Now;
                if (p.Type == RUDPPacketType.RST && !IsServer)
                    ResetConnection();
                else
                    lock (_recvMutex)
                        RecvQueue.Enqueue(p);
            }
            else
                Console.WriteLine("[{0}] RAW RECV: [{1}]", GetType().ToString(), Encoding.ASCII.GetString(data, 0, length));
        }

        public void Send(string data)
        {
            Send(RemoteEndPoint, RUDPPacketType.DAT, data);
        }

        public void Send(IPEndPoint destination, RUDPPacketType type = RUDPPacketType.DAT, string data = null)
        {
            InitSequence(destination);
            RUDPSequence sq = Sequences[destination.ToString()];
            if ((!string.IsNullOrEmpty(data) && data.Length < 4) || data == null)
            {
                lock (_sequenceMutex)
                {
                    lock (_sendMutex)
                        SendQueue.Enqueue(new RUDPPacket()
                        {
                            Dst = destination,
                            Id = sq.PacketId,
                            Type = type,
                            Data = string.IsNullOrEmpty(data) ? null : Encoding.ASCII.GetBytes(data)
                        });
                    sq.PacketId++;
                }
            }
            else if (!string.IsNullOrEmpty(data) && data.Length > 4)
            {
                lock (_sendMutex)
                {
                    int i = 0;
                    string str = data;
                    lock (_sequenceMutex)
                    {
                        List<RUDPPacket> PacketsToSend = new List<RUDPPacket>();
                        while (str.Length > 0)
                        {
                            str = string.Join("", data.Skip(i).Take(4));
                            if (str.Length == 0)
                                break;
                            i += 4;
                            PacketsToSend.Add(new RUDPPacket()
                            {
                                Dst = destination,
                                Id = sq.PacketId,
                                Type = type,
                                Data = string.IsNullOrEmpty(str) ? null : Encoding.ASCII.GetBytes(str)
                            });
                        }
                        lock (_sendMutex)
                            foreach (RUDPPacket p in PacketsToSend)
                            {
                                p.Qty = PacketsToSend.Count;
                                SendQueue.Enqueue(p);
                            }
                        sq.PacketId++;
                    }
                }
            }
            else
                throw new Exception("This should not happen");
            lock (_sequenceMutex)
                if (sq.PacketId > 100)
                    sq.PacketId = 0;
        }

        private void ManualSend(RUDPPacket p)
        {
            lock (_sendMutex)
                SendQueue.Enqueue(p);
        }

        // ###############################################################################################################################
        // ###############################################################################################################################
        // ###############################################################################################################################

        private bool InitSequence(RUDPPacket p)
        {
            return InitSequence(p.Src == null ? p.Dst : p.Src);
        }

        private bool InitSequence(IPEndPoint ep)
        {
            lock (_sequenceMutex)
            {
                if (!Sequences.ContainsKey(ep.ToString()))
                {
                    Sequences[ep.ToString()] = new RUDPSequence() { EndPoint = ep, Local = IsServer ? 200 : 100, Remote = IsServer ? 100 : 200, PacketId = 0 };
                    Debug("NEW SEQUENCE: {0}", Sequences[ep.ToString()]);
                    return true;
                }
                return false;
            }
        }

        private void ResetConnection()
        {
            Debug("Connection reset!");

            List<RUDPPacket> resettedPackets = new List<RUDPPacket>();
            lock (_ackMutex)
                while (Unconfirmed.Count > 0)
                    resettedPackets.Add(Unconfirmed.Dequeue());

            Disconnect();
            Initialize();

            Thread.Sleep(1000);

            if (!IsServer)
                ManualSend(new RUDPPacket() { Dst = RemoteEndPoint, Type = RUDPPacketType.SYN });

            foreach (RUDPPacket p in resettedPackets)
            {
                if (Sequences.ContainsKey(p.Dst.ToString()))
                    Sequences.Remove(p.Dst.ToString());
                ManualSend(p);
            }
        }

        public void SendKeepAlive()
        {
            if (IsServer)
                lock (_clientMutex)
                    Parallel.ForEach(ConnectedClients, (c) => { Send(c.Value, RUDPPacketType.NUL); });
        }

        public void ProcessSendQueue()
        {
            List<RUDPPacket> PacketsToSend = new List<RUDPPacket>();
            lock (_sendMutex)
                while (SendQueue.Count != 0)
                    PacketsToSend.Add(SendQueue.Dequeue());

            foreach (RUDPPacket p in PacketsToSend)
            {
                lock (_sequenceMutex)
                {
                    if(!Sequences.ContainsKey(p.Dst.ToString()))
                        InitSequence(p.Dst);
                    RUDPSequence sq = Sequences[p.Dst.ToString()];
                    p.Seq = sq.Local;
                    sq.Local++;
                }

                lock (_ackMutex)
                {
                    p.ACK = Confirmed.ToArray();
                    Confirmed.Clear();
                }

                lock (_rstMutex)
                    if (PendingReset.Contains(p.Dst.ToString()))
                    {
                        Debug("Overflow reached, resetting {0}", p.Dst);
                        PendingReset.Remove(p.Dst.ToString());
                        p.Flags = RUDPPacketFlags.RST;
                        lock (_sequenceMutex)
                            Sequences.Remove(p.Dst.ToString());
                    }

                lock (_ackMutex)
                    Unconfirmed.Enqueue(p);

                Debug("SEND -> {0}", p);

                if (p.Type == RUDPPacketType.RST)
                    lock (_sequenceMutex)
                        Sequences.Remove(p.Dst.ToString());

                Send(p.Dst, _packetHeader.Concat(Encoding.ASCII.GetBytes(_js.Serialize(p))).ToArray());
            }
        }

        public void ProcessRecvQueue()
        {
            List<RUDPPacket> PacketsToRecv = new List<RUDPPacket>();
            lock (_recvMutex)
                while (RecvQueue.Count != 0 && PacketsToRecv.Count < 50)
                    PacketsToRecv.Add(RecvQueue.Dequeue());

            var packetsToProcess = from x in PacketsToRecv
                                   group x by x.Src into y
                                   select new
                                   {
                                       Source = y.Key,
                                       Packets = y.OrderBy(z => z.Seq)
                                   };

            foreach (var kvEndpoint in packetsToProcess)
            {
                bool ignoreEndpoint = false;
                bool reenqueuePackets = false;
                List<int> ProcessedPackets = new List<int>();
                List<int> MultiProcessedPackets = new List<int>();
                foreach (RUDPPacket p in kvEndpoint.Packets)
                {
                    if(reenqueuePackets)
                    {
                        lock (_recvMutex)
                            RecvQueue.Enqueue(p);
                        continue;
                    }

                    bool isNewSequence = InitSequence(p);
                    RUDPSequence sq = Sequences[kvEndpoint.Source.ToString()];

                    if (p.Seq != sq.Remote)
                    {
                        //if ((DateTime.Now - p.Received).Seconds > 2 && IsServer)
                        if (isNewSequence)
                        {
                            Debug("{0} != {1}", p.Seq, sq.Remote);
                            RequestConnectionReset(sq.EndPoint);
                        }
                        else
                            lock (_recvMutex)
                                foreach (RUDPPacket pkt in kvEndpoint.Packets)
                                    RecvQueue.Enqueue(p);

                        ignoreEndpoint = true;
                        break;
                    }

                    // If IsNewSequence and first packet is not a connection
                    // ignore the packets and do not process the chain :)
                    // NOTE: Never seems to happen though
                    if (isNewSequence && IsServer && p.Type != RUDPPacketType.SYN)
                    {
                        Debug("RECV <- (IGNORE) {0}", p);
                        ignoreEndpoint = true;
                        break;
                    }

                    sq.Remote++;
                    Debug("RECV <- {0}", p);

                    if (p.Type == RUDPPacketType.SYN && IsServer)
                        lock (_clientMutex)
                            if (!ConnectedClients.ContainsKey(kvEndpoint.Source.ToString()))
                            {
                                lock (_recvMutex)
                                    RecvQueue = new Queue<RUDPPacket>(RecvQueue.Where(x => x.Src != kvEndpoint.Source));
                                Debug("+Client {0}", kvEndpoint.Source.ToString());
                                ConnectedClients.Add(kvEndpoint.Source.ToString(), kvEndpoint.Source);
                            }

                    if (MultiProcessedPackets.Contains(p.Seq))
                    {
                        Debug("SKIP MULTIPACKET PART {0}", p);
                        ConfirmPacket(p);
                        continue;
                    }

                    if (p.Qty > 0 && p.Type == RUDPPacketType.DAT)
                    {
                        List<RUDPPacket> Multipackets = kvEndpoint.Packets.Where(x => x.Id == p.Id).ToList();
                        if (Multipackets.Count == p.Qty)
                        {
                            Debug("MULTIPACKET");
                            using (MemoryStream ms = new MemoryStream())
                            {
                                using (BinaryWriter bw = new BinaryWriter(ms))
                                    foreach (RUDPPacket mp in Multipackets)
                                        bw.Write(mp.Data);
                                Debug("MULTIPACKET DATA: {0}", Encoding.ASCII.GetString(ms.ToArray()));
                                MultiProcessedPackets.AddRange(Multipackets.Select(x => x.Seq).ToArray());
                            }
                        }
                        else
                        {
                            reenqueuePackets = true;
                            Debug("MULTIPACKET UNCOMPLETE");
                        }
                    }

                    if(!reenqueuePackets)
                        ConfirmPacket(p);
                    else
                        lock(_recvMutex)
                            RecvQueue.Enqueue(p);

                    if (!IsServer && p.Flags == RUDPPacketFlags.RST)
                    {
                        ignoreEndpoint = true;
                        ResetFlag = true;
                        break;
                    }
                }

                if (!ignoreEndpoint)
                {
                    if (kvEndpoint.Packets.Where(x => x.Type != RUDPPacketType.ACK && x.Type != RUDPPacketType.NUL).Count() > 0)
                        Send(kvEndpoint.Source, RUDPPacketType.ACK);
                    if (IsServer && kvEndpoint.Packets.Last().Seq > 120)
                        lock (_rstMutex)
                            PendingReset.Add(kvEndpoint.Source.ToString());
                }

                if (ResetFlag)
                    break;
            }
        }

        private void ConfirmPacket(RUDPPacket p)
        {
            lock (_ackMutex)
            {
                Confirmed.Add(p.Seq);
                Unconfirmed = new Queue<RUDPPacket>(Unconfirmed.Where(x => !p.ACK.Contains(x.Seq)));
            }
        }

        private void RequestConnectionReset(IPEndPoint endPoint)
        {
            lock (_clientMutex)
            {
                Debug("-Client {0}", endPoint.ToString());
                ConnectedClients.Remove(endPoint.ToString());
                Send(endPoint, RUDPPacketType.RST);
            }
        }
    }
}
