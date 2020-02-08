using CommsLIBPlus.Base;
using CommsLIBPlus.Communications;
using CommsLIBPlus.Helper;
using CommsLIBPlus.SmartPcap.Base;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.SmartPcap
{
    public class NetRecorder
    {
        public enum State
        {
            Stoped,
            Recording
        }

        #region logger
        private NLog.Logger logger;
        #endregion

        #region const
        private const int SAMPLES_DATARATE_AVERAGE = 100;
        #endregion

        #region events
        public delegate void StatusDelegate(State state);
        public event StatusDelegate StatusEvent;
        public delegate void ProgressDelegate(float mb, int secs);
        public event ProgressDelegate ProgressEvent;
        public delegate void DataRateDelegate(Dictionary<string, float> dataRates);
        public event DataRateDelegate DataRateEvent;
        #endregion

        #region fields & properties
        private string filePath = @".\dumptest.mpg";
        private string idxFilePath = @".\dumptest.idx";
        private string folder;
        private string name;
        private FileStream file;
        private FileStream idxFile;

        private long timeOffset = long.MinValue;
        private int secTime;
        private int secsIdxInterval;

        private Dictionary<string, RecPeerInfo> socketListeners = new Dictionary<string, RecPeerInfo>();
        private Dictionary<string, float> _dataRate = new Dictionary<string, float>();

        public State CurrentState { get; set; }
        #endregion

        #region timer
        private Timer eventTimer;
        #endregion

        public NetRecorder()
        {
            try
            {
                logger = NLog.LogManager.GetCurrentClassLogger();
            }
            catch { }
        }

        private void OnEventTimer(object state)
        {
            // Write IDX
            WriteIdx();
            // Datarate event
            DataRateEvent?.Invoke(_dataRate);
            // Update Time
            secTime++;

            // Status event
            if (CurrentState == State.Recording)
                ProgressEvent?.Invoke((float)(file.Position / 1048576.0), secTime);
        }

        /// <summary>
        /// Add a multicast peer to the recording
        /// </summary>
        /// <param name="ID">Given ID</param>
        /// <param name="ip">Multicast IP</param>
        /// <param name="port">Multicast destination port</param>
        /// <param name="dumpToFile">True if packets for this peer should be written to individual file</param>
        /// <param name="netcard">Not needed, will join multicast group in all interfaces</param>
        /// <param name="dumpFileExtension">Extension for the individual dump file</param>
        /// <returns>True if peer is added</returns>
        public bool AddPeer(string id, string ip, int port, bool dumpToFile, string netcard = "", string dumpFileExtension = ".dump")
        {
            if (CurrentState == State.Recording)
            {
                logger.Info($"Added Peer {id} - {ip}:{port} Failed. Recording ongoing");
                return false;
            }

            // Check valid IP
            if (IPAddress.TryParse(ip, out IPAddress ipAddres) && !socketListeners.ContainsKey(id))
            {
                IPSocketCommunicator<object> u = new IPSocketCommunicator<object>();
                u.Init(new ConnUri($"udp://{ip}::{netcard}:{port}"), false, id, 0);
                u.DataReadySyncEvent += DataReadyEventCallback;
                u.DataRateEvent += OnDataRate;

                RecPeerInfo p = new RecPeerInfo()
                {
                    ID = id,
                    DumpToFile = dumpToFile,
                    DumpFileExtension = dumpFileExtension,
                    commsLink = u,
                    IP = ip,
                    Port= port
                };

                socketListeners.Add(id, p);
                _dataRate.Add(id, 0);

                logger.Info($"Added Peer {id} - {ip}:{port} Sucessful");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes a previously added peer by ID
        /// </summary>
        /// <param name="id">Peer to remove</param>
        /// <returns></returns>
        public bool RemovePeer(string id)
        {
            if (CurrentState == State.Recording)
                return false;

            if (!string.IsNullOrEmpty(id) && socketListeners.TryGetValue(id, out var u))
            {
                u.commsLink.DataReadySyncEvent -= DataReadyEventCallback;
                u.commsLink.DataRateEvent -= OnDataRate;
                bool doneRight = _dataRate.Remove(id) && socketListeners.Remove(id);
                return doneRight;
            }

            return false;
        }

        /// <summary>
        /// Add a generic peer to the recording
        /// </summary>
        /// <param name="commLink">ICommunicator to record</param>
        /// <param name="dumpToFile">True if packets for this peer should be written to individual file</param>
        /// <param name="dumpFileExtension">Extension for the individual dump file</param>
        public bool AddPeer(ICommunicator commLink, bool dumpToFile, string dumpFileExtension = ".dump")
        {
            if (CurrentState == State.Recording)
                return false;

            if (!socketListeners.ContainsKey(commLink.ID))
            {
                commLink.DataReadySyncEvent += DataReadyEventCallback;
                commLink.DataRateEvent += OnDataRate;

                RecPeerInfo p = new RecPeerInfo()
                {
                    ID = commLink.ID,
                    DumpToFile = dumpToFile,
                    DumpFileExtension = dumpFileExtension,
                    commsLink = commLink,
                    IP = commLink.CommsUri.IP,
                    Port = commLink.CommsUri.LocalPort
                };

                bool doneRight = socketListeners.TryAdd(commLink.ID, p) && _dataRate.TryAdd(commLink.ID, 0);
                return doneRight;
            }

            return false;
        }

        /// <summary>
        /// Removes a previously added ICommunicator
        /// </summary>
        /// <param name="commLink">ICommunicator to remove</param>
        /// <returns>True is removed, false if not</returns>
        public bool RemovePeer(ICommunicator commLink)
        {
            if (CurrentState == State.Recording)
                return false;

            try
            {
                commLink.DataReadySyncEvent -= DataReadyEventCallback;
                commLink.DataRateEvent -= OnDataRate;

                bool doneRight = _dataRate.Remove(commLink.ID) & socketListeners.Remove(commLink.ID);
                return doneRight;
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Removes all peers
        /// </summary>
        /// <returns>True if removal success</returns>
        public bool RemoveAllPeers()
        {
            if (CurrentState == State.Recording)
                return false;

            bool result = true;
            List<RecPeerInfo> communicators = new List<RecPeerInfo>(socketListeners.Values);

            foreach (var communicator in communicators)
                result &= RemovePeer(communicator.commsLink);

            return result;
        }

        private void OnDataRate(string ID, float MbpsRX, float MbpsTX)
        {
            _dataRate[ID] = MbpsRX;
        }


        // Time is microseconds
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void DataReadyEventCallback(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint)
        {
            // Do not follow on if not recording
            if (CurrentState != State.Recording)
                return;

            // Just started recording. Need to reset time
            if (timeOffset == long.MinValue)
                timeOffset = time;

            long correctedTime = time - timeOffset;

            // Fill header and save to file
            WriteHeader(correctedTime, ID, data.Length, ipuint, port);
            // Save payload to file
            WritePayload(data);

            // Dump if requested
            if (socketListeners[ID].DumpToFile && socketListeners[ID].file != null)
                socketListeners[ID].file.Write(data.Span);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void WriteIdx()
        {
            // Write index
            Span<byte> idxSpan = stackalloc byte[HelperTools.idxIndexSize];

            BinaryPrimitives.WriteInt32LittleEndian(idxSpan, secTime);
            BinaryPrimitives.WriteInt64LittleEndian(idxSpan.Slice(4), file.Position);

            idxFile.Write(idxSpan);
        }

        private void WritePayload(ReadOnlyMemory<byte> payload)
        {
            file.Write(payload.Span);
        }

        private void WriteHeader(long _time, string _ID, int _size, uint _ip, int _port)
        {
            Span<byte> headerSpan = stackalloc byte[HelperTools.headerSize];

            // Time 8
            BinaryPrimitives.WriteInt64LittleEndian(headerSpan, _time);
            // Hashed ID 4
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.Slice(8), HelperTools.GetDeterministicHashCode(_ID));
            // IP 4
            BinaryPrimitives.WriteUInt32LittleEndian(headerSpan.Slice(12), _ip);
            // Port 4
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.Slice(16), _port);
            // Size 4
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.Slice(20), _size);

            // Write
            file.Write(headerSpan);
        }

        /// <summary>
        /// Sets the folder where recording will be stored
        /// </summary>
        /// <param name="_folder">Folder where recording is saved</param>
        /// <param name="_name">Name of the recording</param>
        public void SetRecordingFolder(string _folder, string _name)
        {
            if (CurrentState == State.Recording)
                return;

            if (!Directory.Exists(_folder))
                Directory.CreateDirectory(_folder);

            name = _name;
            folder = _folder;
            filePath = Path.Combine(_folder , $"{_name}.raw");
            idxFilePath = Path.Combine(_folder, $"{_name}.idx");
        }

        /// <summary>
        /// Starts the recording
        /// </summary>
        /// <param name="_secsIdxInterval">How often is the index file populated. Default 1s</param>
        public void Start(int _secsIdxInterval = 1)
        {
            secsIdxInterval = _secsIdxInterval;
            timeOffset = long.MinValue;
            secTime = 0;

            // Open file
            file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            idxFile = new FileStream(idxFilePath, FileMode.Create, FileAccess.Write,FileShare.None);

            // Open file per Peer with Dump2File
            foreach(KeyValuePair<string, RecPeerInfo> kv in socketListeners)
            {
                if (kv.Value.DumpToFile)
                    kv.Value.file = new FileStream(Path.Combine(folder, $"{name}_{kv.Key}{kv.Value.DumpFileExtension}"),FileMode.Create, FileAccess.Write, FileShare.None);
            }

            // Fill IDX File header
            FillIDXHeader(idxFile);

            // Start Timer
            eventTimer = new Timer(OnEventTimer, null, 0, 1000);

            foreach (var u in socketListeners.Values)
                u.commsLink.Start();

            CurrentState = State.Recording;
            StatusEvent?.Invoke(CurrentState);
        }

        /// <summary>
        /// Stops the recording. Can be awaited
        /// </summary>
        /// <returns></returns>
        public async Task Stop()
        {
            List<Task> tasks = new List<Task>();
            foreach (var u in socketListeners.Values)
                tasks.Add(u.commsLink.Stop());

            await Task.WhenAll(tasks);

            eventTimer.Dispose();
            eventTimer = null;

            foreach (KeyValuePair<string, RecPeerInfo> kv in socketListeners)
            {
                if (kv.Value.DumpToFile)
                    kv.Value.file.Dispose();
            }

            file.Close();
            file.Dispose();
            idxFile.Close();
            idxFile.Dispose();

            CurrentState = State.Stoped;
            StatusEvent?.Invoke(CurrentState);
        }

        private void FillIDXHeader(FileStream _idxFile)
        {
            Span<byte> idxHeader = stackalloc byte[4096];

            // Time of recording
            BinaryPrimitives.WriteInt64LittleEndian(idxHeader, HelperTools.millisFromEpochNow());
            // Interval
            BinaryPrimitives.WriteInt32LittleEndian(idxHeader.Slice(8), secsIdxInterval);
            // Number of peers
            BinaryPrimitives.WriteInt32LittleEndian(idxHeader.Slice(12), socketListeners.Count);
            // Peer info
            int nBytes = 16;
            foreach (RecPeerInfo p in socketListeners.Values)
            {
                nBytes += HelperTools.StringWithLength2Bytes(idxHeader.Slice(nBytes), p.ID);
                nBytes += HelperTools.StringWithLength2Bytes(idxHeader.Slice(nBytes), p.IP);
                BinaryPrimitives.WriteInt32LittleEndian(idxHeader.Slice(nBytes), p.Port); nBytes += 4;
            }

            idxFile.Write(idxHeader.Slice(0, nBytes));
        }

        public void Dispose()
        {
        }
    }
}
