using CommsLIBPlus.Base;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.FrameWrappers;
using CommsLIBPlus.SmartPcap;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.Communications
{
    public class IPSocketCommunicator<T> : CommunicatorBase<T>
    {
        #region logger
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        #region global defines
        private int RECEIVE_TIMEOUT = 4000;
        private const int SEND_TIMEOUT = 100; // Needed on linux as socket will not throw exception when send buffer full, instead blocks "forever"

        /// <summary>
        /// Winsock ioctl code which will disable ICMP errors from being propagated to a UDP socket.
        /// This can occur if a UDP packet is sent to a valid destination but there is no socket
        /// registered to listen on the given port.
        /// </summary>

        private const int SIO_UDP_CONNRESET = -1744830452;
        private byte[] byteTrue = { 0x00, 0x00, 0x00, 0x01 };
        #endregion

        #region fields
        private bool disposedValue = false;

        private bool clientProvided = false;
        private bool persistent = true;

        private Socket socket;
        private FrameWrapperBase<T> frameWrapper;

        private uint IPUInt;

        private Task receiverTask;

        private Timer dataRateTimer;
        private int bytesAccumulatorRX = 0;
        private int bytesAccumulatorTX = 0;

        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;

        //private MemoryPool<byte> memoryPool = KestrelMemoryPool.Create();
        #endregion


        public IPSocketCommunicator(FrameWrapperBase<T> _frameWrapper = null) : base()
        {
            frameWrapper = _frameWrapper;
            clientProvided = false;

            State = STATE.STOP;
        }

        public IPSocketCommunicator(Socket client, FrameWrapperBase<T> _frameWrapper = null) : base()
        {
            frameWrapper = _frameWrapper;
            socket = client;
            clientProvided = true;

            // Do stuff
            var IP = (client.RemoteEndPoint as IPEndPoint).Address.ToString();
            var Port = (client.RemoteEndPoint as IPEndPoint).Port;
            CommsUri = new ConnUri($"{(client.ProtocolType == ProtocolType.Tcp ? "tcp" : "udp")}://{IP}:{Port}");
            IPUInt = HelperTools.IP2UInt(IP);

            // When client provided, persistent is forced false
            persistent = false;

            State = CommunicatorBase<T>.STATE.STOP;
        }
        #region CommunicatorBase

        public override void Init(ConnUri uri, bool persistent, string id, int inactivityMS)
        {
            if (State != STATE.STOP || 
                ((uri == null || !uri.IsValid) && !clientProvided))
                return;

            ID = id;
            this.persistent = persistent;
            RECEIVE_TIMEOUT = inactivityMS;
            if (frameWrapper != null) frameWrapper.ID = ID;

            CommsUri = uri ?? CommsUri;
        }

        public override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data)
        {
            if (State != STATE.RUNNING || socket == null)
                return 0;

            int nBytes = 0;

            if (socket != null && socket.Connected)
            {
                try
                {
                    nBytes = await socket.SendAsync(data, SocketFlags.None).ConfigureAwait(false);
                    bytesAccumulatorTX += nBytes;
                }
                catch (Exception e)
                {
                    logger.Warn(e, "SendAsync Error");
                    ClientDown();
                }
            }

            return nBytes;
        }

        public override async ValueTask<int> SendAsync(T Message)
        {
            ReadOnlyMemory<byte> data = frameWrapper.Data2Bytes(Message);
            return await SendAsync(data);
        }

        public override async Task<T> SendReceiveAsync(T message)
        {
            if (message is Identifiable messageID)
            {
                var tcs = new TaskCompletionSource<T>();

                var data = frameWrapper.Data2Bytes(message);
                await SendAsync(data);
                return await frameWrapper.WaitForResponse(messageID.ID, tcs);
            }
            else
                throw new ArgumentException();

        }

        public override void Start()
        {
            if (State == STATE.RUNNING)
                return;

            State = STATE.RUNNING;
            logger.Info("Start");

            FrameWrapper?.Start();

            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;
            receiverTask = clientProvided ? Task.Run(ReceiveWorker, cancellationToken) : Task.Run(ConnectWorker, cancellationToken);
            dataRateTimer = new Timer(OnDataRate, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public override async Task Stop()
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            logger.Info("Stop");

            FrameWrapper?.Stop();
            cancellationTokenSource.Cancel();
            
            await receiverTask;

            cancellationTokenSource.Dispose(); cancellationTokenSource = null;
            dataRateTimer.Dispose(); dataRateTimer = null;

            State = STATE.STOP;
        }

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
            do
            {
                logger.Info("Waiting for new connection");

                // Blocks here for timeout
                using (socket = CommsUri.UriType == ConnUri.TYPE.TCP ? new Socket(SocketType.Stream, ProtocolType.Tcp) : new Socket(SocketType.Dgram, ProtocolType.Udp))
                {
                    try
                    {
                        if (CommsUri.UriType == ConnUri.TYPE.UDP)
                        {
                            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 128);
                            socket.Bind(string.IsNullOrEmpty(CommsUri.BindIP) ? new IPEndPoint(IPAddress.Any, CommsUri.LocalPort) : new IPEndPoint(IPAddress.Parse(CommsUri.BindIP), CommsUri.LocalPort));

                            socket.IOControl(SIO_UDP_CONNRESET, byteTrue, null);
                        }
                        if (IsMulticast(CommsUri.IP, out IPAddress adr))
                            JoinMulticastOnSteroids(socket, CommsUri.IP);

                        socket.SendTimeout = SEND_TIMEOUT;
                        socket.ReceiveTimeout = RECEIVE_TIMEOUT;

                        // Connect
                        if (CommsUri.UriType == ConnUri.TYPE.TCP)
                            await socket.ConnectAsync(CommsUri.IP, CommsUri.Port).ConfigureAwait(false);

                        // Start Receiving
                        await ReceiveWorker().ConfigureAwait(false);

                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "ConnectWorker Error");
                        ClientDown();
                    }
                    finally
                    {
                    }

                }

            } while (!cancellationToken.IsCancellationRequested && persistent);
        }

        private async ValueTask ReceiveWorker()
        {
            if (socket != null)
            {
                // Launch event
                ClientUp();

                // Memories
                int rx;
                using var memoryO = MemoryPool<byte>.Shared.Rent(65536);

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        rx = await socket.ReceiveAsync(memoryO.Memory, SocketFlags.None, cancellationToken).ConfigureAwait(false);

                        // Socket closed
                        if (rx <= 0)
                            break;

                        // Update Accumulator
                        bytesAccumulatorRX += rx;

                        // Slice
                        ReadOnlyMemory<byte> data = memoryO.Memory.Slice(0, rx);

                        // RAW Data Event
                        FireDataEvent(CommsUri.IP,
                                        CommsUri.Port,
                                        HelperTools.GetLocalMicrosTime(),
                                        data,
                                        ID,
                                        IPUInt);

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

        private bool IsMulticast(string ip, out IPAddress adr)
        {
            bool bResult = false;
            if (IPAddress.TryParse(ip, out adr))
            {
                byte first = adr.GetAddressBytes()[0];
                if ((first & 0xF0) == 0xE0)
                    bResult = true;
            }

            return bResult;
        }

        /// <summary>
        /// Makes socket join a multicast group in every interface in the machine
        /// </summary>
        /// <param name="s">Socket</param>
        /// <param name="multicastIP">Multicast group</param>
        private void JoinMulticastOnSteroids(Socket s, string multicastIP)
        {
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in nics)
            {
                IPInterfaceProperties ip_properties = adapter.GetIPProperties();
                //if (!adapter.GetIPProperties().MulticastAddresses.Any())
                //    continue; // most of VPN adapters will be skipped
                //if (!adapter.SupportsMulticast)
                //    continue; // multicast is meaningless for this type of connection
                //if (OperationalStatus.Up != adapter.OperationalStatus)
                //    continue; // this adapter is off or not connected
                //IPv4InterfaceProperties p = adapter.GetIPProperties().GetIPv4Properties();
                //if (null == p)
                //    continue; // IPv4 is not configured on this adapter
                //s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, (int)IPAddress.HostToNetworkOrder(p.Index));
                //if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                //{
                foreach (UnicastIPAddressInformation ip in adapter.GetIPProperties().UnicastAddresses)
                {
                    try
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(System.Net.IPAddress.Parse(multicastIP), ip.Address));
                    }
                    catch (Exception) { }
                }
                //}
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop().Wait();

                    //memoryPool.Dispose();
                    frameWrapper?.Dispose();
                }

                logger = null;
                disposedValue = true;
            }

            base.Dispose(disposing);
        }
    }

}
