using System;
using System.IO;
using FFmpeg.AutoGen.Abstractions;
using Godot;

namespace FFmpeg.Godot
{
    [GlobalClass]
    public partial class FFPlayGodot : Node
    {
        public FFTimings videoTimings;
        public FFTimings audioTimings;

        private GodotThread thread;

        public event Action OnEndReached;
        public event Action OnVideoEndReached;
        public event Action OnAudioEndReached;
        public event Action OnError;

        [Export]
        public double videoOffset = 0.5d;
        [Export]
        public double audioOffset = 0d;

        [Export]
        public FFTexturePlayer texturePlayer;
        [Export]
        public FFAudioPlayer audioPlayer;

        private double timeOffset = 0d;
        private double pauseTime = 0d;

        public bool IsPlaying { get; private set; } = false;
        public bool IsStream { get; private set; } = false;

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
            IsPlaying = false;
            StopThread();
            OnDestroy();
            videoTimings = new FFTimings(streamV, AVMediaType.AVMEDIA_TYPE_VIDEO);
            audioTimings = new FFTimings(streamA, AVMediaType.AVMEDIA_TYPE_AUDIO);
            Init();
        }

        public void Play(string urlV, string urlA)
        {
            IsPlaying = false;
            StopThread();
            OnDestroy();
            videoTimings = new FFTimings(urlV, AVMediaType.AVMEDIA_TYPE_VIDEO);
            audioTimings = new FFTimings(urlA, AVMediaType.AVMEDIA_TYPE_AUDIO);
            Init();
        }

        private void Init()
        {
            if (audioTimings.IsInputValid)
                audioPlayer.Init(audioTimings.decoder.SampleRate, audioTimings.decoder.Channels, audioTimings.decoder.SampleFormat);
            if (videoTimings.IsInputValid)
            {
                timeOffset = timeAsDouble;
                videoOffset = videoTimings.StartTime;
                IsStream = Mathf.Abs(videoTimings.StartTime) > 5d;
            }
            // else
                timeOffset = timeAsDouble;
            if (audioTimings.IsInputValid)
            {
                audioOffset = audioTimings.StartTime;
            }
            if (!videoTimings.IsInputValid && !audioTimings.IsInputValid)
            {
                IsPaused = true;
                StopThread();
                GD.PrintErr("AV not found");
                IsPlaying = false;
                OnError?.Invoke();
            }
            else
            {
                audioPlayer.Resume();
                RunThread();
                IsPlaying = true;
            }
        }

        public void Seek(double timestamp)
        {
            if (IsStream)
                return;
            StopThread();
            timeOffset = timeAsDouble - timestamp;
            pauseTime = timestamp;
            if (videoTimings != null)
            {
                videoTimings.Seek(VideoTime);
            }
            if (audioTimings != null)
            {
                audioTimings.Seek(AudioTime);
                audioTimings.GetFrames();
                audioPlayer.Seek();
            }
            RunThread();
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
            if (IsPaused)
                return;
            pauseTime = PlaybackTime;
            audioPlayer.Pause();
            IsPaused = true;
            StopThread();
            IsPlaying = false;
        }

        public void Resume()
        {
            if (!IsPaused)
                return;
            StopThread();
            timeOffset = timeAsDouble - pauseTime;
            audioPlayer.Resume();
            IsPaused = false;
            RunThread();
            IsPlaying = true;
        }

        private void Update()
        {
            if (!IsPaused)
            {
                if (!thread.IsAlive() && IsPlaying)
                {
                    // StopThread();
                    // RunThread();
                }
                if (videoTimings != null)
                {
                    if (videoTimings.IsEndOfFile())
                    {
                        Pause();
                        OnVideoEndReached?.Invoke();
                        OnEndReached?.Invoke();
                    }
                }
                if (audioTimings != null)
                {
                    if (audioTimings.IsEndOfFile())
                    {
                        Pause();
                        OnAudioEndReached?.Invoke();
                        OnEndReached?.Invoke();
                    }
                }
            }
        }

        private void ThreadUpdate()
        {
            GD.Print("ThreadUpdate Start");
            while (!IsPaused)
            {
                OS.DelayMsec(3);
                try
                {
                    if (videoTimings != null)
                    {
                        videoTimings.Update(VideoTime);
                        texturePlayer.PlayPacket(videoTimings.GetFrame());
                    }
                    if (audioTimings != null)
                    {
                        audioTimings.Update(AudioTime);
                        audioPlayer.PlayPackets(audioTimings.GetFrames());
                    }
                }
                catch (Exception e)
                {
                    GD.PushError(e);
                    break;
                }
            }
            GD.Print("ThreadUpdate Done");
        }

        private void OnDestroy()
        {
            videoTimings?.Dispose();
            videoTimings = null;
            audioTimings?.Dispose();
            audioTimings = null;
        }

        private void RunThread()
        {
            if (thread.IsAlive())
                throw new Exception();
            // if (thread.IsAlive() && thread.IsStarted())
                // StopThread();
            IsPaused = false;
            thread.Start(Callable.From(ThreadUpdate));
        }

        private void StopThread()
        {
            if (!thread.IsStarted())
                return;
            bool paused = IsPaused;
            IsPaused = true;
            thread.WaitToFinish();
            IsPaused = paused;
        }

        public override void _Process(double delta)
        {
            Update();
        }

        public override void _EnterTree()
        {
            thread = new GodotThread();
        }

        public override void _ExitTree()
        {
            IsPaused = true;
            StopThread();
            OnDestroy();
        }
    }
}