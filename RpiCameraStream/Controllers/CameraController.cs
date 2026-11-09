using Microsoft.AspNetCore.Mvc;
using RpiCameraStream.Services;

namespace RpiCameraStream.Controllers;

/// <summary>
/// API controller that serves the MJPEG video stream and snapshots.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CameraController : ControllerBase
{
    private readonly CameraService _cameraService;
    private readonly ILogger<CameraController> _logger;

    public CameraController(CameraService cameraService, ILogger<CameraController> logger)
    {
        _cameraService = cameraService;
        _logger = logger;
    }

    /// <summary>
    /// MJPEG stream endpoint. Returns a continuous multipart/x-mixed-replace response
    /// that can be consumed by an <img> tag or VLC, etc.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        if (!_cameraService.IsCameraRunning)
        {
            Response.StatusCode = 503;
            await Response.WriteAsync("Camera is not running", ct);
            return;
        }

        Response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var subscription = _cameraService.Subscribe();
        try
        {
            await foreach (var frame in subscription.ReadAllAsync(ct))
            {
                await Response.WriteAsync($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n", ct);
                await Response.Body.WriteAsync(frame, ct);
                await Response.WriteAsync("\r\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
        }
        finally
        {
            _cameraService.Unsubscribe(subscription.Id);
        }
    }

    /// <summary>
    /// Snapshot endpoint. Returns the latest captured frame as a JPEG image.
    /// </summary>
    [HttpGet("snapshot")]
    public IActionResult Snapshot()
    {
        var frame = _cameraService.GetLatestFrame();
        if (frame.Length == 0)
        {
            return StatusCode(503, "No frame available — camera may not be running");
        }

        return File(frame, "image/jpeg");
    }

    /// <summary>
    /// Camera status endpoint.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            running = _cameraService.IsCameraRunning,
            width = _cameraService.Width,
            height = _cameraService.Height,
            framerate = _cameraService.Framerate,
            quality = _cameraService.Quality,
            hflip = _cameraService.HFlip,
            vflip = _cameraService.VFlip,
        });
    }
}
