using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.Godot.Helpers;
using Godot;
using Mutex = System.Threading.Mutex;

namespace FFmpeg.Godot
{
    [GlobalClass]
    public partial class FFGodot : Node
    {
        public bool IsPaused => _paused;

        public struct TexData
        {
            public double time;
            public byte[] data;
            public int w;
            public int h;
        }

        [Export]
        public TextureRect renderMesh;
        [Export]
        public AudioStreamPlayer3D source;

        [Export]
        public bool _paused = false;
        private bool _wasPaused = false;
        [Export]
        public bool CanSeek = true;

        // time controls
        [Export]
        public double _offset = 0.0d;
        private double _prevTime = 0.0d;
        private double _timeOffset = 0.0d;
        [Export]
        public double _videoOffset = -1d;
        private Stopwatch _videoWatch;
        private double? _lastPts;
        private int? _lastPts2;
        public double timer;
        public double PlaybackTime => _lastVideoTex?.pts ?? _elapsedOffset;
        public double _elapsedTotalSeconds => _videoWatch?.Elapsed.TotalSeconds ?? 0d;
        public double _elapsedOffsetVideo => _elapsedTotalSeconds + _videoOffset - _timeOffset;
        public double _elapsedOffset => _elapsedTotalSeconds - _timeOffset;

        // buffer controls
        private int _videoBufferCount = 1024;
        private int _audioBufferCount = 1;
        [Export]
        public double _videoTimeBuffer = 1d;
        [Export]
        public double _videoSkipBuffer = 0.1d;
        [Export]
        public double _audioTimeBuffer = 1d;
        [Export]
        public double _audioSkipBuffer = 0.1d;
        private int _audioBufferSize = 1024;

        // godot assets
        private Queue<TexturePool.TexturePoolState> _videoTextures;
        private TexturePool.TexturePoolState _lastVideoTex;
        private TexturePool _texturePool;
        private TexData? _lastTexData;
        private AudioStreamGenerator _audioClip;
        private AudioStreamGeneratorPlayback _audioPlayback;
        private string propertyBlock;
        public Action<Image> OnDisplay = null;

        // decoders
        [Export]
        public AVHWDeviceType _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private FFmpegCtx _streamVideoCtx;
        private FFmpegCtx _streamAudioCtx;
        private VideoStreamDecoder _videoDecoder;
        private VideoStreamDecoder _audioDecoder;
        private VideoFrameConverter _videoConverter;
        private Queue<TexData> _videoFrameClones;
        private Mutex _videoMutex = new Mutex();
        private Thread _decodeThread;
        private Mutex _audioLocker = new Mutex();
        private Queue<float> _audioStream;
        private MemoryStream _audioMemStream;

        // buffers
        private AVFrame[] _videoFrames;
        private AVFrame[] _audioFrames;
        private int _videoDisplayIndex = 0;
        private int _audioDisplayIndex = 0;
        private int _videoWriteIndex = 0;
        private int _audioWriteIndex = 0;

        // logging
        public Action<object> Log = o => GD.Print(o);
        public Action<object> LogWarning = o => GD.PushWarning(o);
        public Action<object> LogError = o => GD.PushError(o);

        public override void _EnterTree()
        {
            OnEnable();
        }

        public override void _ExitTree()
        {
            OnDisable();
        }

        public override void _Process(double delta)
        {
            Update();
        }

        private void OnEnable()
        {
            _paused = true;
        }

        private void OnDisable()
        {
            _paused = true;
        }

        private void OnDestroy()
        {
            _paused = true;
            _decodeThread?.Join();
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _streamVideoCtx?.Dispose();
            _streamAudioCtx?.Dispose();
        }

        public void Seek(double seek)
        {
            Log(nameof(Seek));
            _paused = true;
            _decodeThread?.Join();
            source.Stop();
            {
                _audioMemStream.Position = 0;
                _audioStream.Clear();
            }
            {
                _videoFrameClones.Clear();
                foreach (var tex in _videoTextures)
                {
                    _texturePool.Release(tex);
                }
                _videoTextures.Clear();
                _lastVideoTex = null;
                _lastTexData = null;
            }
            _videoWatch.Restart();
            ResetTimers();
            _timeOffset = -seek;
            _prevTime = _offset;
            _lastPts = null;
            _lastPts2 = null;
            if (CanSeek)
            {
                _streamVideoCtx.Seek(_videoDecoder, seek);
                _streamAudioCtx.Seek(_audioDecoder, seek);
            }
            _videoDecoder.Seek();
            _audioDecoder.Seek();
            source.Stream = _audioClip;
            source.Play();
            CallDeferred(nameof(SetAudioPlayback));
            // _audioPlayback = (AudioStreamGeneratorPlayback)source.GetStreamPlayback();
            _paused = false;
            StartDecodeThread();
        }

