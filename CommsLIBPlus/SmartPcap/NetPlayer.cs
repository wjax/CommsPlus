using CommsLIBPlus.Base;
using CommsLIBPlus.Communications;
using CommsLIBPlus.SmartPcap.Base;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.SmartPcap
{
    public class NetPlayer : IDisposable
    {
        public enum State
        {
            Stoped,
            Playing,
            Paused
        }

        #region logger
        private NLog.Logger logger;
        #endregion

        #region events
        public delegate void DataRateDelegate(Dictionary<string, float> dataRates);
        public event DataRateDelegate DataRateEvent;
        public delegate void StatusDelegate(State state);
        public event StatusDelegate StatusEvent;
        public delegate void ProgressDelegate(int time);
        public event ProgressDelegate ProgressEvent;
        #endregion

        #region fields & properties
        private string filePath = "";
        private string idxFilePath = "";
        private FileStream file;
        private FileStream idxFile;

        private int secsIdxInterval;
        private bool onlyLocal = false;

        private byte[] buffer;

        private byte[] idxBuffer = new byte[HelperTools.idxIndexSize];

        private Task runningJob;
        private CancellationTokenSource cancelSource;
        private CancellationToken cancelToken;

        private bool resetTimeOffsetRequired = true;
        private volatile bool has2UpdatePosition = false;
        private long newPosition = 0;

        private Dictionary<int, PlayPeerInfo> udpSenders = new Dictionary<int, PlayPeerInfo>();
        private Dictionary<int, PlayPeerInfo> udpSendersOriginal = new Dictionary<int, PlayPeerInfo>();
        private Dictionary<string, float> _dataRate = new Dictionary<string, float>();

        private DateTime RecDateTime = DateTime.MinValue;

        public State CurrentState { get; private set; }

        private Dictionary<long, long> timeIndexes = new Dictionary<long, long>();
        private int lastTime;
        private long replayTime;
        #endregion

        #region timer
        private Timer eventTimer;
        #endregion

        ArrayPool<byte> bytePool = ArrayPool<byte>.Shared;


        public NetPlayer()
        {
            try
            {
                logger = NLog.LogManager.GetCurrentClassLogger();
            }
            catch { }

            buffer = bytePool.Rent(HelperTools.SIZE_BYTES);
        }

        /// <summary>
        /// Retrieves information from the recording idx file
        /// </summary>
        /// <param name="_idxFilePath"></param>
        /// <returns>Peers, recording date and time, duration in secs, recording size in bytes</returns>
        public static (ICollection<PlayPeerInfo>, DateTime, int, long) GetRecordingInfo(string _idxFilePath)
        {
            int duration = 0;
            int nPeers = 0;
            int secsIdxInterval = 0;
            DateTime date = DateTime.MinValue;
            List<PlayPeerInfo> peers = new List<PlayPeerInfo>();

            using (FileStream idxFile = new FileStream(_idxFilePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader idxBinaryReader = new BinaryReader(idxFile))
            {
                Span<byte> auxBuff = stackalloc byte[1024];

                // Read DateTime, Secs Interval and Peers info
                date = HelperTools.fromMillis(idxBinaryReader.ReadInt64());
                secsIdxInterval = idxBinaryReader.ReadInt32();
                nPeers = idxBinaryReader.ReadInt32();

                // Read peers
                for (int i = 0; i < nPeers; i++)
                {
                    string ID = HelperTools.Bytes2StringWithLength(idxFile);
                    string IP = HelperTools.Bytes2StringWithLength(idxFile);
                    int Port = idxBinaryReader.ReadInt32();

                    peers.Add(new PlayPeerInfo
                        {
                            ID = ID,
                            IP = IP,
                            Port = Port
                        });
                }

                // Guess duration
                FileInfo fInfo = new FileInfo(_idxFilePath);
                duration = ((int)((fInfo.Length - idxBinaryReader.BaseStream.Position) / HelperTools.idxIndexSize)) * secsIdxInterval;
            }

            string rawFile = Path.Combine(Path.GetDirectoryName(_idxFilePath), Path.GetFileNameWithoutExtension(_idxFilePath)) + ".raw";
            FileInfo fRaw = new FileInfo(rawFile);

            return (peers, date, duration, fRaw.Length);
        }

        /// <summary>
        /// Load a recording file
        /// </summary>
        /// <param name="_filePath">Raw file path</param>
        /// <param name="_startTime">out Start date and time</param>
        /// <param name="_lastTime">out Last time in recording</param>
        /// <returns>Collection of peers recorded in the file</returns>
        public ICollection<PlayPeerInfo> LoadFile(string _filePath, out DateTime _startTime, out int _lastTime)
        {
            if (CurrentState != State.Stoped)
                throw new InvalidOperationException("Playing in progress. Must stop to load file");

            filePath = _filePath;
            idxFilePath = Path.Combine(Path.GetDirectoryName(filePath),Path.GetFileNameWithoutExtension(filePath)) + ".idx";

            // Init stuff
            _lastTime = 0;
            _startTime = DateTime.MinValue;
            timeIndexes.Clear();
            int nPeers = 0;
            udpSendersOriginal.Clear();
            udpSenders.Clear();
            _dataRate.Clear();

            using (FileStream idxFile = new FileStream(idxFilePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader idxBinaryReader = new BinaryReader(idxFile))
            {
                int time = 0; long position = 0;
                Span<byte> auxBuff = stackalloc byte[4096];

                // Read DateTime, Secs Interval and Peers info
                RecDateTime = _startTime = HelperTools.fromMillis(idxBinaryReader.ReadInt64());
                secsIdxInterval = idxBinaryReader.ReadInt32();
                nPeers = idxBinaryReader.ReadInt32();

                // Read peers
                for (int i = 0; i < nPeers; i++)
                {
                    string ID = HelperTools.Bytes2StringWithLength(idxFile);
                    string IP = HelperTools.Bytes2StringWithLength(idxFile);
                    int Port = idxBinaryReader.ReadInt32();

                    udpSenders.Add(HelperTools.GetDeterministicHashCode(ID),
                        new PlayPeerInfo
                        {
                            ID = ID,
                            IP = IP,
                            Port = Port
                        });

                    udpSendersOriginal.Add(HelperTools.GetDeterministicHashCode(ID),
                        new PlayPeerInfo
                        {
                            ID = ID,
                            IP = IP,
                            Port = Port
                        });

                    _dataRate[ID] = 0f;
                }

                Span<byte> timeBuff = stackalloc byte[HelperTools.idxIndexSize];
                // Get All times to cache
                while (idxFile.Read(timeBuff) == HelperTools.idxIndexSize)
                {
                    time = BinaryPrimitives.ReadInt32LittleEndian(timeBuff);
                    position = BinaryPrimitives.ReadInt64LittleEndian(timeBuff.Slice(4));

                    try
                    {
                        timeIndexes.Add(time, position);
                    }
                    catch(Exception e)
                    {
                        logger.Error(e, "Error reading idx file. TimeIndexes");
                    }
                }
                _lastTime = lastTime = time;
            }
            return udpSenders.Values;
        }

        private void OnDataRate(string ID, float MbpsTX)
        {
            _dataRate[ID] = MbpsTX;
        }

        /// <summary>
        /// Seeks into the recording
        /// </summary>
        /// <param name="_time">Time in secs</param>
        public void Seek(int _time)
        {
            // Find closest time in Dictionary
            if (_time <= lastTime)
            {
                int seqTime = (_time / (secsIdxInterval == 0 ? 1: secsIdxInterval)) * secsIdxInterval;

                if (timeIndexes.ContainsKey(seqTime))
                {
                    // DO actual Seek
                    newPosition = timeIndexes[seqTime];
                    has2UpdatePosition = true;
                }
            }
        }

        /// <summary>
        /// Starts playing
        /// </summary>
        /// <param name="_onlyLocal">If true, data will not leave the machine</param>
        public void Play(bool _onlyLocal = true)
        {
            if (CurrentState == State.Playing)
                return;

            if (string.IsNullOrEmpty(filePath))
                return;

            if (CurrentState == State.Paused)
                Resume();
            else
            {
                CurrentState = State.Playing;
                resetTimeOffsetRequired = true;

                onlyLocal = _onlyLocal;
                cancelSource = new CancellationTokenSource();
                cancelToken = cancelSource.Token;
                
                // Create Senders
                foreach (PlayPeerInfo p in udpSenders.Values)
                {
                    var u = new UDPSender(p.ID, p.IP, p.Port, onlyLocal);
                    u.DataRateEvent += OnDataRate;
                    u.Start();

                    p.commsLink = u;
                }

                // Start Timer
                eventTimer = new Timer(OnEventTimer, null, 0, 1000);
                // Start Playing Task
                runningJob = Task.Run(() => RunReaderSenderProcessCallback(filePath, idxFilePath, cancelToken));

                StatusEvent?.Invoke(CurrentState);
            }
        }

        private void CloseSenders()
        {
            foreach (var u in udpSenders.Values)
            {
                u.commsLink.DataRateEvent -= OnDataRate;
                (u.commsLink as IDisposable).Dispose();
            }
        }

        private void RunReaderSenderProcessCallback(string _filePath, string _idxFilePath, CancellationToken token)
        {
            ulong ip;
            int n_bytes = 0, size = 0;
            int hashedID = 0;
            long timeoffset = 0, waitTime = 0, nowTime = 0, time = 0;
            int lastTimeStatusS = 0, timeStatusS = 0;

            // Increase timers resolution
            WinAPI.WinAPITime.TimeBeginPeriod(1);

            // Open file
            using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                if (has2UpdatePosition)
                {
                    file.Seek(newPosition, SeekOrigin.Begin);
                    has2UpdatePosition = false;
                    resetTimeOffsetRequired = true;
                }

                while (!token.IsCancellationRequested)
                {
                    // Paused
                    if (CurrentState == State.Paused)
                    {
                        Thread.Sleep(500);
                    }
                    else
                    {
                        if ((n_bytes = file.Read(buffer, 0, HelperTools.headerSize)) == HelperTools.headerSize)
                        {
                            // Get fields
                            replayTime = time = BitConverter.ToInt64(buffer, 0);
                            hashedID = BitConverter.ToInt32(buffer, 8);
                            ip = BitConverter.ToUInt64(buffer, 12);
                            size = BitConverter.ToInt32(buffer, 20);

                            // Update time in secs
                            timeStatusS = (int)(replayTime / 1000_000);

                            // Read Payload
                            n_bytes = file.Read(buffer, 0, size);

                            nowTime = HelperTools.GetLocalMicrosTime();

                            if (resetTimeOffsetRequired)
                            {
                                timeoffset = nowTime - time;
                                waitTime = 0;
                                resetTimeOffsetRequired = false;
                            }
                            else
                            {
                                nowTime -= timeoffset;
                                waitTime = time - nowTime;
                                if (waitTime > 1000) // 1ms
                                    Thread.Sleep((int)(waitTime / 1000));
                            }

                            // Send
                            Send(hashedID, buffer, 0, n_bytes);

                            // Update Progress
                            if (timeStatusS != lastTimeStatusS)
                            {
                                ProgressEvent?.Invoke(timeStatusS);
                                lastTimeStatusS = timeStatusS;
                            }

                            if (has2UpdatePosition)
                            {
                                file.Seek(newPosition, SeekOrigin.Begin);
                                has2UpdatePosition = false;
                                resetTimeOffsetRequired = true;
                            }
                        }
                        else
                        {
                            // End of File
                            cancelSource.Cancel();
                            // Update Stop Status
                            CurrentState = State.Stoped;
                            StatusEvent?.Invoke(CurrentState);
                            foreach (var k in _dataRate.Keys.ToList())
                                _dataRate[k] = 0;
                            DataRateEvent?.Invoke(_dataRate);
                            // rewind
                            Seek(0);
                            eventTimer.Dispose();
                            eventTimer = null;
                            ProgressEvent?.Invoke(0);
                        }
                    }
                }
            }
            WinAPI.WinAPITime.TimeEndPeriod(1);
        }

        private void Send(int hashedID, byte[] _buff, int offset, int count)
        {
            if (udpSenders.TryGetValue(hashedID, out PlayPeerInfo p) && p.IsEnabled)
                p.commsLink.Send(_buff, offset, count);
        }

        /// <summary>
        /// Enables or disables a given peer by ID
        /// </summary>
        /// <param name="_id">ID of the peer</param>
        /// <param name="enabled">Enable if true, disable if false</param>
        public void SetPeerEnabled(string _id, bool enabled)
        {
            int hashedID = HelperTools.GetDeterministicHashCode(_id);
            if (udpSenders.TryGetValue(hashedID, out PlayPeerInfo p))
                p.IsEnabled = enabled;
        }

        /// <summary>
        /// Override the default destination ip of a recorded peer with a given one.
        /// </summary>
        /// <param name="_ID">ID of the peer</param>
        /// <param name="_dstIP">New destination IP</param>
        /// <param name="_dstPort">New destination Port</param>
        /// <param name="_nic">NIC to send data from</param>
        /// <returns></returns>
        public bool AddReplayAddress(string _ID, string _dstIP, int _dstPort, string _nic)
        {
            if (CurrentState != State.Stoped)
                return false;

            int hashedID = HelperTools.GetDeterministicHashCode(_ID);
            if (udpSenders.TryGetValue(hashedID, out PlayPeerInfo p))
            {
                p.IP = _dstIP;
                p.Port = _dstPort;
                p.NIC = _nic;

                return true;
            }

            return false;
        }

        //public bool RemovePeer(string _id)
        //{
        //    if (CurrentState != State.Stoped)
        //        return false;

        //    return udpSenders.Remove(HelperTools.GetDeterministicHashCode(_id));
        //}

        /// <summary>
        /// Resets the ID Peer to its default
        /// </summary>
        /// <param name="_id">ID of the peer</param>
        /// <param name="pOriginal">Returns the default peer tha was present in the file</param>
        /// <returns></returns>
        public bool RemoveReplayAddress(string _id, out PlayPeerInfo pOriginal)
        {
            pOriginal = null;

            if (CurrentState != State.Stoped)
                return false;

            int hashedID = HelperTools.GetDeterministicHashCode(_id);
            if (udpSenders.TryGetValue(hashedID, out PlayPeerInfo p))
            {
                var original = udpSendersOriginal[hashedID];

                p.IP = original.IP;
                p.Port = original.Port;
                p.NIC = original.NIC;

                pOriginal = original;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Pauses the replay
        /// </summary>
        public void Pause()
        {
            if (CurrentState == State.Playing)
            {
                CurrentState = State.Paused;
                StatusEvent?.Invoke(CurrentState);
            }
        }

        /// <summary>
        /// Resumes the replay
        /// </summary>
        public void Resume()
        {
            if (CurrentState == State.Paused)
            {
                resetTimeOffsetRequired = true;
                CurrentState = State.Playing;
                StatusEvent?.Invoke(CurrentState);
            }
        }

        // Stops the replay and goes back to time 0
        public void Stop()
        {
            if (CurrentState == State.Stoped)
                return;

            if (cancelToken != null && cancelToken.CanBeCanceled)
            {
                cancelSource.Cancel();
                runningJob.Wait();
            }

            // Clean Senders
            CloseSenders();

            eventTimer?.Dispose();
            eventTimer = null;

            CurrentState = State.Stoped;
            StatusEvent?.Invoke(CurrentState);

            foreach (var k in _dataRate.Keys.ToList())
                _dataRate[k] = 0;
            DataRateEvent?.Invoke(_dataRate);

            ProgressEvent?.Invoke(0);
        }

        private void OnEventTimer(object state)
        {
            // Status event
            if (CurrentState == State.Playing)
            {
                // Datarate event
                DataRateEvent?.Invoke(_dataRate);
                //ProgressEvent?.Invoke((int)(replayTime / 1000000));
            }
        }

        public void Dispose()
        {
            CloseSenders();
            bytePool.Return(buffer);
        }
    }
}
