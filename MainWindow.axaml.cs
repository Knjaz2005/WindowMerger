using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace WindowMerger
{
    public partial class MainWindow : Window
    {
        private readonly NativeWindowService windowService = new();
        private readonly ObservableCollection<WindowListItem> windows = new();
        private bool isMoving;

        public MainWindow()
        {
            InitializeComponent();

            WindowList.ItemsSource = windows;
            WindowList.AddHandler(KeyDownEvent, WindowList_OnKeyDown, RoutingStrategies.Tunnel);
            Opened += OnOpened;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            RefreshWindows();
        }

        private void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
        {
            RefreshWindows();
        }

        private async void WindowList_OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            var window = GetWindowItem(e.Source);
            if (window is null)
            {
                return;
            }

            e.Handled = true;
            await MoveWindowAsync(window);
        }

        private async void WindowList_OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && WindowList.SelectedItem is WindowListItem window)
            {
                e.Handled = true;
                await MoveWindowAsync(window);
            }
        }

        private static WindowListItem? GetWindowItem(object? source)
        {
            if (source is Control control)
            {
                var listBoxItem = control as ListBoxItem ?? control.FindAncestorOfType<ListBoxItem>();
                if (listBoxItem?.DataContext is WindowListItem window)
                {
                    return window;
                }
            }

            return null;
        }

        private async Task MoveWindowAsync(WindowListItem window)
        {
            if (isMoving)
            {
                return;
            }

            isMoving = true;
            WindowList.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Перенос окна...";
            try
            {
                var result = await windowService.MoveToNextMonitorAsync(window);
                RefreshWindows();
                StatusText.Text = result.Message;
            }
            catch (Win32Exception exception)
            {
                StatusText.Text = exception.Message;
            }
            finally
            {
                isMoving = false;
                WindowList.IsEnabled = true;
                RefreshButton.IsEnabled = true;
            }
        }

        private void RefreshWindows()
        {
            if (!OperatingSystem.IsWindows())
            {
                StatusText.Text = "Работа с окнами поддерживается только в Windows.";
                return;
            }

            try
            {
                var monitors = windowService.GetMonitors();
                var currentWindows = windowService.GetWindows(monitors);
                var selected = WindowList.SelectedItem as WindowListItem;
                windows.Clear();
                foreach (var window in currentWindows)
                {
                    windows.Add(window);
                }

                WindowList.SelectedItem = currentWindows.FirstOrDefault(window =>
                    selected is not null && window.Handle == selected.Handle && window.ProcessId == selected.ProcessId);
                SummaryText.Text = $"Окон: {currentWindows.Count}   Экранов: {monitors.Count}";
                StatusText.Text = currentWindows.Count == 0 ? "Окна не найдены." : "Готово.";
            }
            catch (Win32Exception exception)
            {
                StatusText.Text = exception.Message;
            }
        }
    }
}
