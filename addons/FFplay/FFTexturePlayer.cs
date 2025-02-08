using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Godot;

namespace FFmpeg.Godot
{
    [GlobalClass]
    public partial class FFTexturePlayer : Node
    {
        public long pts;
        [Export]
        public MeshInstance3D renderMesh;
        [Export]
        public TextureRect renderRect;
        [Export]
        public int materialIndex = -1;
        public Action<ImageTexture> OnDisplay = null;
        private Image image;
        private ImageTexture texture;
        private int framewidth;
        private int frameheight;
        private byte[] framedata = new byte[0];
        private Mutex mutex = new Mutex();

        public void PlayPacket(AVFrame frame)
        {
            pts = frame.pts;
            byte[] data = new byte[frame.width * frame.height * 3];
            if (SaveFrame(frame, data))
            {
                mutex.Lock();
                framewidth = frame.width;
                frameheight = frame.height;
                framedata = data;
                DisplayBytes(framewidth, frameheight, framedata);
                mutex.Unlock();
                // CallDeferred(nameof(DisplayBytes), frame.width, frame.height, data);
            }
        }

        public override void _Process(double delta)
        {
            if (mutex.TryLock())
            {
                // DisplayBytes(framewidth, frameheight, framedata);
                DisplayImage();
                Display(texture);
                mutex.Unlock();
            }
        }

        private void DisplayBytes(int framewidth, int frameheight, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }
            bool newImage = false;
            if (image == null)
                image = Image.CreateEmpty(16, 16, false, Image.Format.Rgb8);
            if (image.GetWidth() != framewidth || image.GetHeight() != frameheight)
                newImage = true;
            image.SetData(framewidth, frameheight, false, Image.Format.Rgb8, data);
            // image.GenerateMipmaps();
        }

        private void DisplayImage()
        {
            if (!IsInstanceValid(image))
                return;
            if (IsInstanceValid(texture))
            {
                if ((Vector2I)texture.GetSize() != image.GetSize())
                    texture.SetImage(image);
                else
                    texture.Update(image);
            }
            else
                texture = ImageTexture.CreateFromImage(image);
        }

        public void Display(ImageTexture texture)
        {
            if (IsInstanceValid(renderMesh))
            {
                if (materialIndex == -1)
                    SetMainTex(renderMesh.GetActiveMaterial(0), texture);
                else
                    SetMainTex(renderMesh.GetActiveMaterial(materialIndex), texture);
            }
            if (IsInstanceValid(renderRect))
            {
                renderRect.Texture = texture;
            }
            OnDisplay?.Invoke(texture);
        }

        private void SetMainTex(Material material, ImageTexture texture)
        {
            switch (material)
            {
                case StandardMaterial3D mat3d:
                    mat3d.AlbedoTexture = texture;
                    break;
            }
        }

        #region Utils

        [ThreadStatic]
        private static byte[] line;

        public unsafe static bool SaveFrame(AVFrame frame, byte[] texture)
        {
            if (line == null)
            {
                line = new byte[4096 * 4096 * 6]; // TODO: is the buffer big enough?
            }
            if (frame.data[0] == null || frame.format == -1 || texture == null)
            {
                return false;
            }
            using var converter = new VideoFrameConverter(new System.Drawing.Size(frame.width, frame.height), (AVPixelFormat)frame.format, new System.Drawing.Size(frame.width, frame.height), AVPixelFormat.AV_PIX_FMT_RGB24);
            var convFrame = converter.Convert(frame);
            Marshal.Copy((IntPtr)convFrame.data[0], line, 0, frame.width * frame.height * 3);
            Array.Copy(line, 0, texture, 0, frame.width * frame.height * 3);
            return true;
        }

        #endregion
    }
}