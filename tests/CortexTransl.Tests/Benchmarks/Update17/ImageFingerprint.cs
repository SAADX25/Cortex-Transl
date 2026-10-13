using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace CortexTransl.Tests.Benchmarks.Update17;

public static class ImageFingerprint
{
    public static unsafe string Compute(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> dimensions = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dimensions, bitmap.Width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], bitmap.Height);
            hash.AppendData(dimensions);
            // All pixels, without copying frame buffers or hashing row padding.
            // Scan0 and the signed stride describe each row, including bottom-up images.
            if (data.Stride == bitmap.Width * 4)
                hash.AppendData(new ReadOnlySpan<byte>((void*)data.Scan0, checked(data.Stride * bitmap.Height)));
            else
                for (var y = 0; y < bitmap.Height; y++)
                    hash.AppendData(new ReadOnlySpan<byte>((byte*)data.Scan0 + y * data.Stride, bitmap.Width * 4));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { bitmap.UnlockBits(data); }
    }
}
