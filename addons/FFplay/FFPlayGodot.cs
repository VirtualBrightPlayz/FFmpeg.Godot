using System;
using System.IO;
using Godot;

namespace FFmpeg.Godot
{
    [GlobalClass]
    public partial class FFPlayGodot : Node
    {
        public FFTimings videoTimings;
        public FFTimings audioTimings;

        public event Action OnEndReached;
        public event Action OnVideoEndReached;
        public event Action OnAudioEndReached;
        public event Action OnError;

        [Export]
        public double videoOffset = 0d;
        [Export]
        public double audioOffset = 0d;

        [Export]
        public FFTexturePlayer texturePlayer;
        [Export]
        public FFAudioPlayer audioPlayer;

        private double timeOffset = 0d;
        private double pauseTime = 0d;

        public bool IsPaused { get; private set; } = false;

        public double timeAsDouble => Time.GetTicksMsec() / 1000d;

        public double PlaybackTime => IsPaused ? pauseTime : timeAsDouble - timeOffset;

        public double VideoTime => timeAsDouble - timeOffset + videoOffset;
        public double AudioTime => timeAsDouble - timeOffset + audioOffset;

        public void Play(string url)
        {
            Play(url, url);
        }

        public void Play(Stream streamV, Stream streamA)
        {
            Resume();
            videoTimings = new FFTimings(streamV, AVMediaType.AVMEDIA_TYPE_VIDEO, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            audioTimings = new FFTimings(streamA, AVMediaType.AVMEDIA_TYPE_AUDIO);
            Init();
        }

        public void Play(string urlV, string urlA)
        {
            Resume();
            videoTimings = new FFTimings(urlV, AVMediaType.AVMEDIA_TYPE_VIDEO, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            audioTimings = new FFTimings(urlA, AVMediaType.AVMEDIA_TYPE_AUDIO);
            Init();
        }

        private void Init()
        {
            if (audioTimings.IsInputValid)
                audioPlayer.Init(audioTimings.decoder.SampleRate, audioTimings.decoder.Channels, audioTimings.decoder.SampleFormat);
            if (videoTimings.IsInputValid)
                timeOffset = timeAsDouble - videoTimings.StartTime;
            else
                timeOffset = timeAsDouble;
            if (!videoTimings.IsInputValid && !audioTimings.IsInputValid)
            {
                GD.PrintErr("AV not found");
                OnError?.Invoke();
            }
        }

        public void Seek(double timestamp)
        {
            timeOffset = timeAsDouble - timestamp;
            pauseTime = timestamp;
            if (videoTimings != null)
            {
                videoTimings.Seek(VideoTime);
            }
            if (audioTimings != null)
            {
                audioTimings.Seek(AudioTime);
                audioPlayer.Seek();
            }
        }

        public double GetLength()
        {
            if (videoTimings != null && videoTimings.IsInputValid)
                return videoTimings.GetLength();
            if (audioTimings != null && audioTimings.IsInputValid)
                return audioTimings.GetLength();
            return 0d;
        }

        public void Pause()
        {
            pauseTime = PlaybackTime;
            audioPlayer.Pause();
            IsPaused = true;
        }

        public void Resume()
        {
            timeOffset = timeAsDouble - pauseTime;
            audioPlayer.Resume();
            IsPaused = false;
        }

        private void Update()
        {
            if (!IsPaused)
            {
                if (videoTimings != null)
                {
                    videoTimings.Update(VideoTime);
                    texturePlayer.PlayPacket(videoTimings.GetCurrentFrame());
                    if (videoTimings.IsEndOfFile())
                    {
                        Pause();
                        OnVideoEndReached?.Invoke();
                        OnEndReached?.Invoke();
                    }
                }
                if (audioTimings != null)
                {
                    audioTimings.Update(AudioTime);
                    audioPlayer.PlayPackets(audioTimings.GetCurrentFrames());
                    if (audioTimings.IsEndOfFile())
                    {
                        Pause();
                        OnAudioEndReached?.Invoke();
                        OnEndReached?.Invoke();
                    }
                }
            }
        }

        private void OnDestroy()
        {
            videoTimings?.Dispose();
            videoTimings = null;
            audioTimings?.Dispose();
            audioTimings = null;
        }

        public override void _Process(double delta)
        {
            Update();
        }

        public override void _ExitTree()
        {
            OnDestroy();
        }
    }
}