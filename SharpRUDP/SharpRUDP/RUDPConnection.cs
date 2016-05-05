using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRUDP
{
    public class RUDPConnection : RUDPSocket
    {
        public bool IsServer { get; set; }
        public ConnectionState State { get; set; }
        public int SendFrequencyMs { get; set; }
        public int RecvFrequencyMs { get; set; }
        public int PacketIdLimit { get; set; }
        public int SequenceLimit { get; set; }
        public int ClientStartSequence { get; set; }
        public int ServerStartSequence { get; set; }
        public int MTU { get; set; }

        public delegate void dlgEventVoid();
        public delegate void dlgEventConnection(IPEndPoint ep);
        public delegate void dlgEventUserData(RUDPPacket p);
        public event dlgEventConnection OnClientConnect;
        public event dlgEventConnection OnClientDisconnect;
        public event dlgEventConnection OnConnected;
        public event dlgEventUserData OnPacketReceived;

        private List<int> _confirmed { get; set; }
        private List<string> _pendingReset { get; set; }
        private Queue<RUDPPacket> _sendQueue { get; set; }
        private Queue<RUDPPacket> _recvQueue { get; set; }
        private Queue<RUDPPacket> _unconfirmed { get; set; }
        private Dictionary<string, IPEndPoint> _clients { get; set; }
        private Dictionary<string, RUDPSequence> _sequences { get; set; }

        private int _maxMTU { get { return (int)(MTU * 0.80); } }
        private bool _isAlive = false;
        private bool _resetFlag = false;
        private object _ackMutex = new object();
        private object _rstMutex = new object();
        private object _sendMutex = new object();
        private object _recvMutex = new object();
        private object _debugMutex = new object();
        private object _clientMutex = new object();
        private object _sequenceMutex = new object();
        private byte[] _packetHeader = { 0xDE, 0xAD, 0xBE, 0xEF };

        private Thread _thSend;
        private Thread _thRecv;

        public RUDPConnection()
        {
            IsServer = false;
            MTU = 500;
            SendFrequencyMs = 10;
            RecvFrequencyMs = 10;
            PacketIdLimit = int.MaxValue / 2;
            SequenceLimit = int.MaxValue / 2;
            ClientStartSequence = 100;
            ServerStartSequence = 200;
            State = ConnectionState.CLOSED;
        }

        private void Debug(object obj, params object[] args)
        {
            lock (_debugMutex)
            {
                RUDPLogger.IsServer = IsServer;
                Console.ForegroundColor = IsServer ? ConsoleColor.Cyan : ConsoleColor.Green;
                RUDPLogger.Info(obj, args);
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

        public virtual void Initialize(bool startThreads = true)
        {
            _isAlive = true;
            _resetFlag = false;
            _confirmed = new List<int>();
            _pendingReset = new List<string>();
            _sendQueue = new Queue<RUDPPacket>();
            _recvQueue = new Queue<RUDPPacket>();
            _unconfirmed = new Queue<RUDPPacket>();
            _sequences = new Dictionary<string, RUDPSequence>();
            _clients = new Dictionary<string, IPEndPoint>();
            if (startThreads)
                StartThreads();
        }

        public void StartThreads()
        {
            _thSend = new Thread(() =>
            {
                while (_isAlive && !_resetFlag)
                {
                    ProcessSendQueue();
                    Thread.Sleep(SendFrequencyMs);
                }
            });
            _thRecv = new Thread(() =>
            {
                while (_isAlive && !_resetFlag)
                {
                    ProcessRecvQueue();
                    Thread.Sleep(RecvFrequencyMs);
                    continue;
                    lock (_ackMutex)
                    {
                        List<RUDPPacket> unconfirmed = new List<RUDPPacket>();
                        while (_unconfirmed.Count > 0)
                            unconfirmed.Add(_unconfirmed.Dequeue());
                        foreach (RUDPPacket p in unconfirmed)
                            if ((DateTime.Now - p.Sent).Seconds >= 1)
                            {
                                p.Retransmit = true;
                                p.Sent = DateTime.Now;
                                Debug("RETRANSMIT {0}", p);
                                _sendQueue.Enqueue(p);
                            }
                            else
                                _unconfirmed.Enqueue(p);
                    }
                }
                if (_resetFlag)
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
            _isAlive = false;
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
                RUDPPacket p = RUDPPacket.Deserialize(_packetHeader, data);
                p.Src = IsServer ? ep : RemoteEndPoint;
                p.Received = DateTime.Now;
                if (p.Type == RUDPPacketType.RST && !IsServer)
                    _resetFlag = true;
                else
                    lock (_recvMutex)
                        _recvQueue.Enqueue(p);
            }
            else
                Console.WriteLine("[{0}] RAW RECV: [{1}]", GetType().ToString(), Encoding.ASCII.GetString(data, 0, length));
        }

        public void Send(string data)
        {
            Send(RemoteEndPoint, RUDPPacketType.DAT, RUDPPacketFlags.NUL, Encoding.ASCII.GetBytes(data));
        }

        public void Send(IPEndPoint destination, RUDPPacketType type = RUDPPacketType.DAT, RUDPPacketFlags flags = RUDPPacketFlags.NUL, byte[] data = null)
        {
            int pid = 0;
            DateTime dtNow = DateTime.Now;
            InitSequence(destination);
            RUDPSequence sq = _sequences[destination.ToString()];
            lock (_sequenceMutex)
                pid = sq.PacketId;
            if ((data != null && data.Length < _maxMTU) || data == null)
            {
                lock (_sendMutex)
                    _sendQueue.Enqueue(new RUDPPacket()
                    {
                        Sent = dtNow,
                        Dst = destination,
                        Id = pid,
                        Type = type,
                        Flags = flags,
                        Data = data
                    });
                sq.PacketId++;
            }
            else if (data != null && data.Length >= _maxMTU)
            {
                int i = 0;
                List<RUDPPacket> PacketsToSend = new List<RUDPPacket>();
                while (i < data.Length)
                {
                    int min = i;
                    int max = _maxMTU;
                    if ((min + max) > data.Length)
                        max = data.Length - min;
                    byte[] buf = data.Skip(i).Take(max).ToArray();
                    PacketsToSend.Add(new RUDPPacket()
                    {
                        Sent = dtNow,
                        Dst = destination,
                        Id = pid,
                        Type = type,
                        Flags = flags,
                        Data = buf
                    });
                    i += _maxMTU;
                }
                lock (_sendMutex)
                    foreach (RUDPPacket p in PacketsToSend)
                    {
                        p.Qty = PacketsToSend.Count;
                        _sendQueue.Enqueue(p);
                    }

                lock(_sequenceMutex)
                    sq.PacketId++;
            }
            else
                throw new Exception("This should not happen");
            lock (_sequenceMutex)
                if (sq.PacketId > PacketIdLimit)
                    sq.PacketId = 0;
        }

        private void ManualSend(RUDPPacket p)
        {
            lock (_sendMutex)
                _sendQueue.Enqueue(p);
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
            bool rv = false;
            lock (_sequenceMutex)
            {
                if (!_sequences.ContainsKey(ep.ToString()))
                {
                    _sequences[ep.ToString()] = new RUDPSequence()
                    {
                        EndPoint = ep,
                        Local = IsServer ? ServerStartSequence : ClientStartSequence,
                        Remote = IsServer ? ClientStartSequence : ServerStartSequence,
                        PacketId = 0,
                        SkippedPackets = new List<int>()
                    };
                    while (!_sequences.ContainsKey(ep.ToString()))
                        Thread.Sleep(10);
                    Debug("NEW SEQUENCE: {0}", _sequences[ep.ToString()]);
                    rv = true;
                }
            }
            return rv;
        }

        private void ResetConnection()
        {
            Debug("Connection reset!");

            List<RUDPPacket> resettedPackets = new List<RUDPPacket>();
            lock (_ackMutex)
                while (_unconfirmed.Count > 0)
                    resettedPackets.Add(_unconfirmed.Dequeue());

            Disconnect();
            Initialize(false);

            Thread.Sleep(1000);

            if (!IsServer)
                ManualSend(new RUDPPacket() { Dst = RemoteEndPoint, Type = RUDPPacketType.SYN });

            foreach (RUDPPacket p in resettedPackets)
                ManualSend(p);

            StartThreads();
        }

        public void SendKeepAlive()
        {
            if (IsServer)
                lock (_clientMutex)
                    Parallel.ForEach(_clients, (c) => { Send(c.Value, RUDPPacketType.NUL); });
        }

        public void ProcessSendQueue()
        {
            DateTime dtNow = DateTime.Now;

            List<RUDPPacket> PacketsToSend = new List<RUDPPacket>();
            lock (_sendMutex)
                while (_sendQueue.Count != 0)
                    PacketsToSend.Add(_sendQueue.Dequeue());

            if (RUDPLogger.LogLevel == RUDPLogger.RUDPLoggerLevel.Trace)
                if (PacketsToSend.Count > 0)
                    foreach (RUDPPacket p in PacketsToSend)
                        Debug("SEND QUEUE: {0}", p);

            List<RUDPPacket> sentPackets = new List<RUDPPacket>();
            foreach (RUDPPacket p in PacketsToSend)
            {
                if (!p.Retransmit)
                {
                    InitSequence(p.Dst);
                    RUDPSequence sq = _sequences[p.Dst.ToString()];
                    p.Seq = sq.Local;
                    sq.Local++;

                    lock (_ackMutex)
                    {
                        p.ACK = _confirmed.ToArray();
                        _confirmed.Clear();
                    }

                    if (IsServer)
                        lock (_rstMutex)
                            if (_pendingReset.Contains(p.Dst.ToString()))
                            {
                                Debug("Overflow reached, resetting {0}", p.Dst);
                                _pendingReset.Remove(p.Dst.ToString());
                                p.Flags = RUDPPacketFlags.RST;
                                lock (_sequenceMutex)
                                    _sequences.Remove(p.Dst.ToString());
                            }
                }

                lock (_ackMutex)
                    _unconfirmed.Enqueue(p);

                if (!p.Retransmit)
                {
                    Debug("SEND -> {0}: {1}", p.Dst, p);

                    if (p.Type == RUDPPacketType.RST)
                        lock (_sequenceMutex)
                            _sequences.Remove(p.Dst.ToString());
                }

                Send(p.Dst, p.ToByteArray(_packetHeader));
            }
        }

        public void ProcessRecvQueue()
        {
            List<RUDPPacket> PacketsToRecv = new List<RUDPPacket>();
            lock (_recvMutex)
                while (_recvQueue.Count != 0)
                    PacketsToRecv.Add(_recvQueue.Dequeue());

            foreach (var kvpEndpoint in (from x in PacketsToRecv group x by x.Src into y select new { Source = y.Key, Packets = y.OrderBy(z => z.Seq) }))
            {
                bool processEndpoint = true;
                bool isNewSequence = InitSequence(kvpEndpoint.Source);
                RUDPSequence sq = _sequences[kvpEndpoint.Source.ToString()];

                if (!isNewSequence)
                    lock (_rstMutex)
                        if (_pendingReset.Contains(kvpEndpoint.Source.ToString()))
                        {
                            Debug("Pending reset {0}, not new sequence.", kvpEndpoint.Source);
                            continue;
                        }

                foreach (RUDPPacket p in kvpEndpoint.Packets)
                {
                    if (sq.SkippedPackets.Contains(p.Seq))
                    {
                        Debug("Skip {0}", p);
                        if(p.Qty > 0 && !p.MutiProcessed)
                            lock(_recvMutex)
                                _recvQueue.Enqueue(p);
                        continue;
                    }

                    // if (IsServer) Debug("{0} vs {1}", p.Seq, sq.Remote);
                    if (p.Seq != sq.Remote)
                    {
                        if (isNewSequence)
                            RequestConnectionReset(sq.EndPoint);
                        else
                            lock (_recvMutex)
                                foreach (RUDPPacket pkt in kvpEndpoint.Packets)
                                    _recvQueue.Enqueue(p);
                        processEndpoint = false;
                        break;
                    }

                    // If IsNewSequence and first packet is not a connection
                    // ignore the packets and do not process the chain :)
                    if (isNewSequence && IsServer && p.Type != RUDPPacketType.SYN)
                    {
                        Debug("RECV <- (IGNORE) {0}: {1}", kvpEndpoint.Source, p);
                        processEndpoint = false;
                        break;
                    }

                    sq.Remote++;
                    Debug("RECV <- {0}: {1}", kvpEndpoint.Source, p);

                    if (p.Type == RUDPPacketType.SYN && IsServer)
                        lock (_clientMutex)
                            if (!_clients.ContainsKey(kvpEndpoint.Source.ToString()))
                            {
                                lock (_recvMutex)
                                    _recvQueue = new Queue<RUDPPacket>(_recvQueue.Where(x => x.Src != kvpEndpoint.Source));
                                Debug("+Client {0}", kvpEndpoint.Source.ToString());
                                _clients.Add(kvpEndpoint.Source.ToString(), kvpEndpoint.Source);
                                Send(kvpEndpoint.Source, RUDPPacketType.SYN, RUDPPacketFlags.ACK);
                                OnClientConnect?.Invoke(kvpEndpoint.Source);
                            }

                    if (p.Qty > 0 && p.Type == RUDPPacketType.DAT)
                    {
                        // TODO: Handle duplicates
                        sq.IsProcessingMultipacket = true;

                        List<RUDPPacket> Multipackets = kvpEndpoint.Packets.Where(x => x.Id == p.Id).ToList();
                        if (Multipackets.Count != p.Qty)
                        {
                            Debug("MULTIPACKET UNCOMPLETE {0} of {1}", Multipackets.Count, p.Qty);
                            sq.SkippedPackets.Add(p.Seq);
                            ConfirmPacket(p);
                            lock (_recvMutex)
                                _recvQueue.Enqueue(p);
                        }
                        else
                        {
                            Debug("MULTIPACKET ARRIVED");
                            sq.IsProcessingMultipacket = false;
                            sq.SkippedPackets.AddRange(Multipackets.Select(x => x.Seq).ToArray());
                            byte[] buf;
                            using (MemoryStream ms = new MemoryStream())
                            {
                                using (BinaryWriter bw = new BinaryWriter(ms))
                                    foreach (RUDPPacket mp in Multipackets)
                                    {
                                        bw.Write(mp.Data);
                                        ConfirmPacket(mp);
                                        mp.MutiProcessed = true;
                                        sq.Remote++;
                                    }
                                buf = ms.ToArray();
                            }
                            Debug("MULTIPACKET DATA: {0}", Encoding.ASCII.GetString(buf));
                            OnPacketReceived?.Invoke(new RUDPPacket()
                            {
                                ACK = p.ACK,
                                Confirmed = p.Confirmed,
                                Data = buf,
                                Dst = p.Dst,
                                Flags = p.Flags,
                                Id = p.Id,
                                Qty = p.Qty,
                                Received = p.Received,
                                Seq = p.Seq,
                                Src = p.Src,
                                Type = p.Type
                            });
                        }

                    }
                    else
                    {
                        ConfirmPacket(p);
                        bool isProcessing = false;
                        isProcessing = sq.IsProcessingMultipacket;
                        if (isProcessing)
                            lock (_recvMutex)
                                _recvQueue.Enqueue(p);
                        else
                            OnPacketReceived?.Invoke(p);
                    }

                    if (!IsServer && p.Type == RUDPPacketType.SYN && p.Flags == RUDPPacketFlags.ACK)
                    {
                        State = ConnectionState.OPEN;
                        OnConnected?.Invoke(p.Src);
                    }

                    if (!IsServer && p.Flags == RUDPPacketFlags.RST)
                    {
                        processEndpoint = false;
                        break;
                    }

                    isNewSequence = false;
                }

                if (processEndpoint)
                {
                    if (kvpEndpoint.Packets.Where(x => x.Type != RUDPPacketType.ACK && x.Type != RUDPPacketType.NUL && !x.Confirmed).Count() > 0)
                        Send(kvpEndpoint.Source, RUDPPacketType.ACK);
                    if (IsServer && kvpEndpoint.Packets.Last().Seq > SequenceLimit)
                        lock (_rstMutex)
                            _pendingReset.Add(kvpEndpoint.Source.ToString());
                }
            }
        }

        private void ConfirmPacket(RUDPPacket p)
        {
            lock (_ackMutex)
                if (!p.Confirmed)
                {
                    p.Confirmed = true;
                    _confirmed.Add(p.Seq);
                    _unconfirmed = new Queue<RUDPPacket>(_unconfirmed.Where(x => !p.ACK.Contains(x.Seq)));
                }
        }

        private void RequestConnectionReset(IPEndPoint endPoint)
        {
            lock (_clientMutex)
            {
                Debug("-Client {0}", endPoint.ToString());
                _clients.Remove(endPoint.ToString());
                Send(endPoint, RUDPPacketType.RST);
                OnClientDisconnect?.Invoke(endPoint);
            }
        }
    }
}
