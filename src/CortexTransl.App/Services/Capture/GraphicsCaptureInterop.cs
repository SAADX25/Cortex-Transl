using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CortexTransl.App.Services.Capture;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);

        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static GraphicsCaptureItem CreateItemForWindow(nint window)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForWindow(window, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    public static IDirect3DDevice CreateWinRtDevice(nint dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static nint GetD3D11TexturePointer(IDirect3DSurface surface)
    {
        // The surface is a C#/WinRT projection, not a COM RCW. Query the
        // native interface through WinRT instead of casting the managed object.
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = D3D11Texture2DIid;
        return access.GetInterface(ref iid);
    }
}
