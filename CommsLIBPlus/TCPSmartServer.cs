using CommsLIBPlus.Communications;
using CommsLIBPlus.Communications.FrameWrappers;
using ProtoBuf;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.Comms
{
    public class TCPSmartServer<T , U> : IDisposable where T : FrameWrapperBase<U>
    {
        #region logger
        private NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        #region consts
        private const int RCV_BUFFER_SIZE = 8192;
        #endregion

        #region members
        private TcpListener server = null;
        private int ListeningPort;
        private string ListeningIP;

        private Dictionary<string, CommunicatorBase<U>> ClientList = new Dictionary<string, CommunicatorBase<U>>();
        private object lockerClientList = new object();

        #region events
        public event DataReadySyncDelegate DataReadySyncEvent;
        public delegate void DataReadySyncDelegate(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint);

        public event DataReadyAsyncDelegate DataReadyAsyncEvent;
        public delegate void DataReadyAsyncDelegate(string ip, int port, long time, IMemoryOwner<byte> data, string ID, uint ipuint);

        public delegate void FrameReadyDelegate(U message, string ID);
        public event FrameReadyDelegate FrameReadyEvent;

        public delegate void ConnectionStateDelegate(string SourceID,  bool connected);
        public event ConnectionStateDelegate ConnectionStateEvent;
        #endregion

        private Task listenTask;
        private CancellationTokenSource cancelListenSource;
        private CancellationToken cancelListenToken;

        private Task senderTask;
        private CancellationTokenSource cancelSenderSource;
        private CancellationToken cancelSenderToken;

        #endregion

        #region fields
        public enum STATE
        {
            RUNNING,
            STOP
        }
        public STATE State;

        private readonly Func<FrameWrapperBase<U>> createFrameWrapperFunc;
        #endregion

        public TCPSmartServer(int _port, Func<FrameWrapperBase<U>> _createFrameWrapperFunc, string _ip = null)
        {
            ListeningPort = _port;
            ListeningIP = _ip;
            createFrameWrapperFunc = _createFrameWrapperFunc;

            State = STATE.STOP;
        }

        public void Start()
        {
            if (State == STATE.RUNNING)
                return;

            server = new TcpListener(string.IsNullOrEmpty(ListeningIP) ? IPAddress.Any : IPAddress.Parse(ListeningIP), ListeningPort);
            server.Start();

            cancelListenSource = new CancellationTokenSource();
            cancelListenToken = cancelListenSource.Token;
            listenTask = Task.Run(async () => DoListenForClients(server, cancelListenToken), cancelListenToken);
        }

        public async Task Stop()
        {
            if (State == STATE.STOP)
                return;

            if (cancelListenToken.CanBeCanceled)
            {
                cancelListenSource.Cancel();
                server.Stop();
                server = null;
                listenTask.Wait();

                // Stop clients
                foreach (var client in ClientList.Values)
                {
                    // Deregister events
                    client.ConnectionStateEvent -= OnCommunicatorConnection;
                    client.DataReadySyncEvent -= OnCommunicatorSyncData;
                    client.DataReadyAsyncEvent -= OnCommunicatorAsyncData;
                    client.FrameWrapper.FrameAvailableEvent -= OnFrameReady;

                    await client.Stop().ConfigureAwait(false);
                    client.Dispose();
                }
                // Clean
                ClientList.Clear();
            }
        }

        private static string GetIDFromSocket(Socket s)
        {
            return (s.RemoteEndPoint as IPEndPoint).Address.ToString() + ":" + (s.RemoteEndPoint as IPEndPoint).Port.ToString();
        }

        private async Task DoListenForClients(object state, CancellationToken token)
        {
            TcpListener _server = (state as TcpListener);

            while (!cancelListenToken.IsCancellationRequested)
            {
                logger.Info("Waiting for a connection... ");

                // Perform a blocking call to accept requests.
                Socket socket = await _server.AcceptSocketAsync().ConfigureAwait(false);
                // Get ID
                string id = GetIDFromSocket(socket);
                // Create Framewrapper
                var framewrapper = createFrameWrapperFunc();
                // Create Communicator
                CommunicatorBase<U> communicator = new IPSocketCommunicator<U>(socket, framewrapper);

                // Add to dict 
                lock (lockerClientList)
                    ClientList.Add(id, communicator);

                // Subscribe to events
                communicator.ConnectionStateEvent += OnCommunicatorConnection;
                communicator.DataReadySyncEvent += OnCommunicatorSyncData;
                communicator.DataReadyAsyncEvent += OnCommunicatorAsyncData;
                framewrapper.FrameAvailableEvent += OnFrameReady;

                communicator.Init(null, false, id, 0);
                communicator.Start();
            }
        }

        private void OnFrameReady(string ID, U payload)
        {
            // Raise
            FrameReadyEvent?.Invoke(payload, ID);
        }

        private void OnCommunicatorAsyncData(string ip, int port, long time, IMemoryOwner<byte> data, string ID, uint ipuint)
        {
            // Launched by threadpool in dedicated thread (different from socket). Dispose IMemoryOwner so it goes back to Pool
            DataReadyAsyncEvent?.Invoke(ip, port, time, data, ID, ipuint);
        }

        private void OnCommunicatorSyncData(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint)
        {
            // Same socket receive thread. Memory must be consumed in this method
            DataReadySyncEvent?.Invoke(ip, port, time, data, ID, ipuint);
        }

        private void OnCommunicatorConnection(string ID, CommsLIBPlus.Base.ConnUri uri, bool connected)
        {
            // Raise
            ConnectionStateEvent?.Invoke(ID, connected);

            // Remove if disconnected
            if (!connected)
            {
                lock (lockerClientList)
                {
                    var communicator = ClientList[ID];
                    communicator.Dispose();
                    ClientList.Remove(ID);
                }
            }
                  
        }

        public void Send2All(U data)
        {
            lock (lockerClientList)
            {
                foreach (KeyValuePair<string, CommunicatorBase<U>> kv in ClientList)
                    _ = kv.Value.SendAsync(data);
            }
        }

        public void Send2All(ReadOnlyMemory<byte> data)
        {
            lock (lockerClientList)
            {
                foreach (KeyValuePair<string, CommunicatorBase<U>> kv in ClientList)
                    _ = kv.Value.SendAsync(data);
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        // TODO
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~TCPSmartServer()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
