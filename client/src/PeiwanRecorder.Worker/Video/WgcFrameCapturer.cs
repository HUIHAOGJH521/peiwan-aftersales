using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PeiwanRecorder.Worker.Video;

/// <summary>
/// 基于 Windows.Graphics.Capture (WGC) 的零注入游戏画面采集器。
/// 优先捕获目标窗口句柄；若窗口尚未就绪则采集主显示器，若处于无桌面/RDP受限环境则退化为 GDI 截屏。
/// </summary>
public sealed class WgcFrameCapturer : IDisposable
{
    private static readonly Guid IGraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid ID3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly object _frameLock = new();
    private byte[]? _latestFrameBgra;
    private int _width;
    private int _height;
    private bool _disposed;

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private ID3D11Texture2D? _stagingTexture;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private bool _useGdiFallback;
    private CancellationTokenSource? _gdiCts;

    public int Width => _width;
    public int Height => _height;

    public void Start(nint targetHwnd)
    {
        try
        {
            InitializeWgc(targetHwnd);
        }
        catch
        {
            StartGdiFallback();
        }
    }

    public bool TryCopyLatestFrame(out byte[] frameCopy, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (_latestFrameBgra == null || _width <= 0 || _height <= 0)
            {
                frameCopy = [];
                width = 0;
                height = 0;
                return false;
            }

            frameCopy = new byte[_latestFrameBgra.Length];
            Buffer.BlockCopy(_latestFrameBgra, 0, frameCopy, 0, _latestFrameBgra.Length);
            width = _width;
            height = _height;
            return true;
        }
    }

    public bool SaveSnapshotPng(string outputPath)
    {
        if (!TryCopyLatestFrame(out var bgra, out var w, out var h))
        {
            return false;
        }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
        bmp.Save(outputPath, ImageFormat.Png);
        return true;
    }

    private void InitializeWgc(nint targetHwnd)
    {
        var hr = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _d3dDevice);

        if (hr.Failure || _d3dDevice == null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed: {hr}");
        }

        _d3dContext = _d3dDevice.ImmediateContext;
        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        var createHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtDevicePtr);
        if (createHr != 0 || winrtDevicePtr == nint.Zero)
        {
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: {createHr}");
        }

        _winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(winrtDevicePtr);
        Marshal.Release(winrtDevicePtr);

        _captureItem = CreateCaptureItem(targetHwnd);
        _width = EnsureEven(_captureItem.Size.Width);
        _height = EnsureEven(_captureItem.Size.Height);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureItem.Size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.StartCapture();
    }

    private static GraphicsCaptureItem CreateCaptureItem(nint targetHwnd)
    {
        var factoryPtr = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = IGraphicsCaptureItemGuid;
        nint itemPtr = nint.Zero;

        if (targetHwnd != nint.Zero)
        {
            var hr = factoryPtr.CreateForWindow(targetHwnd, ref iid, out itemPtr);
            if (hr != 0 || itemPtr == nint.Zero)
            {
                itemPtr = nint.Zero;
            }
        }

        if (itemPtr == nint.Zero)
        {
            var hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var hr = factoryPtr.CreateForMonitor(hMonitor, ref iid, out itemPtr);
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != nint.Zero) Marshal.Release(itemPtr);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed || _d3dDevice == null || _d3dContext == null) return;

        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        try
        {
            var surfacePtr = ((IWinRTObject)frame.Surface).NativeObject.ThisPtr;
            var accessIid = ID3DDxgiInterfaceAccessGuid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, ref accessIid, out var accessPtr));
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                var texIid = ID3D11Texture2DGuid;
                Marshal.ThrowExceptionForHR(access.GetInterface(ref texIid, out var texPtr));

                using var srcTex = new ID3D11Texture2D(texPtr);
                var desc = srcTex.Description;
                var w = EnsureEven((int)desc.Width);
                var h = EnsureEven((int)desc.Height);

                if (_stagingTexture == null ||
                    _stagingTexture.Description.Width != desc.Width ||
                    _stagingTexture.Description.Height != desc.Height)
                {
                    _stagingTexture?.Dispose();
                    var stagingDesc = new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };
                    _stagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);
                }

                _d3dContext.CopyResource(_stagingTexture, srcTex);
                var mapped = _d3dContext.Map(_stagingTexture, 0, MapMode.Read);
                try
                {
                    var rowBytes = w * 4;
                    var buffer = new byte[rowBytes * h];
                    for (var row = 0; row < h; row++)
                    {
                        var srcRowPtr = IntPtr.Add(mapped.DataPointer, row * (int)mapped.RowPitch);
                        Marshal.Copy(srcRowPtr, buffer, row * rowBytes, rowBytes);
                    }

                    lock (_frameLock)
                    {
                        _width = w;
                        _height = h;
                        _latestFrameBgra = buffer;
                    }
                }
                finally
                {
                    _d3dContext.Unmap(_stagingTexture, 0);
                }
            }
            finally
            {
                Marshal.Release(accessPtr);
            }
        }
        catch
        {
            // 忽略偶发桌面切换或窗口最小化期间的帧异常
        }
    }

    private void StartGdiFallback()
    {
        _useGdiFallback = true;
        _gdiCts = new CancellationTokenSource();
        var token = _gdiCts.Token;

        _ = Task.Run(async () =>
        {
            var sw = Math.Max(640, GetSystemMetrics(0));
            var sh = Math.Max(360, GetSystemMetrics(1));
            var w = EnsureEven(sw);
            var h = EnsureEven(sh);

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(66));

            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        var bytes = new byte[w * h * 4];
                        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                        lock (_frameLock)
                        {
                            _width = w;
                            _height = h;
                            _latestFrameBgra = bytes;
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                }
                catch
                {
                }
            }
        }, token);
    }

    private static int EnsureEven(int value) => value <= 2 ? 2 : (value & ~1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gdiCts?.Cancel();
        _gdiCts?.Dispose();
        if (_framePool != null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }
        _session?.Dispose();
        _framePool?.Dispose();
        _stagingTexture?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
        _ = _useGdiFallback;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow([In] nint window, [In] ref Guid iid, [Out] out nint result);

        [PreserveSig]
        int CreateForMonitor([In] nint monitor, [In] ref Guid iid, [Out] out nint result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface([In] ref Guid iid, [Out] out nint p);
    }
}
