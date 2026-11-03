using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Spectari.Capture;

/// <summary>
/// Asks a responsive window to render one still image of itself, scaled to fit
/// the stream canvas as tightly packed BGRA. Only the window's own rendering is
/// used, never the screen, so no other content can enter a window share.
/// </summary>
internal sealed class WindowStillImage
{
    private const int TimeoutMs = 1500;
    private const int RespondTimeoutMs = 100;

    private Task<byte[]>? _pending;

    /// <summary>Throws with a plain reason when no still can be taken. One owner
    /// thread calls it.</summary>
    internal byte[] Capture(IntPtr windowHandle, int width, int height)
    {
        if (_pending is { IsCompleted: true }) _pending = null;
        // PrintWindow can block for as long as the target thread is hung, even after
        // it answered WM_NULL. A stuck attempt keeps its thread, so never stack a
        // second one behind it; the next call after it completes starts fresh.
        if (_pending is not null)
            throw new InvalidOperationException("an earlier still image request has not finished");

        Task<byte[]> attempt = Task.Factory.StartNew(
            () => Read(windowHandle, width, height),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _pending = attempt;
        if (Task.WaitAny([attempt], TimeoutMs) < 0)
        {
            Console.Error.WriteLine(
                $"[window-still] still image request did not return within {TimeoutMs} ms; retrying once it finishes.");
            throw new TimeoutException("the window did not return a still image in time");
        }
        _pending = null;
        return attempt.GetAwaiter().GetResult();
    }

    private static byte[] Read(IntPtr windowHandle, int width, int height)
    {
        // Physical pixels regardless of the process DPI mode, matching what WGC captures.
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("the window no longer exists");
        if (!IsWindowVisible(windowHandle) || IsIconic(windowHandle))
            throw new InvalidOperationException("the window is not visible");
        if (DwmGetWindowAttribute(windowHandle, Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            throw new InvalidOperationException("the window is not visible");
        if (!GetWindowDisplayAffinity(windowHandle, out uint affinity) || affinity != DisplayAffinityNone)
            throw new InvalidOperationException("the window does not allow capture of its contents");
        if (SendMessageTimeoutW(windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero,
                AbortIfHung, RespondTimeoutMs, out _) == IntPtr.Zero)
            throw new InvalidOperationException("the window is not responding");
        if (!GetWindowRect(windowHandle, out RECT windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
            throw new InvalidOperationException("the window has no visible surface");
        RECT frame = DwmGetWindowAttribute(windowHandle, ExtendedFrameBounds, out RECT extended,
            Marshal.SizeOf<RECT>()) == 0 && extended.Width > 0 && extended.Height > 0
            ? extended
            : windowRect;

        using var source = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            IntPtr dc = graphics.GetHdc();
            bool printed;
            try { printed = PrintWindow(windowHandle, dc, RenderFullContent); }
            finally { graphics.ReleaseHdc(dc); }
            if (!printed)
                throw new InvalidOperationException("the window could not render a still image");
        }

        var region = Rectangle.Intersect(
            new Rectangle(0, 0, windowRect.Width, windowRect.Height),
            new Rectangle(frame.Left - windowRect.Left, frame.Top - windowRect.Top, frame.Width, frame.Height));
        if (region.Width <= 0 || region.Height <= 0)
            region = new Rectangle(0, 0, windowRect.Width, windowRect.Height);
        return ScaleToFit(source, region, width, height);
    }

    private static byte[] ScaleToFit(Bitmap source, Rectangle region, int width, int height)
    {
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Min((float)width / region.Width, (float)height / region.Height);
            float fittedWidth = region.Width * scale;
            float fittedHeight = region.Height * scale;
            var target = new RectangleF((width - fittedWidth) / 2f, (height - fittedHeight) / 2f,
                fittedWidth, fittedHeight);
            graphics.DrawImage(source, target, region, GraphicsUnit.Pixel);
        }
        return ToBgra(canvas);
    }

    /// <summary>Copies a 32bpp ARGB bitmap into a tightly packed BGRA frame.</summary>
    internal static byte[] ToBgra(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] frame = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), frame, y * rowBytes, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return frame;
    }

    private const uint RenderFullContent = 0x2;
    private const uint WmNull = 0x0000;
    private const uint AbortIfHung = 0x0002;
    private const uint DisplayAffinityNone = 0x0;
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
