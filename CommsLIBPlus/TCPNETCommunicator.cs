using CommsLIBPlus.Base;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.Helper;
using CommsLIBPlus.Memory;
using CommsLIBPlus.SmartPcap;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CommsLIBPlus.Communications
{
    public class TCPNETCommunicator<T> : CommunicatorBase<T>
    {
        #region logger
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        #region global defines
        private int RECEIVE_TIMEOUT = 4000;
        private const int SEND_TIMEOUT = 100; // Needed on linux as socket will not throw exception when send buffer full, instead blocks "forever"
        #endregion

        #region fields
        private bool disposedValue = false;

        private bool tcpClientProvided = false;
        private bool persistent = true;
        private long lastTX = 0;

        private Socket socket;
        private FrameWrapperBase<T> frameWrapper;

        private Task receiverTask;

        private Timer dataRateTimer;
        private int bytesAccumulatorRX = 0;
        private int bytesAccumulatorTX = 0;

        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;
        #endregion


        public TCPNETCommunicator(FrameWrapperBase<T> _frameWrapper = null) : base()
        {
            frameWrapper = _frameWrapper;
            tcpClientProvided = false;
        }

        public TCPNETCommunicator(Socket client, FrameWrapperBase<T> _frameWrapper = null) : base()
        {
            frameWrapper = _frameWrapper;

            // Do stuff
            tcpClientProvided = true;
            var IP = (client.RemoteEndPoint as IPEndPoint).Address.ToString();
            var Port = (client.RemoteEndPoint as IPEndPoint).Port;
            CommsUri = new ConnUri($"tcp://{IP}:{Port}");

            // When clint provided, persistent is forced false
            persistent = false;
        }

        #region CommunicatorBase

        public override void Init(ConnUri uri, bool persistent, string id, int inactivityMS)
        {
            if (State != STATE.STOP || uri == null || !uri.IsValid || tcpClientProvided)
                return;

            ID = id;
            RECEIVE_TIMEOUT = inactivityMS;
            if (frameWrapper != null) frameWrapper.ID = ID;

            CommsUri = uri ?? CommsUri;
            SetIPChunks(CommsUri?.IP);

            this.persistent = persistent;
        }

        public override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data)
        {
            if (State != STATE.RUNNING || socket == null)
                return 0;

            int nBytes = 0;

            try
            {
                nBytes = await socket.SendAsync(data, SocketFlags.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.Warn(e, "SendAsync Error");
                ClientDown();
            }

            bytesAccumulatorTX += nBytes;

            return nBytes;
        }

        public override void Start()
        {
            if (State == STATE.RUNNING)
                return;

            State = STATE.RUNNING;

            logger.Info("Start");

            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;
            receiverTask = tcpClientProvided ? Task.Run(ReceiveWorker, cancellationToken) : Task.Run(ConnectWorker, cancellationToken);
            dataRateTimer = new Timer(OnDataRate, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public override async Task Stop()
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            logger.Info("Stop");
            cancellationTokenSource.Cancel();
            dataRateTimer.Dispose();

            await receiverTask;

            State = STATE.STOP;
        }

        public override async ValueTask<int> SendAsync(T Message)
        {
            ReadOnlyMemory<byte> data = frameWrapper.Data2Bytes(Message);
            return await SendAsync(data);
;        }

        public override FrameWrapperBase<T> FrameWrapper { get => frameWrapper; }
        #endregion

        private void ClientDown()
        {
            if (socket == null)
                return;

            logger.Info("ClientDown - " + ID);

            bytesAccumulatorRX = 0;
            bytesAccumulatorTX = 0;

            try
            {
                socket?.Dispose();
            }
            catch (Exception e)
            {
                logger.Error(e, "ClientDown Exception");
            }
            finally
            {
                socket = null;
            }

            // Launch Event
            FireConnectionEvent(ID, CommsUri, false);
        }

        private void ClientUp()
        {
            bytesAccumulatorRX = 0;
            bytesAccumulatorTX = 0;

            // Launch Event
            FireConnectionEvent(ID, CommsUri, true);
        }

        private async Task ConnectWorker()
        {
            while (!cancellationToken.IsCancellationRequested && persistent)
            {
                logger.Info("Waiting for new connection");

                // Blocks here for timeout
                using (socket = new Socket(SocketType.Stream, ProtocolType.Tcp))
                {
                    try
                    {
                        await socket.ConnectAsync(CommsUri.IP, CommsUri.Port).ConfigureAwait(false);
                        await ReceiveWorker().ConfigureAwait(false);
                    } catch (Exception e)
                    {
                        logger.Warn(e, "ConnectWorker Error");
                        ClientDown();
                    }
                    
                }

            } 
        }

        private async ValueTask ReceiveWorker()
        {
            if (socket != null)
            {
                socket.SendTimeout = SEND_TIMEOUT;
                socket.ReceiveTimeout = RECEIVE_TIMEOUT;

                // Launch event and Add to Dictionary of valid connections
                ClientUp();

                // Memories
                int rx;

                try
                {
                    using var rxMemory = MemoryPool<byte>.Shared.Rent(8192);

                    while ((rx = await socket.ReceiveAsync(rxMemory.Memory, SocketFlags.None, cancellationToken)) > 0)
                    {
                        // Update Accumulator
                        bytesAccumulatorRX += rx;

                        // Slice
                        ReadOnlyMemory<byte> data = rxMemory.Memory.Slice(0, rx);

                        // RAW Data Event
                        FireDataEvent(CommsUri.IP,
                                        CommsUri.Port,
                                        HelperTools.GetLocalMicrosTime(),
                                        data,
                                        ID,
                                        IpChunks);

                        // Feed to FrameWrapper
                        frameWrapper?.AddBytes(data);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error while receiving TCPNet");
                }
                finally
                {
                    ClientDown();
                }
            }
        }

        private void OnDataRate(object state)
        {
            float dataRateMpbsRX = (bytesAccumulatorRX * 8f) / 1048576; // Mpbs
            float dataRateMpbsTX = (bytesAccumulatorTX * 8f) / 1048576; // Mpbs

            bytesAccumulatorRX = 0;
            bytesAccumulatorTX = 0;

            FireDataRateEvent(ID, dataRateMpbsRX, dataRateMpbsTX);
        }


        protected override async void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    await Stop();
                }

                socket = null;
                dataRateTimer = null;

                disposedValue = true;
            }

            base.Dispose(disposing);
        }
    }

}
