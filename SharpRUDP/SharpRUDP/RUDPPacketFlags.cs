using System;

namespace SharpRUDP
{
    [Flags]
    public enum RUDPPacketFlags
    {
        NUL = 0,
        ACK,
        RST
    }
}