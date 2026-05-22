using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowMerger
{
    internal sealed class NativeWindowService
    {
        private const int MonitorInfoPrimary = 0x00000001;
        private const int DwmwaCloaked = 14;
        private const int SwRestore = 9;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        public IReadOnlyList<WindowListItem> GetWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Array.Empty<WindowListItem>();
            }

            var monitors = GetMonitors();
            var windows = new List<WindowListItem>();

            EnumWindows((handle, _) =>
            {
                if (!ShouldShowWindow(handle) || !TryGetWindowBounds(handle, out var bounds))
                {
                    return true;
                }

                var title = GetWindowTitle(handle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                var monitor = FindMonitor(bounds, monitors);
                var (processName, processId) = GetProcessInfo(handle);

                windows.Add(new WindowListItem(
                    handle,
                    title,
                    processName,
                    processId,
                    bounds,
                    monitor?.DisplayName ?? "Неизвестный экран",
                    monitor?.Index ?? int.MaxValue));

                return true;
            }, IntPtr.Zero);

            return windows
                .OrderBy(window => window.ScreenIndex)
                .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Array.Empty<MonitorInfo>();
            }

            var monitors = new List<MonitorInfo>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr deviceContext, ref Rect monitorRect, IntPtr data) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>(),
                    DeviceName = string.Empty
                };

                if (GetMonitorInfo(handle, ref info))
                {
                    monitors.Add(new MonitorInfo(
                        0,
                        handle,
                        info.DeviceName,
                        NativeBounds.FromRect(info.Monitor),
                        NativeBounds.FromRect(info.WorkArea),
                        (info.Flags & MonitorInfoPrimary) == MonitorInfoPrimary));
                }

                return true;
            }, IntPtr.Zero);

            return monitors
                .OrderBy(monitor => monitor.Bounds.Left)
                .ThenBy(monitor => monitor.Bounds.Top)
                .Select((monitor, index) => monitor with { Index = index + 1 })
                .ToArray();
        }

        public bool MoveToNextMonitor(IntPtr windowHandle, out string message)
        {
            message = string.Empty;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                message = "Перенос окон поддерживается только в Windows.";
                return false;
            }

            var monitors = GetMonitors();
            if (monitors.Count < 2)
            {
                message = "Следующий экран не найден: подключен только один экран.";
                return false;
            }

            if (!TryGetWindowBounds(windowHandle, out var bounds))
            {
                message = "Не удалось получить размеры окна.";
                return false;
            }

            if (IsIconic(windowHandle) || IsZoomed(windowHandle))
            {
                ShowWindow(windowHandle, SwRestore);
                TryGetWindowBounds(windowHandle, out bounds);
            }

            var sourceMonitor = FindMonitor(bounds, monitors);
            if (sourceMonitor is null)
            {
                message = "Не удалось определить текущий экран окна.";
                return false;
            }

            var nextMonitorIndex = sourceMonitor.Index % monitors.Count;
            var targetMonitor = monitors[nextMonitorIndex];
            var targetArea = targetMonitor.WorkArea;
            var x = targetArea.Left + Math.Max(0, (targetArea.Width - bounds.Width) / 2);
            var y = targetArea.Top + Math.Max(0, (targetArea.Height - bounds.Height) / 2);

            if (!SetWindowPos(windowHandle, IntPtr.Zero, x, y, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate))
            {
                var error = Marshal.GetLastWin32Error();
                message = $"Не удалось перенести окно. Код Win32: {error}.";
                return false;
            }

            var title = GetWindowTitle(windowHandle);
            message = string.IsNullOrWhiteSpace(title)
                ? $"Окно перенесено на {targetMonitor.DisplayName}."
                : $"Окно «{title}» перенесено на {targetMonitor.DisplayName}.";
            return true;
        }

        private static bool ShouldShowWindow(IntPtr handle)
        {
            return IsWindowVisible(handle)
                && !IsIconic(handle)
                && !IsCloaked(handle);
        }

        private static bool TryGetWindowBounds(IntPtr handle, out NativeBounds bounds)
        {
            bounds = default;

            if (!GetWindowRect(handle, out var rect))
            {
                return false;
            }

            bounds = NativeBounds.FromRect(rect);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        private static string GetWindowTitle(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(length + 1);
            _ = GetWindowText(handle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static (string ProcessName, int ProcessId) GetProcessInfo(IntPtr handle)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return ("Неизвестный процесс", 0);
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                return (process.ProcessName, (int)processId);
            }
            catch
            {
                return ("Недоступный процесс", (int)processId);
            }
        }

        private static MonitorInfo? FindMonitor(NativeBounds bounds, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors.Count == 0)
            {
                return null;
            }

            return monitors
                .OrderByDescending(monitor => monitor.Bounds.GetIntersectionArea(bounds))
                .ThenBy(monitor => monitor.Bounds.GetCenterDistanceSquared(bounds))
                .First();
        }

        private static bool IsCloaked(IntPtr handle)
        {
            try
            {
                var result = DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, Marshal.SizeOf<int>());
                return result == 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref Rect rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clippingRect, MonitorEnumProc enumProc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }
    }

    internal sealed record MonitorInfo(
        int Index,
        IntPtr Handle,
        string DeviceName,
        NativeBounds Bounds,
        NativeBounds WorkArea,
        bool IsPrimary)
    {
        public string DisplayName => IsPrimary
            ? $"Экран {Index} (основной)"
            : $"Экран {Index}";
    }

    public sealed class WindowListItem
    {
        internal WindowListItem(
            IntPtr handle,
            string title,
            string processName,
            int processId,
            NativeBounds bounds,
            string screenName,
            int screenIndex)
        {
            Handle = handle;
            Title = title;
            ProcessName = processName;
            ProcessId = processId;
            ScreenName = screenName;
            ScreenIndex = screenIndex;
            BoundsText = $"{bounds.Left}, {bounds.Top}; {bounds.Width}x{bounds.Height}";
        }

        internal IntPtr Handle { get; }

        internal int ScreenIndex { get; }

        public string Title { get; }

        public string ProcessName { get; }

        public int ProcessId { get; }

        public string ProcessText => $"{ProcessName} ({ProcessId})";

        public string ScreenName { get; }

        public string BoundsText { get; }
    }

    internal readonly record struct NativeBounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;

        public static NativeBounds FromRect(NativeWindowService.Rect rect)
        {
            return new NativeBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        public long GetIntersectionArea(NativeBounds other)
        {
            var left = Math.Max(Left, other.Left);
            var top = Math.Max(Top, other.Top);
            var right = Math.Min(Right, other.Right);
            var bottom = Math.Min(Bottom, other.Bottom);
            var width = Math.Max(0, right - left);
            var height = Math.Max(0, bottom - top);

            return (long)width * height;
        }

        public double GetCenterDistanceSquared(NativeBounds other)
        {
            var x = (Left + Right) / 2d - (other.Left + other.Right) / 2d;
            var y = (Top + Bottom) / 2d - (other.Top + other.Bottom) / 2d;

            return x * x + y * y;
        }
    }
}
