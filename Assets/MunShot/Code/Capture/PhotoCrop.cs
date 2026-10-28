using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace MunShot.Capture
{
    /// <summary>
    /// Crops a photo Redux has saved to a <see cref="FrameShape"/>, writing it back over the file in the same format.
    /// Redux's screenshot has no crop of its own, so the saved file is read back: decoding has to be on the main thread
    /// (Unity's image loading), the cut is a copy of rows, and encoding and writing (thread-safe in Unity) run in the
    /// background. A PNG loses nothing; a JPG is compressed again, at Redux's own quality.
    /// </summary>
    public sealed class PhotoCrop
    {
        // Redux's JPG quality (ScreenshotCapture's JPG_QUALITY), so a cropped JPG is as good as one Redux saves
        private const int JpgQuality = 92;

        private readonly Task _writing;

        /// <summary>The photo's path.</summary>
        public string Path { get; }

        /// <summary>The cropped size, or the photo's own when nothing needed cutting.</summary>
        public Vector2Int Size { get; }

        /// <summary>Whether it's written (or failed): see <see cref="Error"/>.</summary>
        public bool IsDone => _writing == null || _writing.IsCompleted;

        /// <summary>Why the crop failed, once done; null if it worked (the file is then cropped).</summary>
        public string Error => _writing?.Exception?.GetBaseException().Message;

        private PhotoCrop(string path, Vector2Int size, Task writing)
        {
            Path = path;
            Size = size;
            _writing = writing;
        }

        /// <summary>
        /// Reads the photo at <paramref name="path"/> and starts writing it back cropped to <paramref name="shape"/>.
        /// Throws if the photo can't be read; a failure writing it shows in <see cref="Error"/>.
        /// </summary>
        public static PhotoCrop Start(string path, FrameShape shape)
        {
            bool jpg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path), false))
                    throw new InvalidDataException($"Unity couldn't read {path} as an image.");
                int width = texture.width, height = texture.height;
                Rect fit = PhotoFrame.Fit(width, height, shape);
                int cropWidth = Mathf.Clamp(Mathf.RoundToInt(fit.width), 1, width);
                int cropHeight = Mathf.Clamp(Mathf.RoundToInt(fit.height), 1, height);
                if (cropWidth == width && cropHeight == height) return new PhotoCrop(path, new Vector2Int(width, height), null);

                // Centred, so the same rows whether the image's rows run up or down
                int left = (width - cropWidth) / 2, bottom = (height - cropHeight) / 2;
                NativeArray<byte> pixels = texture.GetPixelData<byte>(0);
                int bytesPerPixel = pixels.Length / (width * height);
                var cropped = new NativeArray<byte>(cropWidth * cropHeight * bytesPerPixel, Allocator.Persistent,
                                                    NativeArrayOptions.UninitializedMemory);
                for (int row = 0; row < cropHeight; row++)
                    NativeArray<byte>.Copy(pixels, ((bottom + row) * width + left) * bytesPerPixel,
                                           cropped, row * cropWidth * bytesPerPixel, cropWidth * bytesPerPixel);
                GraphicsFormat format = texture.graphicsFormat;

                Task writing = Task.Run(() =>
                {
                    try
                    {
                        NativeArray<byte> encoded = jpg
                            ? ImageConversion.EncodeNativeArrayToJPG(cropped, format, (uint)cropWidth, (uint)cropHeight, 0u, JpgQuality)
                            : ImageConversion.EncodeNativeArrayToPNG(cropped, format, (uint)cropWidth, (uint)cropHeight, 0u);
                        try
                        {
                            File.WriteAllBytes(path, encoded.ToArray());
                        }
                        finally
                        {
                            encoded.Dispose();
                        }
                    }
                    finally
                    {
                        cropped.Dispose();
                    }
                });
                return new PhotoCrop(path, new Vector2Int(cropWidth, cropHeight), writing);
            }
            finally
            {
                Object.Destroy(texture);
            }
        }
    }
}
