using System;

namespace SharpRUDP
{
    public enum RUDPPacketType
    {
        NUL,
        SYN,
        RST,
        DAT,
        ACK
    }

    [Flags]
    public enum RUDPPacketFlags
    {
        NUL = 0,
        ACK,
        RST
    }
}
