using CommsLIBPlus.Base;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace CommsLIBPlus
{
    public delegate void DataReadySyncDelegate(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint);
    public delegate void DataReadyAsyncDelegate(string ip, int port, long time, IMemoryOwner<byte> data, string ID, uint ipuint);
    public delegate void ConnectionStateDelegate(string ID, ConnUri uri, bool connected);
    public delegate void DataRateDelegate(string ID, float MbpsRX, float MbpsTX);

    public interface ICommunicator : IDisposable
    {
        event DataRateDelegate DataRateEvent;
        event ConnectionStateDelegate ConnectionStateEvent;
        event DataReadySyncDelegate DataReadySyncEvent;
        event DataReadyAsyncDelegate DataReadyAsyncEvent;


        string ID { get; set; }
        ConnUri CommsUri {get;}

        void Init(ConnUri uri, bool persistent, string ID, int inactivityMS);
        void Start();
        Task Stop();
        ValueTask<int> SendAsync(ReadOnlyMemory<byte> bytes);
    }
}
