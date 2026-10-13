using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect(int left = 0, int top = 0, int right = 0, int bottom = 0)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;
}
