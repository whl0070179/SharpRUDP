using System;
using System.Net;
using System.Web.Script.Serialization;

namespace SharpRUDP
{
    public class RUDPPacket
    {
        [ScriptIgnore]
        public IPEndPoint Src { get; set; }
        [ScriptIgnore]
        public IPEndPoint Dst { get; set; }
        [ScriptIgnore]
        public DateTime Received { get; set; }

        public int Id { get; set; }
        public int Qty { get; set; }
        public int Seq { get; set; }
        public RUDPPacketType Type { get; set; }
        public RUDPPacketFlags Flags { get; set; }
        public byte[] Data { get; set; }
        public int[] ACK { get; set; }

        public override string ToString()
        {
            return new JavaScriptSerializer().Serialize(this);
        }
    }
}
