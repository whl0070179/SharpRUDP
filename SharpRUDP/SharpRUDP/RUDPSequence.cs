using System.Net;

namespace SharpRUDP
{
    public class RUDPSequence
    {
        public IPEndPoint EndPoint { get; set; }
        public int Local { get; set; }
        public int? Remote { get; set; }
        public int PacketId { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] Local: {1} | Remote: {2}", EndPoint, Local, Remote);
        }
    }
}