        public void Play(Stream video, Stream audio)
        {
            DynamicallyLinkedBindings.Initialize();
            _paused = true;
            _decodeThread?.Join();
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _streamVideoCtx?.Dispose();
            _streamAudioCtx?.Dispose();
            _streamVideoCtx = new FFmpegCtx(video);
            _streamAudioCtx = new FFmpegCtx(audio);
            Init();
        }

        public void Play(string urlV, string urlA)
        {
            DynamicallyLinkedBindings.Initialize();
            _paused = true;
            _decodeThread?.Join();
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _streamVideoCtx?.Dispose();
            _streamAudioCtx?.Dispose();
            _streamVideoCtx = new FFmpegCtx(urlV);
            _streamAudioCtx = new FFmpegCtx(urlA);
            Init();
        }

        public void Resume()
        {
            if (!CanSeek)
                Init();
            _paused = false;
        }

        public void Pause()
        {
            _paused = true;
        }

        private void ResetTimers()
        {
            // reset index counters and timers
            _videoDisplayIndex = 0;
            _audioDisplayIndex = 0;
            _videoWriteIndex = 0;
            _audioWriteIndex = 0;
            _lastPts = null;
            _lastPts2 = null;
            _offset = 0d;
            _prevTime = 0d;
            _timeOffset = 0d;
            timer = 0d;
        }

        private void SetAudioPlayback()
        {
            _audioPlayback = (AudioStreamGeneratorPlayback)source.GetStreamPlayback();
        }

        private void Init()
        {
            _paused = true;

            // Stopwatches are more accurate than Time.timeAsDouble(?)
            _videoWatch = new Stopwatch();

            // pre-allocate buffers, prevent the C# GC from using CPU
            _texturePool = new TexturePool(_videoBufferCount);
            _videoTextures = new Queue<TexturePool.TexturePoolState>(_videoBufferCount);
            _audioClip = null; // don't create audio clip yet, we have nothing to play.
            _videoFrames = new AVFrame[_videoBufferCount];
            _videoFrameClones = new Queue<TexData>(_videoBufferCount);
            _audioFrames = new AVFrame[_audioBufferCount];
            _audioMemStream = new MemoryStream();
            _audioStream = new Queue<float>(_audioBufferSize * 4);

            ResetTimers();
            _lastVideoTex = null;
            _lastTexData = null;

            // init decoders
            _videoMutex = new Mutex(false, "Video Mutex");
            _videoDecoder = new VideoStreamDecoder(_streamVideoCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, _hwType);
            _audioLocker = new Mutex(false, "Audio Mutex");
            _audioDecoder = new VideoStreamDecoder(_streamAudioCtx, AVMediaType.AVMEDIA_TYPE_AUDIO);
            // Seek(0d);
            _paused = false;
            _audioClip = new AudioStreamGenerator()
            {
                MixRate = _audioDecoder.SampleRate,
                BufferLength = 0.1f,
                // BufferLength = 0.5f,
            };
            source.Stream = _audioClip;
            source.Play();
            CallDeferred(nameof(SetAudioPlayback));
            Log(nameof(Play));
            Seek(0d);
        }

        private void Update()
        {
            if (_videoWatch == null)
                return;
            
            if (_streamVideoCtx.EndReached && _streamAudioCtx.EndReached && _videoTextures.Count == 0 && _audioStream.Count == 0 && !_paused)
            {
                Pause();
            }

            if (!_paused)
            {
                _offset = _elapsedOffset;
                if (!_videoWatch.IsRunning)
                {
                    _videoWatch.Start();
                    source.StreamPaused = false;
                }
            }
            else
            {
                if (_videoWatch.IsRunning)
                {
                    _videoWatch.Stop();
                    source.StreamPaused = true;
                }
            }

            if (!_paused)
            {
                if (_decodeThread == null || !_decodeThread.IsAlive)
                    StartDecodeThread();

                AudioCallback();

                int idx = _videoDisplayIndex;
                if (_videoMutex.WaitOne(1))
                {
                    UpdateVideoFromClones(idx);
                    _videoMutex.ReleaseMutex();
                }
                if (_streamVideoCtx.TryGetFps(_videoDecoder, out double fps))
                {
                    while (_elapsedOffset - timer >= 0d)
                    {
                        timer += 1d / fps;
                        Present(idx);
                        // AudioCallback();
                    }
                    int k = 0;
                    if (CanSeek && _elapsedOffsetVideo > PlaybackTime + _videoSkipBuffer && k < fps)
                    {
                        k++;
                        Present(idx);
                        // AudioCallback();
                    }
                }
            }

            _prevTime = _offset;
            _wasPaused = _paused;
        }

