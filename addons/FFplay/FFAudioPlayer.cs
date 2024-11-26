using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
            audioSource.Stream = clip;
        }

        public void Pause()
        {
            audioSource.StreamPaused = true;
        }

        public void Resume()
        {
            audioSource.StreamPaused = false;
        }
        
        public void Seek()
        {
            audioSource.Stop();
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
                break;
            }
            // source.AddQueue(pcm.ToArray(), 1, clip.frequency);
            if (!audioSource.Playing)
                audioSource.Play();
            if (audioSource.Playing && audioSource.GetStreamPlayback() is AudioStreamGeneratorPlayback playback && playback.CanPushBuffer(1))
            {
                Vector2[] pcm2 = new Vector2[pcm.Count];
                for (int i = 0; i < pcm.Count; i++)
                {
                    float ch1 = pcm[i];
                    pcm2[i] = new Vector2(ch1, ch1);
                }
                playback.PushBuffer(pcm2);
            }
        }
    }
}