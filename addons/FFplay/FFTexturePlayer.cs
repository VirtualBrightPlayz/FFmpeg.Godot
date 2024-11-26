using System;
using System.Runtime.InteropServices;
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
        public int materialIndex = -1;
        public Action<ImageTexture> OnDisplay = null;
        private Image image;
        private ImageTexture texture;

        public void PlayPacket(AVFrame frame)
        {
            pts = frame.pts;
            byte[] data = new byte[frame.width * frame.height * 3];
            if (SaveFrame(frame, data))
            {
                if (image == null)
                    image = Image.CreateEmpty(16, 16, false, Image.Format.Rgb8);
                // if (image.GetWidth() != frame.width || image.GetHeight() != frame.height)
                image.SetData(frame.width, frame.height, false, Image.Format.Rgb8, data);
            }
            if (IsInstanceValid(texture))
                texture.SetImage(image);
            else
                texture = ImageTexture.CreateFromImage(image);
            Display(texture);
        }

        private void Display(ImageTexture texture)
        {
            if (OnDisplay == null)
            {
                if (materialIndex == -1)
                    SetMainTex(renderMesh.GetActiveMaterial(0), texture);
                else
                    SetMainTex(renderMesh.GetActiveMaterial(materialIndex), texture);
            }
            else
            {
                OnDisplay.Invoke(texture);
            }
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