        private void Update_Thread()
        {
            Log("AV Thread started.");
            while (!_paused)
            {
                try
                {
                    FillVideoBuffers(false);
                    Thread.Sleep(1);
                    Thread.Yield();
                }
                catch (Exception e)
                {
                    LogError(e);
                }
            }
            Log("AV Thread stopped.");
            _videoWatch.Stop();
            _paused = true;
        }

        private void StartDecodeThread()
        {
            _decodeThread = new Thread(() => Update_Thread());
            _decodeThread.Name = $"AV Decode Thread {Name}";
            _decodeThread.Start();
        }

        #region Callbacks
        private void Present(int idx)
        {
            if (_videoTextures.Count == 0)
            {
                return;
            }
            if (_streamVideoCtx.TryGetPts(_videoDecoder, out double pts))
                _lastPts2 = (int)_elapsedOffset;
            var tex = _videoTextures.Dequeue();
            if (tex != _lastVideoTex)
                _texturePool.Release(_lastVideoTex);
            _lastVideoTex = tex;
            if (OnDisplay == null)
            {
                if (IsInstanceValid(renderMesh))
                {
                    renderMesh.Texture = ImageTexture.CreateFromImage(_lastVideoTex.texture);
                }
            }
            else
            {
                OnDisplay.Invoke(tex.texture);
            }
        }

        private bool _lastAudioRead = false;
        private int _audioMissCount = 0;

        private unsafe void AudioCallback()
        {
            if (!IsInstanceValid(_audioPlayback))
                return;
            if (_audioLocker.WaitOne(0))
            {
                Vector2[] buffer = new Vector2[_audioPlayback.GetFramesAvailable()];
                Array.Fill(buffer, Vector2.Zero);
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (_audioStream.Count <= _audioDecoder.Channels)
                    {
                        continue;
                    }
                    float final = 0f;
                    for (int j = 0; j < _audioDecoder.Channels; j++)
                    {
                        float val = _audioStream.Dequeue();
                        final += val;
                    }
                    buffer[i] = Vector2.One * final / _audioDecoder.Channels;
                }
                _audioPlayback.PushBuffer(buffer);
                _audioLocker.ReleaseMutex();
            }
        }
        #endregion

        #region Buffer Handling
        private double _lastVideoDecodeTime;
        private double _lastAudioDecodeTime;
        [NonSerialized]
        public int skippedFrames = 0;

