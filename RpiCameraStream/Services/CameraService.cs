using System.Collections.Concurrent;
using System.Diagnostics;

namespace RpiCameraStream.Services;

/// <summary>
/// Background service that captures MJPEG frames from the Raspberry Pi CSI camera
/// using rpicam-vid (or libcamera-vid as fallback).
/// Each connected client gets its own bounded channel of JPEG frames.
/// </summary>
public sealed class CameraService : BackgroundService
{
    private readonly ILogger<CameraService> _logger;
    private readonly IConfiguration _configuration;

    // Latest JPEG frame captured from the camera
    private byte[] _latestFrame = [];
    private readonly Lock _frameLock = new();

    // Connected clients waiting for frames
    private readonly ConcurrentDictionary<Guid, ClientSubscription> _clients = new();

    // Camera status
    private volatile bool _isCameraRunning;
    public bool IsCameraRunning => _isCameraRunning;

    // Camera settings (can be changed at runtime — requires restart of the capture process)
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Framerate { get; set; } = 30;
    public int Quality { get; set; } = 80;
    public bool HFlip { get; set; }
    public bool VFlip { get; set; }

    public CameraService(ILogger<CameraService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Read defaults from configuration
        var section = _configuration.GetSection("Camera");
        if (section.Exists())
        {
            Width = section.GetValue("Width", Width);
            Height = section.GetValue("Height", Height);
            Framerate = section.GetValue("Framerate", Framerate);
            Quality = section.GetValue("Quality", Quality);
            HFlip = section.GetValue("HFlip", HFlip);
            VFlip = section.GetValue("VFlip", VFlip);
        }
    }

    /// <summary>
    /// Subscribe to the camera feed. Returns a stream of JPEG frames.
    /// </summary>
    public ClientSubscription Subscribe()
    {
        var sub = new ClientSubscription();
        _clients.TryAdd(sub.Id, sub);
        _logger.LogInformation("Client {ClientId} subscribed. Total clients: {Count}", sub.Id, _clients.Count);
        return sub;
    }

    /// <summary>
    /// Unsubscribe a client from the camera feed.
    /// </summary>
    public void Unsubscribe(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var sub))
        {
            sub.Dispose();
            _logger.LogInformation("Client {ClientId} unsubscribed. Total clients: {Count}", clientId, _clients.Count);
        }
    }

    /// <summary>
    /// Get the latest captured frame (for snapshot endpoint).
    /// </summary>
    public byte[] GetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestFrame;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CameraService starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCameraCapture(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Camera capture failed. Restarting in 3 seconds...");
                _isCameraRunning = false;
                await Task.Delay(3000, stoppingToken);
            }
        }

        _isCameraRunning = false;
        _logger.LogInformation("CameraService stopped.");
    }

    private async Task RunCameraCapture(CancellationToken stoppingToken)
    {
        // Try rpicam-vid first, then libcamera-vid
        var (command, args) = GetCameraCommand();

        _logger.LogInformation("Starting camera: {Command} {Args}", command, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("[Camera stderr] {Data}", e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        _isCameraRunning = true;
        _logger.LogInformation("Camera process started (PID: {Pid})", process.Id);

        var stream = process.StandardOutput.BaseStream;
        await ParseMjpegStream(stream, stoppingToken);

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        _isCameraRunning = false;
    }

    private (string command, string args) GetCameraCommand()
    {
        var flipArgs = "";
        if (HFlip) flipArgs += " --hflip";
        if (VFlip) flipArgs += " --vflip";

        var commonArgs = $"--width {Width} --height {Height} --framerate {Framerate} " +
                         $"--codec mjpeg --quality {Quality} " +
                         $"--nopreview -t 0 -o -{flipArgs}";

        // Check which camera tool is available
        if (IsCommandAvailable("rpicam-vid"))
            return ("rpicam-vid", commonArgs);

        if (IsCommandAvailable("libcamera-vid"))
            return ("libcamera-vid", commonArgs);

        throw new InvalidOperationException(
            "Neither rpicam-vid nor libcamera-vid found. " +
            "Please install libcamera-apps: sudo apt install libcamera-apps");
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(2000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parse the MJPEG stream from stdout. MJPEG is just a sequence of JPEG frames.
    /// We detect JPEG SOI (0xFF 0xD8) and EOI (0xFF 0xD9) markers.
    /// </summary>
    private async Task ParseMjpegStream(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var frameBuffer = new MemoryStream();
        bool inFrame = false;

        while (!ct.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
            {
                _logger.LogWarning("Camera stream ended (0 bytes read).");
                break;
            }

            for (int i = 0; i < bytesRead; i++)
            {
                if (!inFrame)
                {
                    // Look for JPEG SOI marker: 0xFF 0xD8
                    if (i + 1 < bytesRead && buffer[i] == 0xFF && buffer[i + 1] == 0xD8)
                    {
                        inFrame = true;
                        frameBuffer.SetLength(0);
                        frameBuffer.WriteByte(buffer[i]);
                        frameBuffer.WriteByte(buffer[i + 1]);
                        i++; // skip next byte
                    }
                }
                else
                {
                    frameBuffer.WriteByte(buffer[i]);

                    // Look for JPEG EOI marker: 0xFF 0xD9
                    if (frameBuffer.Length >= 2)
                    {
                        var pos = frameBuffer.Position;
                        if (buffer[i] == 0xD9 && pos >= 2)
                        {
                            // Check previous byte
                            frameBuffer.Position = pos - 2;
                            var prev = frameBuffer.ReadByte();
                            frameBuffer.Position = pos;

                            if (prev == 0xFF)
                            {
                                // Complete JPEG frame
                                var frame = frameBuffer.ToArray();
                                DistributeFrame(frame);
                                inFrame = false;
                            }
                        }
                    }
                }
            }
        }
    }

    private void DistributeFrame(byte[] frame)
    {
        lock (_frameLock)
        {
            _latestFrame = frame;
        }

        foreach (var (id, client) in _clients)
        {
            if (!client.TryWrite(frame))
            {
                // Client is too slow; skip frame for this client
                _logger.LogTrace("Dropped frame for slow client {ClientId}", id);
            }
        }
    }
}

/// <summary>
/// Represents a client subscription to the camera feed.
/// Uses a bounded channel to buffer frames for each client.
/// </summary>
public sealed class ClientSubscription : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    private readonly System.Threading.Channels.Channel<byte[]> _channel;
    private readonly System.Threading.Channels.ChannelWriter<byte[]> _writer;
    private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;

    public ClientSubscription()
    {
        _channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(3)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
        _writer = _channel.Writer;
        _reader = _channel.Reader;
    }

    public bool TryWrite(byte[] frame) => _writer.TryWrite(frame);

    public async ValueTask<byte[]> ReadAsync(CancellationToken ct) => await _reader.ReadAsync(ct);

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken ct) => _reader.ReadAllAsync(ct);

    public void Dispose() => _writer.TryComplete();
}
