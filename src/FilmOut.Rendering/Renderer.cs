using System.Reflection;
using FilmOut.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FilmOut.Rendering;

/// <summary>
/// Owns the Direct3D device and the output swap chain. All methods must be called on the
/// render thread.
///
/// The frame to show is rendered once into a 10-bit "prepared" texture. Each refresh then
/// either copies that texture to the back buffer or clears the back buffer to black, so
/// presenting is cheap and identical on every refresh of an exposure.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private const int MaxDefects = 8192; // two texels each, within the 16384-wide texture limit

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDXGISwapChain2 _swapChain2;
    private readonly IntPtr _frameWaitable;
    private readonly ID3D11Texture2D _backBuffer;
    private readonly ID3D11RenderTargetView _backBufferView;
    private readonly ID3D11Texture2D _prepared;
    private readonly ID3D11RenderTargetView _preparedView;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;

    private ID3D11Texture2D? _source;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11Texture2D _flat;
    private ID3D11ShaderResourceView _flatView;
    private ID3D11Texture2D _defectMap;
    private ID3D11ShaderResourceView _defectMapView;
    private ID3D11Texture2D _defectData;
    private ID3D11ShaderResourceView _defectDataView;

    public Renderer(IDXGIFactory2 factory, IDXGIAdapter1 adapter, IntPtr hwnd, int width, int height)
    {
        Width = width;
        Height = height;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, levels,
            out ID3D11Device device, out FeatureLevel _, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;

        // Flip model, 10 bits per channel, one frame of latency, paced by a waitable object.
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.R10G10B10A2_UNorm,
            Stereo = false,
            SampleDescription = SampleDescription.Default,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, description,
            new SwapChainFullscreenDescription { Windowed = true });
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);

        _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        DxgiTiming.SetMaximumFrameLatency(_swapChain2.NativePointer, 1);
        _frameWaitable = DxgiTiming.GetFrameLatencyWaitableObject(_swapChain2.NativePointer);

        // With flip-model swap chains in Direct3D 11, buffer 0 always refers to the current back buffer.
        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device.CreateRenderTargetView(_backBuffer);

        _prepared = _device.CreateTexture2D(Describe(width, height, Format.R10G10B10A2_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _preparedView = _device.CreateRenderTargetView(_prepared);

        string shaderSource = LoadShaderSource();
        _vertexShader = _device.CreateVertexShader(ShaderCompiler.Compile(shaderSource, "VSMain", "vs_5_0"));
        _pixelShader = _device.CreatePixelShader(ShaderCompiler.Compile(shaderSource, "PSMain", "ps_5_0"));

        _constants = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)sizeof(GpuConstants),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        });
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);

        (_flat, _flatView) = CreateTexture(1, 1, Format.R32G32B32A32_Float, new float[] { 1, 1, 1, 1 }, 16);
        (_defectMap, _defectMapView) = CreateTexture(1, 1, Format.R16_UInt, new ushort[1], 2);
        (_defectData, _defectDataView) = CreateTexture(2, 1, Format.R32G32B32A32_Float, new float[8], 16);

        ShowBlack();
    }

    public int Width { get; }
    public int Height { get; }
    public string? SourcePath { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public bool HasFlatField { get; private set; }
    public bool HasDefects { get; private set; }

    public void WaitForFrame(uint timeoutMilliseconds) =>
        Native.WaitForSingleObjectEx(_frameWaitable, timeoutMilliseconds, true);

    public void UploadSource(DecodedImage image)
    {
        if (_source is null || SourceWidth != image.Width || SourceHeight != image.Height)
        {
            _sourceView?.Dispose();
            _source?.Dispose();
            _source = _device.CreateTexture2D(Describe(image.Width, image.Height, Format.R16G16B16A16_UNorm,
                BindFlags.ShaderResource | BindFlags.RenderTarget, mipLevels: 0, ResourceOptionFlags.GenerateMips));
            _sourceView = _device.CreateShaderResourceView(_source);
        }

        fixed (byte* p = image.Rgba64)
            _context.UpdateSubresource(_source, 0, null, (IntPtr)p, (uint)(image.Width * 8), 0);
        _context.GenerateMips(_sourceView!);

        SourcePath = image.Path;
        SourceWidth = image.Width;
        SourceHeight = image.Height;
    }

    /// <summary>Makes the next request for any image upload it again (used after the files change on disk).</summary>
    public void ForgetSource() => SourcePath = null;

    public void UploadFlatField(FlatFieldMap? map)
    {
        _flatView.Dispose();
        _flat.Dispose();
        if (map is null)
        {
            (_flat, _flatView) = CreateTexture(1, 1, Format.R32G32B32A32_Float, new float[] { 1, 1, 1, 1 }, 16);
            HasFlatField = false;
            return;
        }

        var rgba = new float[map.Width * map.Height * 4];
        for (int i = 0; i < map.Width * map.Height; i++)
        {
            rgba[i * 4] = map.Gains[i * 3];
            rgba[i * 4 + 1] = map.Gains[i * 3 + 1];
            rgba[i * 4 + 2] = map.Gains[i * 3 + 2];
            rgba[i * 4 + 3] = 1;
        }
        (_flat, _flatView) = CreateTexture(map.Width, map.Height, Format.R32G32B32A32_Float, rgba, 16);
        HasFlatField = true;
    }

    public void UploadDefects(IReadOnlyList<PixelDefect> defects)
    {
        var usable = defects
            .Where(d => d.X >= 0 && d.Y >= 0 && d.X < Width && d.Y < Height && d.Channels != ColorChannels.None)
            .Take(MaxDefects)
            .ToList();

        _defectMapView.Dispose();
        _defectMap.Dispose();
        _defectDataView.Dispose();
        _defectData.Dispose();

        if (usable.Count == 0)
        {
            (_defectMap, _defectMapView) = CreateTexture(1, 1, Format.R16_UInt, new ushort[1], 2);
            (_defectData, _defectDataView) = CreateTexture(2, 1, Format.R32G32B32A32_Float, new float[8], 16);
            HasDefects = false;
            return;
        }

        // Each pixel in a defect's 3 × 3 neighbourhood points at that defect.
        var map = new ushort[Width * Height];
        var data = new float[usable.Count * 8];
        for (int i = 0; i < usable.Count; i++)
        {
            var d = usable[i];
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = d.X + dx, y = d.Y + dy;
                    if (x >= 0 && y >= 0 && x < Width && y < Height) map[y * Width + x] = (ushort)(i + 1);
                }
            data[i * 8] = d.X;
            data[i * 8 + 1] = d.Y;
            data[i * 8 + 2] = (int)d.Channels;
            data[i * 8 + 3] = (int)d.Kind;
            data[i * 8 + 4] = (float)Math.Clamp(d.Level, 0, 1);
        }
        // A defect's own pixel always points at itself, even if a neighbouring defect overlaps it.
        for (int i = 0; i < usable.Count; i++) map[usable[i].Y * Width + usable[i].X] = (ushort)(i + 1);

        (_defectMap, _defectMapView) = CreateTexture(Width, Height, Format.R16_UInt, map, 2);
        (_defectData, _defectDataView) = CreateTexture(usable.Count * 2, 1, Format.R32G32B32A32_Float, data, 16);
        HasDefects = true;
    }

    public void RenderPrepared(in GpuConstants constants)
    {
        var c = constants;
        _context.ClearState();
        _context.UpdateSubresource(_constants, 0, null, (IntPtr)(&c), 0, 0);
        _context.OMSetRenderTargets(_preparedView);
        _context.RSSetViewport(new Viewport(Width, Height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        if (_sourceView is not null) _context.PSSetShaderResource(0, _sourceView);
        _context.PSSetShaderResource(1, _flatView);
        _context.PSSetShaderResource(2, _defectMapView);
        _context.PSSetShaderResource(3, _defectDataView);
        _context.Draw(3, 0);
        _context.ClearState();
        _context.Flush();
    }

    public void ShowPrepared() => _context.CopyResource(_backBuffer, _prepared);

    public void ShowBlack() => _context.ClearRenderTargetView(_backBufferView, new Color4(0f, 0f, 0f, 1f));

    public void Present() => _swapChain.Present(1, PresentFlags.None);

    public uint LastPresentCount => DxgiTiming.GetLastPresentCount(_swapChain.NativePointer);

    public bool TryGetFrameStatistics(out DxgiTiming.FrameStatistics statistics) =>
        DxgiTiming.TryGetFrameStatistics(_swapChain.NativePointer, out statistics);

    public void Dispose()
    {
        _context.ClearState();
        _context.Flush();
        _sourceView?.Dispose();
        _source?.Dispose();
        _flatView.Dispose();
        _flat.Dispose();
        _defectMapView.Dispose();
        _defectMap.Dispose();
        _defectDataView.Dispose();
        _defectData.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _preparedView.Dispose();
        _prepared.Dispose();
        _backBufferView.Dispose();
        _backBuffer.Dispose();
        if (_frameWaitable != IntPtr.Zero) Native.CloseHandle(_frameWaitable);
        _swapChain2.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private (ID3D11Texture2D Texture, ID3D11ShaderResourceView View) CreateTexture<T>(int width, int height, Format format, T[] data, int bytesPerPixel)
        where T : unmanaged
    {
        var texture = _device.CreateTexture2D(Describe(width, height, format, BindFlags.ShaderResource));
        fixed (T* p = data)
            _context.UpdateSubresource(texture, 0, null, (IntPtr)p, (uint)(width * bytesPerPixel), 0);
        return (texture, _device.CreateShaderResourceView(texture));
    }

    private static Texture2DDescription Describe(int width, int height, Format format, BindFlags bind,
        uint mipLevels = 1, ResourceOptionFlags misc = ResourceOptionFlags.None) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = mipLevels,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = bind,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = misc,
    };

    private static string LoadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FilmOut.Rendering.Pipeline.hlsl")
            ?? throw new InvalidOperationException("The display shader is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