        private void FillVideoBuffers(bool mainThread)
        {
            if (_streamVideoCtx == null || _streamAudioCtx == null)
                return;
            bool found = false;
            int k = 0;
            Stopwatch sw = new Stopwatch();
            sw.Restart();
            if (!_streamVideoCtx.TryGetFps(_videoDecoder, out var fps))
                return;
            fps *= 2;
            // fps = 120;
            while (k < fps && sw.ElapsedMilliseconds <= 16)
            {
                double timeBuffer = 0.5d;
                double timeBufferSkip = 0.15d;
                double pts = default;
                double time = default;

                int breaks = 0;
                bool decodeV = true;
                bool decodeA = true;
                if (_lastVideoTex != null)
                {
                    if (Math.Abs(_elapsedOffsetVideo - PlaybackTime) > _videoTimeBuffer * 5 && !CanSeek)
                    {
                        _timeOffset = -PlaybackTime;
                    }
                }
                if (_lastVideoTex != null && _videoDecoder.CanDecode() && _streamVideoCtx.TryGetTime(_videoDecoder, out time))
                {
                    if (_elapsedOffsetVideo + _videoTimeBuffer < time)
                        decodeV = false;
                    if (_elapsedOffsetVideo > time + _videoSkipBuffer && CanSeek)
                    {
                        _streamVideoCtx.NextFrame(out _);
                        skippedFrames++;
                        decodeV = false;
                    }
                }
                if (_lastVideoTex != null && _audioDecoder.CanDecode() && _streamAudioCtx.TryGetTime(_audioDecoder, out time))
                {
                    if (_elapsedOffset + _audioTimeBuffer < time)
                        decodeA = false;
                    if (_elapsedOffset > time + _audioSkipBuffer && CanSeek)
                    {
                        _streamAudioCtx.NextFrame(out _);
                        skippedFrames++;
                        decodeA = false;
                    }
                }
                if (breaks >= 1)
                {
                    break;
                }
                if (true)
                {
                    int vid = -1;
                    int aud = -1;
                    AVFrame vFrame = default;
                    AVFrame aFrame = default;
                    if (decodeV)
                    {
                        _streamVideoCtx.NextFrame(out _);
                        vid = _videoDecoder.Decode(out vFrame);
                    }
                    if (decodeA)
                    {
                        _streamAudioCtx.NextFrame(out _);
                        aud = _audioDecoder.Decode(out aFrame);
                    }
                    found = false;
                    switch (vid)
                    {
                        case 0:
                            if (_streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time) && _elapsedOffsetVideo > time + _videoSkipBuffer && CanSeek)
                                break;
                            if (_streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time) && time != 0)
                                _lastVideoDecodeTime = time;
                            _videoFrames[_videoWriteIndex % _videoFrames.Length] = vFrame;
                            if (mainThread)
                            {
                                UpdateVideo(_videoWriteIndex % _videoFrames.Length);
                            }
                            else
                            {
                                {
                                    if (_videoMutex.WaitOne(1))
                                    {
                                        byte[] frameClone = new byte[vFrame.width * vFrame.height * 3];
                                        if (!SaveFrame(vFrame, vFrame.width, vFrame.height, frameClone, _videoDecoder.HWPixelFormat))
                                        {
                                            LogError("Could not save frame");
                                            _videoWriteIndex--;
                                        }
                                        else
                                        {
                                            _streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time);
                                            _lastPts = time;
                                            _videoFrameClones.Enqueue(new TexData()
                                            {
                                                time = time,
                                                data = frameClone,
                                                w = vFrame.width,
                                                h = vFrame.height,
                                            });
                                        }
                                        _videoMutex.ReleaseMutex();
                                    }
                                }
                            }
                            _videoWriteIndex++;
                            found = false;
                            break;
                        case 1:
                            found = true;
                            break;
                    }
                    switch (aud)
                    {
                        case 0:
                            if (_streamAudioCtx.TryGetTime(_audioDecoder, aFrame, out time) && _elapsedOffset > time + _audioSkipBuffer && CanSeek)
                                break;
                            if (_streamAudioCtx.TryGetTime(_audioDecoder, aFrame, out time) && time != 0)
                                _lastAudioDecodeTime = time;
                            _audioFrames[_audioWriteIndex % _audioFrames.Length] = aFrame;
                            UpdateAudio(_audioWriteIndex % _audioFrames.Length);
                            _audioWriteIndex++;
                            found = false;
                            break;
                        case 1:
                            found = true;
                            break;
                    }
                }
                else
                    break;
            }
            if (k >= fps)
                LogWarning("Max while true reached!");
        }
        #endregion

        #region Frame Handling
        private unsafe Image UpdateVideo(int idx)
        {
            // Profiler.BeginSample(nameof(UpdateVideo), this);
            AVFrame videoFrame;
            videoFrame = _videoFrames[idx];
            if (videoFrame.data[0] == null)
            {
                // Profiler.EndSample();
                return null;
            }
            var tex = _texturePool.Get();
            if (tex.texture == null)
            {
                tex.texture = SaveFrame(videoFrame, videoFrame.width, videoFrame.height, _videoDecoder.HWPixelFormat);
            }
            else
                SaveFrame(videoFrame, videoFrame.width, videoFrame.height, tex.texture, _videoDecoder.HWPixelFormat);
            tex.texture.ResourceName = $"{Name}-Texture2D-{idx}";
            _videoTextures.Enqueue(tex);
            // Profiler.EndSample();
            return tex.texture;
        }

        private unsafe void UpdateVideoFromClones(int idx)
        {
            // Profiler.BeginSample(nameof(UpdateVideoFromClones), this);
            if (_videoFrameClones.Count == 0)
            {
                // Profiler.EndSample();
                return;
            }
            TexData videoFrame = _videoFrameClones.Peek();
            _videoFrameClones.Dequeue();
            var tex = _texturePool.Get();
            if (tex.texture != null)
            {
                if (tex.texture.GetWidth() != videoFrame.w || tex.texture.GetHeight() != videoFrame.h)
                    tex.texture.Resize(videoFrame.w, videoFrame.h);
                tex.texture.SetData(videoFrame.w, videoFrame.h, false, Image.Format.Rgb8, videoFrame.data);
            }
            tex.pts = videoFrame.time;
            _lastTexData = videoFrame;
            _videoTextures.Enqueue(tex);
            // Profiler.EndSample();
        }

        private unsafe void UpdateAudio(int idx)
        {
            // Profiler.BeginSample(nameof(UpdateAudio), this);
            var audioFrame = _audioFrames[idx];
            if (audioFrame.data[0] == null)
            {
                // Profiler.EndSample();
                return;
            }
            List<float> vals = new List<float>();
            for (uint ch = 0; ch < _audioDecoder.Channels; ch++)
            {
                int size = ffmpeg.av_samples_get_buffer_size(null, 1, audioFrame.nb_samples, _audioDecoder.SampleFormat, 1);
                if (size < 0)
                {
                    LogError("audio buffer size is less than zero");
                    // Profiler.EndSample();
                    return;
                }
                byte[] backBuffer2 = new byte[size];
                float[] backBuffer3 = new float[size / sizeof(float)];
                Marshal.Copy((IntPtr)audioFrame.data[ch], backBuffer2, 0, size);
                Buffer.BlockCopy(backBuffer2, 0, backBuffer3, 0, backBuffer2.Length);
                {
                    for (int i = 0; i < backBuffer3.Length; i++)
                    {
                        vals.Add(backBuffer3[i]);
                    }
                }
            }
            if (_audioLocker.WaitOne(0))
            {
                int c = vals.Count / _audioDecoder.Channels;
                for (int i = 0; i < c; i++)
                {
                    for (int j = 0; j < _audioDecoder.Channels; j++)
                    {
                        _audioStream.Enqueue(vals[i + c * j]);
                    }
                }
                _audioLocker.ReleaseMutex();
            }
            // Profiler.EndSample();
        }
        #endregion

        public static Image SaveFrame(AVFrame frame, int width, int height, AVPixelFormat format)
        {
            Image texture = Image.Create(width, height, false, Image.Format.Rgb8);
            SaveFrame(frame, width, height, texture, format);
            return texture;
        }

        private static byte[] line = new byte[4096 * 4096 * 3];

        public unsafe static bool SaveFrame(AVFrame frame, int width, int height, byte[] texture, AVPixelFormat format)
        {
            if (frame.data[0] == null || frame.format == -1 || texture == null)
            {
                return false;
            }
            using var converter = new VideoFrameConverter(new System.Drawing.Size(frame.width, frame.height), (AVPixelFormat)frame.format, new System.Drawing.Size(width, height), AVPixelFormat.AV_PIX_FMT_RGB24);
            var convFrame = converter.Convert(frame, format == AVPixelFormat.AV_PIX_FMT_NONE ? 32 : 1);
            // var convFrame = converter.Convert(frame, 32);
            Marshal.Copy((IntPtr)convFrame.data[0], line, 0, width * height * 3);
            Array.Copy(line, 0, texture, 0, width * height * 3);
            return true;
        }

        public unsafe static void SaveFrame(AVFrame frame, int width, int height, Image texture, AVPixelFormat format)
        {
            // Profiler.BeginSample(nameof(SaveFrame), texture);
            if (frame.data[0] == null || frame.format == -1)
            {
                // Profiler.EndSample();
                return;
            }
            using var converter = new VideoFrameConverter(new System.Drawing.Size(width, height), (AVPixelFormat)frame.format, new System.Drawing.Size(width, height), AVPixelFormat.AV_PIX_FMT_RGB24);
            // Profiler.BeginSample(nameof(SaveFrame) + "Convert", texture);
            var convFrame = converter.Convert(frame);
            // Profiler.EndSample();
            // Profiler.BeginSample(nameof(SaveFrame) + "LoadTexture", texture);
            if (texture.GetWidth() != width || texture.GetHeight() != height)
                texture.Resize(width, height);
            Marshal.Copy((IntPtr)convFrame.data[0], line, 0, width * height * 3);
            texture.SetData(width, height, false, Image.Format.Rgb8, line);
            // Profiler.EndSample();
            // Profiler.EndSample();
        }
    }
}