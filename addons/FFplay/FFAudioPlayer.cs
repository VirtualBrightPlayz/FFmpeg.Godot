using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Godot;

namespace FFmpeg.Godot
{
    [GlobalClass]
    public partial class FFAudioPlayer : Node
    {
        public long pts;
        [Export]
        public AudioStreamPlayer3D audioSource;
        private AudioStreamGenerator clip;
        private int channels;
        private AVSampleFormat sampleFormat;
        private List<float> pcm = new List<float>();
        private AudioStreamGeneratorPlayback playback;

        public void Init(int frequency, int channels, AVSampleFormat sampleFormat)
        {
            this.channels = channels;
            this.sampleFormat = sampleFormat;
            GD.Print($"Freq={frequency}");
            clip = new AudioStreamGenerator()
            {
                BufferLength = 1f,
                MixRate = frequency,
            };
            if (!IsInstanceValid(audioSource))
                return;
            audioSource.Stream = clip;
            audioSource.Play();
            if (audioSource.GetStreamPlayback() is AudioStreamGeneratorPlayback pb)
            {
                playback = pb;
            }
        }

        public void Pause()
        {
            if (IsInstanceValid(audioSource))
                audioSource.StreamPaused = true;
        }

        public void Resume()
        {
            if (IsInstanceValid(audioSource))
                audioSource.StreamPaused = false;
        }
        
        public void Seek()
        {
            if (!IsInstanceValid(audioSource))
                return;
            if (audioSource.Playing && IsInstanceValid(playback))
            {
                playback.ClearBuffer();
            }
            else
            {
                // audioSource.Stop();
                audioSource.Play();
                if (audioSource.GetStreamPlayback() is AudioStreamGeneratorPlayback pb)
                {
                    playback = pb;
                }
            }
        }

        public void PlayPackets(List<AVFrame> frames)
        {
            if (frames.Count == 0)
            {
                return;
            }
            foreach (var frame in frames)
            {
                QueuePacket(frame);
            }
        }

        private unsafe void QueuePacket(AVFrame frame)
        {
            pcm.Clear();
            pts = frame.pts;
            for (uint ch = 0; ch < channels; ch++)
            {
                int size = ffmpeg.av_samples_get_buffer_size(null, 1, frame.nb_samples, sampleFormat, 1);
                if (size < 0)
                {
                    GD.PrintErr("audio buffer size is less than zero");
                    continue;
                }
                byte[] backBuffer2 = new byte[size];
                float[] backBuffer3 = new float[size / sizeof(float)];
                Marshal.Copy((IntPtr)frame.data[ch], backBuffer2, 0, size);
                Buffer.BlockCopy(backBuffer2, 0, backBuffer3, 0, backBuffer2.Length);
                for (int i = 0; i < backBuffer3.Length; i++)
                {
                    pcm.Add(backBuffer3[i]);
                }
            }
            // source.AddQueue(pcm.ToArray(), 1, clip.frequency);
            if (playback.CanPushBuffer(1))
            {
                Vector2[] pcm2 = new Vector2[pcm.Count / channels];
                for (int i = 0; i < pcm2.Length; i++)
                {
                    float ch1 = pcm[i];
                    for (int j = 1; j < channels; j++)
                    {
                        ch1 += pcm[pcm2.Length * j + i];
                    }
                    ch1 /= channels;
                    pcm2[i] = new Vector2(ch1, ch1);
                }
                playback.PushBuffer(pcm2);
            }
        }
    }
}