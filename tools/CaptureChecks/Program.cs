using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

using var device = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
using var texture = device.CreateTexture2D(new Texture2DDescription
{
    Width = 16,
    Height = 16,
    MipLevels = 1,
    ArraySize = 1,
    Format = Format.B8G8R8A8_UNorm,
    SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Default,
    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
});
using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
Native.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var surfacePointer);
try
{
    var surface = MarshalInterface<IDirect3DSurface>.FromAbi(surfacePointer);
    try
    {
        var pointer = GraphicsCaptureInterop.GetD3D11TexturePointer(surface);
        Require(pointer != nint.Zero, "Texture interop returned no texture.");
        using var recovered = new ID3D11Texture2D(pointer);
        Require(recovered.Description.Width == 16 && recovered.Description.Height == 16,
            "Texture interop recovered a different surface.");
        Console.WriteLine("WinRT to D3D11 texture interop: passed.");
    }
    finally
    {
        (surface as IDisposable)?.Dispose();
    }
}
finally
{
    Marshal.Release(surfacePointer);
}

(string Input, string Expected)[] uiCases =
[
    ("Steam", "Steam"), ("Brave", "Brave"), ("Discord", "Discord"),
    ("New Game", "لعبة جديدة"), ("Load Game", "تحميل لعبة محفوظة"),
    ("This PC", "هذا الكمبيوتر"), ("Recycle Bin", "سلة المحذوفات"),
    ("Control Panel", "لوحة التحكم"), ("PC Health\nCheck", "فحص صحة الكمبيوتر"),
    ("Visual Studio\nCode", "Visual Studio Code"), ("Tor Browser", "متصفح Tor"),
    ("NVIDIA App", "تطبيق NVIDIA"), ("Cortex DNA", "Cortex DNA"),
    ("&Settings...", "الإعدادات..."), ("Save (Ctrl+S)", "حفظ (Ctrl+S)"),
    ("Resolution: 1920x1080", "دقة العرض: 1920x1080"), ("VSync: Off", "المزامنة الرأسية: إيقاف"),
    ("saadr.docx", "saadr.docx"), ("K70RGB", "K70RGB"), ("SAAD X", "SAAD X"),
    ("Free Downlo...", "Free Downlo..."), ("Driver Booster F...", "Driver Booster F...")
];
using var modelHandler = new RejectNetworkHandler();
using var modelHttp = new HttpClient(modelHandler);
using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(
    Path.Combine(AppContext.BaseDirectory, "empty-models"), httpClient: modelHttp));
var uiResults = await offline.TranslateManyAsync(uiCases.Select(test => test.Input).ToArray(),
    "en", "ar", contentKind: TranslationContentKind.UiLabel);
for (var index = 0; index < uiCases.Length; index++)
{
    Require(uiResults[index] == uiCases[index].Expected,
        $"UI label {uiCases[index].Input}: got {uiResults[index]}");
}
Require(modelHandler.Attempts == 0, "Known offline menus attempted to use the network.");
try
{
    await offline.TranslateAsync("Steam", "en", "ar", contentKind: TranslationContentKind.GameDialogue);
    throw new InvalidOperationException("Dialogue incorrectly used the product-name shortcut.");
}
catch (TranslationProviderException)
{
    Require(modelHandler.Attempts == 0, "Missing dialogue models attempted a download during playback.");
}
Console.WriteLine($"Offline menu terminology: {uiCases.Length} cases passed; no network for known labels; dialogue isolation passed.");

OcrTextBlock[] captionBlocks =
[
    new("PC Health", new(10, 10, 90, 16)), new("Check", new(35, 26, 40, 16)),
    new("Steam", new(150, 10, 50, 16)), new("Downloads", new(150, 26, 80, 16)),
    new("New Game", new(10, 90, 80, 16)), new("Continue", new(10, 120, 80, 16))
];
var mergedCaptions = MenuLabelAssembler.MergeKnownWrappedLabels(captionBlocks);
Require(mergedCaptions.Count == 5, "Wrapped caption merge changed independent labels.");
var healthCaption = mergedCaptions.Single(block => block.Text.Contains("PC Health"));
Require(healthCaption.Text.Replace(Environment.NewLine, " ") == "PC Health Check", "Wrapped product name was not merged.");
Require(healthCaption.Bounds == new CaptureRegion(10, 10, 90, 32), "Merged OCR coordinates do not cover both lines.");
OcrTextBlock[] threeLineCaption =
[
    new("Visual", new(10, 10, 50, 16)), new("Studio", new(10, 26, 50, 16)), new("Code", new(20, 42, 30, 16))
];
var mergedThree = MenuLabelAssembler.MergeKnownWrappedLabels(threeLineCaption);
Require(mergedThree.Count == 1 && mergedThree[0].Text.Replace(Environment.NewLine, " ") == "Visual Studio Code",
    "Three-line caption was not merged.");
var context = new StoryTranslationContext { GameKey = "fixture" };
Require(context.OfflineCacheScope == (context with { Speaker = "Alice" }).OfflineCacheScope,
    "Unused speaker facts fragmented offline caches.");
Require(context.OfflineCacheScope != (context with { GameKey = "other game" }).OfflineCacheScope,
    "Offline cache identity lost game isolation.");
Console.WriteLine("Wrapped captions, independent labels, bounds, and menu-cache separation: passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static class Native
{
    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint surface);
}

sealed class RejectNetworkHandler : HttpMessageHandler
{
    public int Attempts { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Attempts++;
        throw new HttpRequestException("No model downloads are allowed in the offline terminology check.");
    }
}
