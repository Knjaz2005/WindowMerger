using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WindowMerger
{
    internal sealed class NativeWindowService
    {
        private const int MonitorInfoPrimary = 0x00000001;
        private const int DwmwaCloaked = 14;
        private const int SwShowNoActivate = 4;
        private const uint MonitorDefaultToNearest = 2;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsCaption = 0x00C00000;
        private const long WsExToolWindow = 0x00000080;
        private const long WsExAppWindow = 0x00040000;
        private const long WsExNoActivate = 0x08000000;

        public IReadOnlyList<WindowListItem> GetWindows(IReadOnlyList<MonitorInfo>? monitors = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Array.Empty<WindowListItem>();
            }

            monitors ??= GetMonitors();
            var windows = new List<WindowListItem>();

            if (!EnumWindows((handle, _) =>
            {
                if (!ShouldShowWindow(handle) || !TryGetWindowBounds(handle, out var bounds))
                {
                    return true;
                }

                var monitorHandle = MonitorFromWindow(handle, MonitorDefaultToNearest);
                var monitor = monitors.FirstOrDefault(item => item.Handle == monitorHandle);
                var (processName, processId) = GetProcessInfo(handle);
                var title = GetWindowTitle(handle);

                windows.Add(new WindowListItem(
                    handle,
                    string.IsNullOrWhiteSpace(title) ? $"{processName} (без заголовка)" : title,
                    processName,
                    processId,
                    bounds,
                    monitor?.DisplayName ?? "Неизвестный экран",
                    monitor?.Index ?? int.MaxValue,
                    IsIconic(handle)));

                return true;
            }, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить список окон.");
            }

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

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr deviceContext, ref Rect monitorRect, IntPtr data) =>
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
            }, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить список экранов.");
            }

            return monitors
                .OrderBy(monitor => monitor.Bounds.Left)
                .ThenBy(monitor => monitor.Bounds.Top)
                .Select((monitor, index) => monitor with { Index = index + 1 })
                .ToArray();
        }

        public async Task<WindowMoveResult> MoveToNextMonitorAsync(WindowListItem window)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new(false, "Перенос окон поддерживается только в Windows.");
            }

            var windowHandle = window.Handle;
            if (!IsCurrentWindow(window))
            {
                return new(false, "Окно уже закрыто. Обновите список.");
            }

            var monitors = GetMonitors();
            if (monitors.Count == 0)
            {
                return new(false, "Не удалось найти подключенные экраны.");
            }

            // Resolve the source before restoring: a minimized window has synthetic coordinates.
            var sourceHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            var targetMonitor = GetNextMonitor(monitors, sourceHandle);
            if (targetMonitor is null)
            {
                return new(false, "Расположение экранов изменилось. Повторите перенос.");
            }

            var restoreTimer = Stopwatch.StartNew();
            for (var restoreAttempt = 0; restoreAttempt < 2
                && (IsIconic(windowHandle) || IsZoomed(windowHandle)); restoreAttempt++)
            {
                var wasMinimized = IsIconic(windowHandle);
                if (!ShowWindowAsync(windowHandle, SwShowNoActivate))
                {
                    return new(false, "Не удалось восстановить окно.");
                }

                // A window minimized from maximized needs a second restore to become normal.
                while (IsCurrentWindow(window)
                    && (IsIconic(windowHandle) || (!wasMinimized && IsZoomed(windowHandle))))
                {
                    if (restoreTimer.ElapsedMilliseconds >= 2000)
                    {
                        return new(false, "Окно не отвечает на запрос восстановления.");
                    }

                    await Task.Delay(50);
                }
            }

            // A DPI change can resize the window after moving it. Recenter using its actual size.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!IsCurrentWindow(window) || !TryGetWindowBounds(windowHandle, out var bounds))
                {
                    return new(false, "Окно уже закрыто или недоступно. Обновите список.");
                }

                var centered = bounds.CenterIn(targetMonitor.WorkArea);
                if (!SetWindowPos(windowHandle, IntPtr.Zero, centered.Left, centered.Top, 0, 0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos))
                {
                    var error = Marshal.GetLastWin32Error();
                    return new(false, error == 5
                        ? "Доступ к окну запрещён. Для этого окна нужны те же права запуска."
                        : $"Не удалось перенести окно. Код Win32: {error}.");
                }

                var moveTimer = Stopwatch.StartNew();
                while (moveTimer.ElapsedMilliseconds < 1000)
                {
                    await Task.Delay(50);
                    if (!IsCurrentWindow(window) || !TryGetWindowBounds(windowHandle, out var actual))
                    {
                        return new(false, "Окно закрылось во время переноса.");
                    }

                    if (!IsIconic(windowHandle) && !IsZoomed(windowHandle)
                        && actual == actual.CenterIn(targetMonitor.WorkArea))
                    {
                        return new(true, $"Окно перенесено: {targetMonitor.DisplayName}.");
                    }

                    if (actual.Width != bounds.Width || actual.Height != bounds.Height)
                    {
                        break;
                    }
                }
            }

            return new(false, "Окно не подтвердило перенос в центр экрана: оно не отвечает или ограничивает перемещение.");
        }

        internal static MonitorInfo? GetNextMonitor(IReadOnlyList<MonitorInfo> monitors, IntPtr sourceHandle)
        {
            for (var index = 0; index < monitors.Count; index++)
            {
                if (monitors[index].Handle == sourceHandle)
                {
                    return monitors[(index + 1) % monitors.Count];
                }
            }

            return null;
        }

        private static bool IsCurrentWindow(WindowListItem window)
        {
            return IsWindow(window.Handle)
                && GetWindowThreadProcessId(window.Handle, out var processId) != 0
                && processId == window.ProcessId;
        }

        private static bool ShouldShowWindow(IntPtr handle)
        {
            if (!IsWindowVisible(handle) || IsCloaked(handle)
                || handle == GetShellWindow() || handle == GetDesktopWindow())
            {
                return false;
            }

            var extendedStyle = GetWindowStyle(handle, GwlExStyle);
            if ((extendedStyle & WsExAppWindow) == 0
                && ((extendedStyle & WsExNoActivate) != 0
                    || ((extendedStyle & WsExToolWindow) != 0
                        && (GetWindowStyle(handle, GwlStyle) & WsCaption) == 0)))
            {
                return false;
            }

            var className = new StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            return className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
        }

        private static long GetWindowStyle(IntPtr handle, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(handle, index).ToInt64() : GetWindowLong(handle, index);
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
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return ("Недоступный процесс", (int)processId);
            }
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clippingRect, MonitorEnumProc enumProc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr handle, int index);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr handle, int command);

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

    internal sealed record WindowMoveResult(bool Success, string Message);

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
            int screenIndex,
            bool isMinimized)
        {
            Handle = handle;
            Title = title;
            ProcessName = processName;
            ProcessId = processId;
            ScreenName = screenName;
            ScreenIndex = screenIndex;
            BoundsText = isMinimized ? "Свёрнуто" : $"{bounds.Left}, {bounds.Top}; {bounds.Width}x{bounds.Height}";
        }

        internal IntPtr Handle { get; }

        internal int ScreenIndex { get; }

        public string Title { get; }

        public string ProcessName { get; }

        public int ProcessId { get; }

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

        public NativeBounds CenterIn(NativeBounds area)
        {
            var left = area.Left + (area.Width - Width) / 2;
            var top = area.Top + (area.Height - Height) / 2;
            return new NativeBounds(left, top, left + Width, top + Height);
        }
    }
}
