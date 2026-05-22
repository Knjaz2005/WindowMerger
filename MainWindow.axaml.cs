using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Collections.ObjectModel;

namespace WindowMerger
{
    public partial class MainWindow : Window
    {
        private readonly NativeWindowService windowService = new();
        private readonly ObservableCollection<WindowListItem> windows = new();

        public MainWindow()
        {
            InitializeComponent();

            WindowList.ItemsSource = windows;
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

        private void WindowList_OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            var window = GetWindowItem(e.Source);
            if (window is null)
            {
                return;
            }

            if (windowService.MoveToNextMonitor(window.Handle, out var message))
            {
                RefreshWindows();
            }

            StatusText.Text = message;
        }

        private WindowListItem? GetWindowItem(object? source)
        {
            if (source is Control control)
            {
                var listBoxItem = control.FindAncestorOfType<ListBoxItem>();
                if (listBoxItem?.DataContext is WindowListItem window)
                {
                    return window;
                }

                if (control.DataContext is WindowListItem controlWindow)
                {
                    return controlWindow;
                }
            }

            return WindowList.SelectedItem as WindowListItem;
        }

        private void RefreshWindows()
        {
            windows.Clear();

            var currentWindows = windowService.GetWindows();
            foreach (var window in currentWindows)
            {
                windows.Add(window);
            }

            var monitorCount = windowService.GetMonitors().Count;
            SummaryText.Text = $"{currentWindows.Count} окон, {monitorCount} экранов";
            StatusText.Text = currentWindows.Count == 0
                ? "Окна не найдены."
                : "Готово.";
        }
    }
}
