using CommsLIBPlus;
using CommsLIBPlus.Base;
using CommsLIBPlus.Communications;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.Memory;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CommsLIBPlus.Communications
{
    public abstract class CommunicatorBase<T> : ICommunicator
    {
        #region logger
        private NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        public event DataReadySyncDelegate DataReadySyncEvent;
        public event DataReadyAsyncDelegate DataReadyAsyncEvent;
        public event ConnectionStateDelegate ConnectionStateEvent;
        public event DataRateDelegate DataRateEvent;

        private Channel<EventItem> channelAsyncData;
        private Task eventAsyncTask;
        private CancellationTokenSource tokenSource;
        private CancellationToken token;

        public enum STATE
        {
            RUNNING,
            STOP
        }
        public STATE State;

        public ConnUri CommsUri { get; protected set; }
        public string ID { get; set; }

        public abstract void Init(ConnUri uri, bool persistent, string ID, int inactivityMS);
        public abstract void Start();
        public abstract Task Stop();
        public abstract ValueTask<int> SendAsync(ReadOnlyMemory<byte> memory);
        public abstract ValueTask<int> SendAsync(T message);
        public abstract FrameWrapperBase<T> FrameWrapper { get; }

        public CommunicatorBase()
        {
            channelAsyncData = Channel.CreateUnbounded<EventItem>(new UnboundedChannelOptions {SingleReader = true, SingleWriter = true });

            tokenSource = new CancellationTokenSource();
            eventAsyncTask = Task.Run(EventAsyncWorker, tokenSource.Token);
        }

        private async ValueTask EventAsyncWorker()
        {
            while (!token.IsCancellationRequested && await channelAsyncData.Reader.WaitToReadAsync(token))
            {
                if (channelAsyncData.Reader.TryRead(out EventItem item))
                    DataReadyAsyncEvent?.Invoke(item.IP, item.Port, item.Time, item.Data, item.ID, item.IpUInt);
            }
        }

        public virtual void FireDataEvent(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint)
        {
            try
            {
                DataReadySyncEvent?.Invoke(ip, port, time, data, ID, ipuint);//Interlocked.CompareExchange(ref DataReadyEvent, null, null)?.Invoke(ip, port, time, data, ID, ipChunks);

                if (DataReadyAsyncEvent != null)
                    channelAsyncData.Writer.TryWrite(new EventItem
                    {
                        IP = ip,
                        Port = port,
                        Time = time,
                        Data = data.Lease(),
                        ID = ID,
                        IpUInt = ipuint
                    });

            } catch (Exception e)
            {
                logger.Warn(e, "Client exception while processing DataEvent");
            }
        }

        public virtual void FireConnectionEvent(string ID, ConnUri uri, bool connected)
            => ConnectionStateEvent?.Invoke(ID, uri, connected);

        public virtual void FireDataRateEvent(string ID, float dataRateMbpsRX, float dataRateMbpsTX)
            => DataRateEvent?.Invoke(ID, dataRateMbpsRX, dataRateMbpsTX);
     

        public void UnsubscribeEventHandlers()
        {
            if (DataReadySyncEvent != null)
                foreach (var d in DataReadySyncEvent.GetInvocationList())
                    DataReadySyncEvent -= (d as DataReadySyncDelegate);
            DataReadySyncEvent = null;

            if (DataReadyAsyncEvent != null)
                foreach (var d in DataReadyAsyncEvent.GetInvocationList())
                    DataReadyAsyncEvent -= (d as DataReadyAsyncDelegate);
            DataReadyAsyncEvent = null;

            if (ConnectionStateEvent != null)
                foreach (var d in ConnectionStateEvent.GetInvocationList())
                    ConnectionStateEvent -= (d as ConnectionStateDelegate);
            ConnectionStateEvent = null;

            if (DataRateEvent != null)
                foreach (var d in DataRateEvent.GetInvocationList())
                    DataRateEvent -= (d as DataRateDelegate);
            DataRateEvent = null;
        }

        #region IDisposable Support
        protected bool disposedValue = false; 

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    tokenSource.Cancel();
                    eventAsyncTask.Wait();

                    channelAsyncData.Writer.Complete();
                    tokenSource.Dispose();

                    UnsubscribeEventHandlers();
                }

                logger = null;

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
