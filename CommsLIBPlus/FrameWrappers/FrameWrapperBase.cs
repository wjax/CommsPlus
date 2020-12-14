using CommsLIBPlus.Base;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Threading;
using System.Buffers;
using System.IO.Pipelines;

namespace CommsLIBPlus.Communications.FrameWrappers
{
    public abstract class FrameWrapperBase<T> : IDisposable
    {
        private const int MAX_CHANNEL_BOUND = 100;

        // Delegate and event
        public delegate void FrameAvailableDelegate(string ID, T payload);
        public event FrameAvailableDelegate FrameAvailableEvent;

        private bool useThreadPool4Event;
        public string ID { get; set; }

        private Channel<T> fireChannel;
        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;
        private Task fireTask;

        public FrameWrapperBase(bool _useThreadPool4Event)
        {
            if (useThreadPool4Event = _useThreadPool4Event)
                fireTask = Task.Run(FireQueuedEventLoop);     
        }

        public abstract void AddBytes(ReadOnlyMemory<byte> bytes);

        public abstract void Start();

        public abstract Task Stop();

        public abstract Task<T> WaitForResponse(uint ResponseID, TaskCompletionSource<T> tcs);

        public async ValueTask FireEvent(T toFire)
        {
            if (useThreadPool4Event)
            {
                if (!fireChannel.Writer.TryWrite(toFire))
                    await fireChannel.Writer.WriteAsync(toFire, cancellationToken);
            }
            else
            {
                try
                {
                    DoFireEvent(ID, toFire);
                }
                catch (Exception e)
                { }
            }
        }

        private async ValueTask FireQueuedEventLoop()
        {
            fireChannel = Channel.CreateBounded<T>(MAX_CHANNEL_BOUND);
            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;

            while (!cancellationToken.IsCancellationRequested)
            {
                T toFire = default;
                toFire = await fireChannel.Reader.ReadAsync(cancellationToken);//fireQueue.Take();
                try
                {
                    DoFireEvent(ID, toFire);
                }
                catch (Exception) { }
            }
        }

        public virtual void DoFireEvent(string ID, T message)
        {
            FrameAvailableEvent?.Invoke(ID, message);
        }

        public abstract ReadOnlyMemory<byte> Data2Bytes(T data);

        public void UnsubscribeEventHandlers()
        {
            if (FrameAvailableEvent != null)
                foreach (var d in FrameAvailableEvent.GetInvocationList())
                    FrameAvailableEvent -= (d as FrameAvailableDelegate);
            FrameAvailableEvent = null;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    UnsubscribeEventHandlers();
                    
                    if (cancellationTokenSource != null && cancellationToken.CanBeCanceled)
                        cancellationTokenSource.Cancel();

                    if (fireTask != null)
                        fireTask.Wait();

                    cancellationTokenSource?.Dispose();
                    fireChannel?.Writer.Complete();
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
