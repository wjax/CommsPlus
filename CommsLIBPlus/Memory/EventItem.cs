using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Memory
{
    public struct EventItem
    {
        public string IP;
        public int Port;
        public long Time;
        public IMemoryOwner<byte> Data;
        public string ID;
        public uint IpUInt;
    }
}
