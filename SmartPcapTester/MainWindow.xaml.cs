using CommsLIBPlus.Helper;
using CommsLIBPlus.SmartPcap;
using CommsLIBPlus.SmartPcap.Base;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace SmartPcapTester
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region MVVM
        public event PropertyChangedEventHandler PropertyChanged;
        public void NotifyPropertyChanged([CallerMemberName] String propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public bool SetProperty<T>(ref T storage, T value, [CallerMemberName]string propertyName = null)
        {
            if (Equals(storage, value))
                return false;
            storage = value;
            NotifyPropertyChanged(propertyName);
            return true;
        }
        #endregion

        NetRecorder recorder;
        NetPlayer player;

        bool sliderUpdating = false;
        double tempSliderValue = 0;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;


            Console.WriteLine("holla");

            recorder = new NetRecorder();
            recorder.StatusEvent += OnRecorderStatus;
            recorder.ProgressEvent += OnRecorderProgress;
            recorder.DataRateEvent += OnRecorderDataRate;
            player = new NetPlayer();
            player.StatusEvent += OnPlayerStatus;
            player.ProgressEvent += OnPlayerProgress;
            player.DataRateEvent += OnPlayerDataRate;

            FilePath = "";
            ModeRec = false;
            ModePlay = true;
        }

        private void OnPlayerDataRate(Dictionary<string, float> dataRates)
        {
            
        }

        private void OnRecorderDataRate(Dictionary<string, float> dataRates)
        {
            
        }

        private void OnRecorderProgress(float mb, int secs)
        {
            RecMb = mb;

        }

        private void OnRecorderStatus(NetRecorder.State state)
        {
            RecStatus = state;
        }

        private void OnPlayerProgress(int time)
        {
            Position = time;
        }

        private void OnPlayerStatus(NetPlayer.State state)
        {
            PlayerStatus = state;
            if (state == NetPlayer.State.Paused)
                Application.Current.Dispatcher.Invoke(() => B_Pause.Content = "RESUME");
            else if (state == NetPlayer.State.Playing)
                Application.Current.Dispatcher.Invoke(() => B_Pause.Content = "PAUSE");
        }

        private bool _modeRec;
        public bool ModeRec
        {
            get { return _modeRec; }
            set { SetProperty(ref _modeRec, value); }
        }

        private bool _modePlay;
        public bool ModePlay
        {
            get { return _modePlay; }
            set { SetProperty(ref _modePlay, value); }
        }

        private string _filePath;
        public string FilePath
        {
            get { return _filePath; }
            set { SetProperty(ref _filePath, value); }
        }

        private NetPlayer.State _playerStatus;
        public NetPlayer.State PlayerStatus
        {
            get { return _playerStatus; }
            set { SetProperty(ref _playerStatus, value); }
        }

        private NetRecorder.State _recStatus;
        public NetRecorder.State RecStatus
        {
            get { return _recStatus; }
            set { SetProperty(ref _recStatus, value); }
        }

        private int _position;
        public int Position
        {
            get { return _position; }
            set
            {
                if (!sliderUpdating)
                    SetProperty(ref _position, value);
            }
        }

        private int _maximum;
        public int Maximum
        {
            get { return _maximum; }
            set { SetProperty(ref _maximum, value); }
        }

        private float _recMb;
        public float RecMb
        {
            get { return _recMb; }
            set { SetProperty(ref _recMb, value); }
        }

        private void ButtonStart(object sender, RoutedEventArgs e)
        {
            if (ModeRec)
            {
                // Several Uris
                var filename = Tools.TimeTools.dateAsString4FileName(DateTime.Now);
                var folder = Path.Combine(/*AppDomain.CurrentDomain.BaseDirectory*/@"c:\temp", filename);
                FilePath = Path.Combine(folder, $"{filename}.raw");
                recorder.SetRecordingFolder(folder,filename);

                recorder.AddPeer("SRC1", "239.10.10.10", 21000, true);

                recorder.Start(1);
            }
            else if (ModePlay)
            {
                player.Play(false);
            }
        }

        private void ButtonStop(object sender, RoutedEventArgs e)
        {
            if (ModeRec)
            {
                recorder.Stop();
            }
            else if (ModePlay)
            {
                player.Stop();
            }
        }
        private void ButtonPause(object sender, RoutedEventArgs e)
        {
            if (ModePlay)
            {
                if (player.CurrentState == NetPlayer.State.Playing)
                    player.Pause();
                else if (player.CurrentState == NetPlayer.State.Paused)
                    player.Resume();
            }
        }


        private void ButtonRefresh(object sender, RoutedEventArgs e)
        {
            //if (ModeRec)
            //{
            //    recorder.SetRecordingFolder(AppDomain.CurrentDomain.BaseDirectory, Tools.TimeTools.dateAsString4FileName(DateTime.Now));
            //}
            //else if (ModePlay)
            {
                ICollection<PlayPeerInfo> peers = player.LoadFile(FilePath, out DateTime startRecDateTime, out int maxTime);
                Maximum = maxTime;
                
            }
        }


        private void Slider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            sliderUpdating = true;
        }

        private void S_Position_MouseUp(object sender, MouseButtonEventArgs e)
        {

        }

        private void S_Position_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            sliderUpdating = true;
        }

        private void S_Position_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sliderUpdating)
            {
                tempSliderValue = S_Position.Value;

            }
        }

        private void S_Position_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            player.Seek((int)tempSliderValue);
            sliderUpdating = false;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            player.Dispose();
            recorder.Dispose();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog()
            {
                Filter = "Raw recording (*.raw)|*.raw",
                InitialDirectory = @"c:\temp\"
            };

            if (openFileDialog.ShowDialog() == true)
                FilePath = openFileDialog.FileName;
        }
    }
}
