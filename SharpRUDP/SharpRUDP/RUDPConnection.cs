using System;
using System.Collections.Concurrent;
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
                Console.ForegroundColor = IsServer ? ConsoleColor.Cyan : ConsoleColor.Green;
                RUDPLogger.Info(IsServer ? "[S]" : "[C]", obj, args);
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
            State = ConnectionState.CLOSING;
            _isAlive = false;
            _thSend.Abort();
            _thRecv.Abort();
            if (IsServer)
                _socket.Close();
            while (_thSend.IsAlive)
                Thread.Sleep(10);
            while (_thRecv.IsAlive)
                Thread.Sleep(10);
            State = ConnectionState.CLOSED;
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
            DateTime dtNow = DateTime.Now;
            InitSequence(destination);
            RUDPSequence sq = _sequences[destination.ToString()];
            if ((data != null && data.Length < _maxMTU) || data == null)
            {
                lock (_sendMutex)
                    _sendQueue.Enqueue(new RUDPPacket()
                    {
                        Sent = dtNow,
                        Dst = destination,
                        Id = sq.PacketId,
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
                        Id = sq.PacketId,
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

                sq.PacketId++;
            }
            else
                throw new Exception("This should not happen");
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
                        IsWaitingForMultiPacket = false,
                        MultiPackets = new Dictionary<int, List<RUDPPacket>>()
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
            {
                resettedPackets.AddRange(_unconfirmed);
                _unconfirmed = new Queue<RUDPPacket>();
            }

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
            else
                Send(RemoteEndPoint, RUDPPacketType.NUL);
        }

        // ###############################################################################################################################
        // ###############################################################################################################################
        // ###############################################################################################################################

        public void ProcessSendQueue()
        {
            DateTime dtNow = DateTime.Now;

            List<RUDPPacket> PacketsToSend = new List<RUDPPacket>();
            lock(_sendMutex)
                while (_sendQueue.Count > 0)
                    PacketsToSend.Add(_sendQueue.Dequeue());

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
                        _confirmed = new List<int>();
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
                else
                    Debug("RETRANSMIT -> {0}: {1}", p.Dst, p);
            }

            Parallel.ForEach(PacketsToSend, (RUDPPacket p) =>
            {
                Send(p.Dst, p.ToByteArray(_packetHeader));
            });
        }

        // ###############################################################################################################################
        // ###############################################################################################################################

        public void ProcessRecvQueue()
        {
            List<RUDPPacket> PacketsToRecv = new List<RUDPPacket>();
            lock(_recvMutex)
                while (_recvQueue.Count > 0)
                {
                    RUDPPacket p = _recvQueue.Dequeue();
                    if(PacketsToRecv.Where(x => x.Src == p.Src && x.Seq == p.Seq).Count() == 0)
                        PacketsToRecv.Add(p);
                }

            foreach (var kvpEndpoint in (from x in PacketsToRecv group x by x.Src into y select new { Source = y.Key, Packets = y.OrderBy(z => z.Seq) }))
            {
                int lowestRetransmit = -1;
                bool retransmit = false;
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
                    if (p.Skip)
                        continue;

                    // If packet sequence does not match, reenqueue next packets
                    // and wait for the next batch.
                    if (p.Seq != sq.Remote)
                    {
                        if (IsServer)
                            Debug("RECV <- (SQ. MISMATCH) {0} != {1} | {2}: {3}", p.Seq, sq.Remote, kvpEndpoint.Source, p);
                        if (isNewSequence)
                            RequestConnectionReset(sq.EndPoint);
                        else
                            lock (_recvMutex)
                                if(p.Seq >= sq.Remote)
                                    _recvQueue.Enqueue(p);
                        if (lowestRetransmit < 0)
                        {
                            lowestRetransmit = sq.Remote.Value;
                            retransmit = true;
                        }
                        continue;
                    }

                    // If IsNewSequence and first packet is not a connection
                    // ignore the packets and do not process the chain :)
                    if (isNewSequence && IsServer && p.Type != RUDPPacketType.SYN)
                    {
                        Debug("RECV <- (IGNORE) {0}: {1}", kvpEndpoint.Source, p);
                        processEndpoint = false;
                        break;
                    }

                    Debug("RECV <- {0}: {1}", kvpEndpoint.Source, p);

                    if (p.Qty > 0 && p.Type == RUDPPacketType.DAT)
                    {
                        List<RUDPPacket> Multipackets = kvpEndpoint.Packets.Where(x => x.Id == p.Id).ToList();
                        if (Multipackets.Count != p.Qty)
                        {
                            Debug("MULTIPACKET ID {0} UNCOMPLETE {1} of {2}", p.Id, Multipackets.Count, p.Qty);
                            foreach (RUDPPacket mp in Multipackets)
                            {
                                sq.Remote++;
                                ConfirmPacket(mp);
                                lock (_recvMutex)
                                    _recvQueue.Enqueue(mp);
                            }
                            lowestRetransmit = sq.Remote.Value;
                            retransmit = false;
                            sq.IsWaitingForMultiPacket = true;
                            break;
                        }
                        else
                        {
                            Debug("MULTIPACKET ID {0} ARRIVED", p.Id);
                            byte[] buf;
                            using (MemoryStream ms = new MemoryStream())
                            {
                                using (BinaryWriter bw = new BinaryWriter(ms))
                                    foreach (RUDPPacket mp in Multipackets)
                                    {
                                        bw.Write(mp.Data);
                                        ConfirmPacket(mp);
                                        Debug("RECV <- {0}: {1}", kvpEndpoint.Source, mp);
                                        mp.Skip = true;
                                        sq.Remote++;
                                    }
                                buf = ms.ToArray();
                            }
                            Debug("MULTIPACKET ID {0} DATA: {1}", p.Id, Encoding.ASCII.GetString(buf));
                            Debug("NEXT SEQUENCE MUST BE: {0}", sq.Remote);
                            lowestRetransmit = sq.Remote.Value;
                            retransmit = false;
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
                        sq.Remote++;

                        if (IsServer && p.Type == RUDPPacketType.SYN)
                            lock (_clientMutex)
                                if (!_clients.ContainsKey(kvpEndpoint.Source.ToString()))
                                {
                                    _recvQueue = new Queue<RUDPPacket>(_recvQueue.Where(x => x.Src != kvpEndpoint.Source));
                                    Debug("+Client {0}", kvpEndpoint.Source.ToString());
                                    _clients.Add(kvpEndpoint.Source.ToString(), kvpEndpoint.Source);
                                    Send(kvpEndpoint.Source, RUDPPacketType.SYN, RUDPPacketFlags.ACK);
                                    OnClientConnect?.Invoke(kvpEndpoint.Source);
                                }

                        ConfirmPacket(p);

                        if (sq.IsWaitingForMultiPacket)
                        {
                            lock (_recvMutex)
                                foreach (RUDPPacket pkt in kvpEndpoint.Packets)
                                    _recvQueue.Enqueue(pkt);
                            processEndpoint = false;
                            break;
                        }
                        else
                            if (p.Type == RUDPPacketType.DAT)
                                OnPacketReceived?.Invoke(p);

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

                        if (p.Type == RUDPPacketType.RET)
                        {
                            int startFrom = int.Parse(Encoding.ASCII.GetString(p.Data));
                            Debug("RETRANSMISSION REQUEST STARTING FROM {0}", startFrom);
                            lock (_ackMutex)
                            {
                                lock (_sendMutex)
                                    foreach (RUDPPacket rp in _unconfirmed.Where(x => x.Seq >= startFrom))
                                    {
                                        Debug("ENQUEUE FOR RETRANSMISSION: {0}", rp);
                                        rp.Retransmit = true;
                                        _sendQueue.Enqueue(rp);
                                    }
                                _unconfirmed = new Queue<RUDPPacket>(_unconfirmed.Where(x => x.Seq >= startFrom));
                            }
                        }
                    }

                    isNewSequence = false;
                }

                if (retransmit)
                    RequestPacketRetransmission(kvpEndpoint.Source, lowestRetransmit);

                if (processEndpoint)
                {
                    if (kvpEndpoint.Packets.Where(x => x.Type != RUDPPacketType.ACK && x.Type != RUDPPacketType.NUL && !x.Skip).Count() > 0)
                        Send(kvpEndpoint.Source, RUDPPacketType.ACK);
                    if (IsServer && kvpEndpoint.Packets.Last().Seq > SequenceLimit)
                        lock (_rstMutex)
                            _pendingReset.Add(kvpEndpoint.Source.ToString());
                }
            }
        }

        private void RequestPacketRetransmission(IPEndPoint ep, int? remoteSequence)
        {
            Debug("REQUEST RETRANSMISSION: {0}, {1}", ep, remoteSequence);
            Send(ep, RUDPPacketType.RET, RUDPPacketFlags.NUL, Encoding.ASCII.GetBytes(remoteSequence.ToString()));
        }

        private void ConfirmPacket(RUDPPacket p)
        {
            lock (_ackMutex)
            {
                if (!p.Confirmed)
                {
                    p.Confirmed = true;
                    _confirmed.Add(p.Seq);
                    _unconfirmed = new Queue<RUDPPacket>(_unconfirmed.Where(x => !p.ACK.Contains(x.Seq)));
                }
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
