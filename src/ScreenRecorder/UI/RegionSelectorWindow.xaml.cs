using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenRecorder.Capture;

namespace ScreenRecorder.UI;

/// <summary>
/// Per-monitor dimmed overlay for drag-to-select region capture (PLAN §4.2).
/// One window per monitor keeps mixed-DPI setups correct.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));

    private readonly MonitorInfo _monitor;
    private readonly Rectangle _dimLeft = new() { Fill = DimBrush };
    private readonly Rectangle _dimRight = new() { Fill = DimBrush };
    private readonly Rectangle _dimTop = new() { Fill = DimBrush };
    private readonly Rectangle _dimBottom = new() { Fill = DimBrush };
    private readonly Rectangle _selectionBorder = new()
    {
        Fill = Brushes.Transparent,
        StrokeThickness = 2,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _sizeLabel = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2, 8, 2),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };

    private Point _dragStart;
    private bool _dragging;
    private Rect? _selection; // DIPs, relative to this overlay (= monitor-relative)

    public RegionSelectorWindow(MonitorInfo monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        Left = DpiConvert.ToDips(monitor.Left, monitor.Dpi);
        Top = DpiConvert.ToDips(monitor.Top, monitor.Dpi);
        Width = DpiConvert.ToDips(monitor.Width, monitor.Dpi);
        Height = DpiConvert.ToDips(monitor.Height, monitor.Dpi);

        _selectionBorder.Stroke = new SolidColorBrush(SystemParameters.WindowGlassColor);
        _sizeLabel.Child = _sizeText;
        Root.Children.Add(_dimLeft);
        Root.Children.Add(_dimRight);
        Root.Children.Add(_dimTop);
        Root.Children.Add(_dimBottom);
        Root.Children.Add(_selectionBorder);
        Root.Children.Add(_sizeLabel);

        Loaded += (_, _) =>
        {
            UpdateVisuals();
            Focus();
            Root.Focus();
        };
        SizeChanged += (_, _) => UpdateVisuals();
        PreviewKeyDown += OnKey;
    }

    public event Action<CaptureTarget>? Confirmed;
    public event Action? Cancelled;

    /// <summary>
    /// Shows one overlay per monitor and returns the confirmed region, or null when
    /// cancelled. The owner is hidden while selecting.
    /// </summary>
    public static Task<CaptureTarget?> ShowAsync(Window owner, IReadOnlyList<MonitorInfo> monitors)
    {
        var completion = new TaskCompletionSource<CaptureTarget?>();
        var windows = monitors.Select(m => new RegionSelectorWindow(m)).ToList();
        var finished = false;

        void Finish(CaptureTarget? target)
        {
            if (finished)
                return;
            finished = true;
            foreach (var window in windows)
            {
                window.Confirmed -= OnConfirmed;
                window.Cancelled -= OnCancelled;
                window.Close();
            }
            owner.Show();
            owner.Activate();
            completion.SetResult(target);
        }

        void OnConfirmed(CaptureTarget target) => Finish(target);
        void OnCancelled() => Finish(null);

        foreach (var window in windows)
        {
            window.Confirmed += OnConfirmed;
            window.Cancelled += OnCancelled;
        }
        owner.Hide();
        foreach (var window in windows)
            window.Show();
        return completion.Task;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(Root);
        _selection = new Rect(_dragStart, _dragStart);
        Root.CaptureMouse();
        UpdateVisuals();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var pos = e.GetPosition(Root);
        _selection = new Rect(
            Math.Min(_dragStart.X, pos.X), Math.Min(_dragStart.Y, pos.Y),
            Math.Abs(pos.X - _dragStart.X), Math.Abs(pos.Y - _dragStart.Y));
        UpdateVisuals();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Root.ReleaseMouseCapture();
        UpdateVisuals();
        if (_selection is { Width: > 4, Height: > 4 })
            ConfirmButton.Focus(); // highlight the choice: Enter/Space confirms
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Confirm();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Confirm()
    {
        if (_selection is not { Width: > 4, Height: > 4 } sel)
            return; // too small to be intentional; keep selecting
        var x = DpiConvert.ToPixels(sel.X, _monitor.Dpi);
        var y = DpiConvert.ToPixels(sel.Y, _monitor.Dpi);
        var w = DpiConvert.ToPixels(sel.Width, _monitor.Dpi);
        var h = DpiConvert.ToPixels(sel.Height, _monitor.Dpi);
        Confirmed?.Invoke(CaptureTarget.Normalize(_monitor, x, y, w, h));
    }

    private void UpdateVisuals()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        if (_selection is not { Width: > 0, Height: > 0 } sel)
        {
            SetRect(_dimLeft, 0, 0, w, h);
            SetRect(_dimRight, 0, 0, 0, 0);
            SetRect(_dimTop, 0, 0, 0, 0);
            SetRect(_dimBottom, 0, 0, 0, 0);
            _selectionBorder.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            Buttons.Visibility = Visibility.Collapsed;
            return;
        }

        // Four dimmed rects around the clear selection.
        SetRect(_dimLeft, 0, 0, sel.X, h);
        SetRect(_dimRight, sel.Right, 0, w - sel.Right, h);
        SetRect(_dimTop, sel.X, 0, sel.Width, sel.Y);
        SetRect(_dimBottom, sel.X, sel.Bottom, sel.Width, h - sel.Bottom);

        _selectionBorder.Visibility = Visibility.Visible;
        SetRect(_selectionBorder, sel.X, sel.Y, sel.Width, sel.Height);

        var pxW = DpiConvert.ToPixels(sel.Width, _monitor.Dpi) & ~1;
        var pxH = DpiConvert.ToPixels(sel.Height, _monitor.Dpi) & ~1;
        _sizeText.Text = $"{pxW} × {pxH}";
        _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeLabel, Math.Max(4, sel.X));
        Canvas.SetTop(_sizeLabel, sel.Y >= 34 ? sel.Y - 30 : Math.Min(h - 30, sel.Bottom + 6));

        Buttons.Visibility = Visibility.Visible;
    }

    private static void SetRect(Rectangle rect, double x, double y, double width, double height)
    {
        Canvas.SetLeft(rect, Math.Max(0, x));
        Canvas.SetTop(rect, Math.Max(0, y));
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
    }
}
