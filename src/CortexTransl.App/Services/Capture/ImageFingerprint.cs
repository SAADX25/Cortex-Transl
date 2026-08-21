using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Capture;

public static class ImageFingerprint
{
    public static string Compute(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                var xStep = Math.Max(1, bitmap.Width / 48);
                var yStep = Math.Max(1, bitmap.Height / 48);
                var stride = data.Stride;
                var baseOffset = stride < 0 ? Math.Abs(stride) * (bitmap.Height - 1) : 0;

                for (var y = 0; y < bitmap.Height; y += yStep)
                {
                    var row = baseOffset + (y * stride);
                    for (var x = 0; x < bitmap.Width; x += xStep)
                    {
                        var index = row + (x * 4);
                        hash = (hash ^ (byte)(Marshal.ReadByte(data.Scan0, index) >> 4)) * prime;
                        hash = (hash ^ (byte)(Marshal.ReadByte(data.Scan0, index + 1) >> 4)) * prime;
                        hash = (hash ^ (byte)(Marshal.ReadByte(data.Scan0, index + 2) >> 4)) * prime;
                    }
                }

                return hash.ToString("X16");
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
