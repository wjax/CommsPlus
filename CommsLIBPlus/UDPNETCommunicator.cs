﻿using CommsLIBPlus.Base;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.Helper;
using CommsLIBPlus.SmartPcap;
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.Communications
{
    public class UDPNETCommunicator<T> : CommunicatorBase<T>
    {
        #region logger
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        #region global defines
        private int RECEIVE_TIMEOUT = 4000;
        private const int CONNECTION_TIMEOUT = 5000;
        private const int SEND_TIMEOUT = 100; // Needed on linux as socket will not throw exception when send buffer full, instead blocks "forever"
        private int MINIMUM_SEND_GAP = 0;
        #endregion

        #region fields
        private bool disposedValue = false;
        private long LastTX = 0;

        private volatile bool exit = false;

        private Socket socket;
        private FrameWrapperBase<T> frameWrapper;

        private IPEndPoint remoteEP;

        private IPEndPoint remoteIPEPSource = new IPEndPoint(IPAddress.Any, 0);
        public IPEndPoint RemoteIPEPSource { get => remoteEPSource as IPEndPoint; }
        private EndPoint remoteEPSource;
        private EndPoint bindEP;

        private Timer dataRateTimer;
        private int bytesAccumulatorRX = 0;
        private int bytesAccumulatorTX = 0;
        #endregion

        public UDPNETCommunicator(FrameWrapperBase<T> _frameWrapper = null) : base()
        {
            frameWrapper = _frameWrapper != null ? _frameWrapper : null;
            remoteEPSource = (EndPoint)remoteIPEPSource;
        }


        #region CommunicatorBase
        public override void Init(ConnUri uri, bool persistent, string id, int inactivityMS, int _sendGap = 0)
        {
            if (uri == null || !uri.IsValid)
                return;

            ID = id;
            MINIMUM_SEND_GAP = _sendGap;
            RECEIVE_TIMEOUT = inactivityMS;
            if (frameWrapper != null) frameWrapper.ID = ID;
            State = STATE.STOP;

            CommsUri = uri;
            SetIPChunks(CommsUri.IP);

            remoteEP = new IPEndPoint(IPAddress.Parse(uri.IP), uri.Port);
            if (string.IsNullOrEmpty(uri.BindIP))
                bindEP = new IPEndPoint(IPAddress.Any, CommsUri.LocalPort);
            else
                bindEP = new IPEndPoint(IPAddress.Parse(uri.BindIP), CommsUri.LocalPort);
        }

        public override async ValueTask<int> SendAsync(Memory<byte> payload, bool lengthPrefix = true)
        {
            return await socket.SendAsync(payload, SocketFlags.None);
        }

        public override bool SendSync(byte[] bytes, int offset, int length)
        {
            return Send2Equipment(bytes, offset, length, udpEq);
        }

        public override void Start()
        {
            if (State == STATE.RUNNING)
                return;

            logger.Info("Start");
            exit = false;

            senderTask = new Task(DoSendStart, TaskCreationOptions.LongRunning);
            receiverTask = new Task(Connect2EquipmentCallback, TaskCreationOptions.LongRunning);

            senderTask.Start();
            receiverTask.Start();

            dataRateTimer = new Timer(OnDataRate, null, 1000, 1000);

            State = STATE.RUNNING;
        }

        public override async Task Stop()
        {
            logger.Info("Stop");
            exit = true;

            dataRateTimer.Dispose();

            messageQueu.Reset();
            udpEq.ClientImpl?.Dispose();

            await senderTask;
            await receiverTask;

            State = STATE.STOP;
        }

        public override void SendSync(T Message)
        {
            byte[] buff = frameWrapper.Data2BytesSync(Message, out int count);
            if (count > 0)
                SendSync(buff, 0, count);
        }

        public override FrameWrapperBase<T> FrameWrapper { get => frameWrapper; }
        #endregion

        private void ClientDown()
        {
            if (!udpEq.Connected)
                return;

            logger.Info("ClientDown - " + udpEq.ID);
            bytesAccumulatorRX = 0;
            bytesAccumulatorTX = 0;

            try
            {
                udpEq.ClientImpl?.Close();
                udpEq.ClientImpl?.Dispose();
            }
            catch (Exception e)
            {
                logger.Error(e, "ClientDown Exception");
            }
            finally
            {
                udpEq.ClientImpl = null;
            }

            // Launch Event
            FireConnectionEvent(udpEq.ID, udpEq.ConnUri, false);

            udpEq.Connected = false;
        }

        private void ClientUp(UdpClient o)
        {
            if (!udpEq.Connected)
            {
                udpEq.Connected = true;
                bytesAccumulatorRX = 0;
                bytesAccumulatorTX = 0;
                // Launch Event
                FireConnectionEvent(udpEq.ID, udpEq.ConnUri, true);
            }
        }

        private void DoSendStart()
        {
            long toWait = 0;
            while (!exit)
            {
                try
                {
                    int read = messageQueu.Take(ref txBuffer, 0);

                    if ((toWait = TimeTools.GetCoarseMillisNow() - LastTX) < MINIMUM_SEND_GAP)
                        Thread.Sleep((int)toWait);

                    Send2Equipment(txBuffer, 0, read, udpEq);
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Exception in messageQueue");
                }
            }
            Console.WriteLine("Exited sender task");
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private bool Send2Equipment(byte[] data, int offset, int length, CommEquipmentObject<UdpClient> o)
        {
            if (o == null || o.ClientImpl == null)
                return false;

            string ID = o.ID;
            int nSent = 0;
            UdpClient t = o.ClientImpl;
            try
            {
                nSent = t.Client.SendTo(data, offset, length, SocketFlags.None, remoteEP);

                bytesAccumulatorTX += nSent;
                LastTX = TimeTools.GetCoarseMillisNow();
            }
            catch (Exception e)
            {
                logger.Error(e, "Error while sending UDPNet");
                // Client Down
                ClientDown();

                return false;
            }

            return true;
        }

        private void Connect2EquipmentCallback()
        {
            do
            {
                logger.Info("Waiting for new connection");

                using (UdpClient t = new UdpClient())
                {
                    try
                    {
                        t.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        t.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 128);
                        t.Client.Bind(bindEP);

                        t.Client.SendTimeout = SEND_TIMEOUT;
                        t.Client.ReceiveTimeout = RECEIVE_TIMEOUT;

                        if (IsMulticast(udpEq.ConnUri.IP, out IPAddress adr))
                            JoinMulticastOnSteroids(t.Client, udpEq.ConnUri.IP);


                        if (t != null)
                        {
                            int rx;
                            udpEq.ClientImpl = t;
                            try
                            {
                                if (RECEIVE_TIMEOUT == 0)
                                    ClientUp(t);

                                // Make first reception with RecveiveFrom. Just first to avoid allocations for IPEndPoint
                                if ((rx = udpEq.ClientImpl.Client.ReceiveFrom(rxBuffer, ref remoteEPSource)) > 0)
                                {
                                    // Launch event and Add to Dictionary of valid connections
                                    ClientUp(t);
                                    // Update Accumulator
                                    bytesAccumulatorRX += rx;
                                    // Update RX Time
                                    udpEq.timeLastIncoming = TimeTools.GetCoarseMillisNow();

                                    // RAW Data Event
                                    FireDataEvent(CommsUri.IP,
                                                        CommsUri.Port,
                                                        HelperTools.GetLocalMicrosTime(),
                                                        rxBuffer,
                                                        0,
                                                        rx,
                                                        udpEq.ID,
                                                        IpChunks);

                                    // Feed to FrameWrapper
                                    frameWrapper?.AddBytes(rxBuffer, rx);
                                }
                                // Following receives does not allocate remoteEPSource
                                while ((rx = udpEq.ClientImpl.Client.Receive(rxBuffer)) > 0)
                                {
                                    // Update Accumulator
                                    bytesAccumulatorRX += rx;
                                    // Update RX Time
                                    udpEq.timeLastIncoming = TimeTools.GetCoarseMillisNow();

                                    // RAW Data Event
                                    FireDataEvent(CommsUri.IP,
                                                        CommsUri.Port,
                                                        HelperTools.GetLocalMicrosTime(),
                                                        rxBuffer,
                                                        0,
                                                        rx,
                                                        udpEq.ID,
                                                        IpChunks);

                                    // Feed to FrameWrapper
                                    frameWrapper?.AddBytes(rxBuffer, rx);
                                }
                            }
                            catch (Exception e)
                            {
                                logger.Error(e, "Error while receiving UDPNet");
                            }
                            finally
                            {
                                ClientDown();
                            }
                        }
                    }
                    catch (Exception eInner)
                    {
                        logger.Error(eInner, "Error while connecting Inner");
                    }
                }
                if (!exit) Thread.Sleep(CONNECTION_TIMEOUT);

            } while (!exit && udpEq.IsPersistent);

            logger.Info("Exited Connect2EquipmentCallback");
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

        protected override async void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    await Stop();

                    (messageQueu as IDisposable).Dispose();
                    udpEq.ClientImpl?.Dispose();
					dataRateTimer?.Dispose();
                }

                messageQueu = null;
                udpEq.ClientImpl = null;
				dataRateTimer = null;

                disposedValue = true;
            }

            base.Dispose(disposing);
        }
    }

    

}
