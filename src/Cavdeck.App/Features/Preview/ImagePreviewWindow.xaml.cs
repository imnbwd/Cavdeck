using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;

namespace Cavdeck.App.Features.Preview;

/// <summary>
/// Image-only visual interaction: WPF rendering, mouse anchored zoom, pan and keyboard shortcuts.
/// Remote loading, selection and cancellation stay in ImagePreviewViewModel.
/// </summary>
public partial class ImagePreviewWindow : AppWindow
{
    private readonly ImagePreviewViewModel _model;
    private ImageViewportMath.View _view = new(1, 0, 0);
    private double _imageWidth;
    private double _imageHeight;
    private bool _fitMode = true;
    private bool _dragging;
    private Point _previousPointer;

    public ImagePreviewWindow(ImagePreviewViewModel model, LocalizationService texts)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        UseTexts(texts);
        _model.PropertyChanged += Model_PropertyChanged;
        Loaded += (_, _) =>
        {
            ShowCurrentImage();
            _model.Start();
        };
        Closed += (_, _) =>
        {
            _model.PropertyChanged -= Model_PropertyChanged;
            _model.Dispose();
        };
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImagePreviewViewModel.Image))
            Dispatcher.BeginInvoke(ShowCurrentImage, DispatcherPriority.Loaded);
    }

    private void ShowCurrentImage()
    {
        StopDrag();
        if (_model.Image is not BitmapSource image)
        {
            _imageWidth = _imageHeight = 0;
            ZoomText.Text = "—";
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _imageWidth = image.PixelWidth / dpi.DpiScaleX;
        _imageHeight = image.PixelHeight / dpi.DpiScaleY;
        CanvasImage.Width = _imageWidth;
        CanvasImage.Height = _imageHeight;
        FitToWindow();
    }

    private void FitToWindow()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
            return;
        _fitMode = true;
        _view = ImageViewportMath.Fit(
            _imageWidth, _imageHeight, Viewport.ActualWidth, Viewport.ActualHeight);
        RenderView();
    }

    private void ActualSize()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
            return;
        _fitMode = false;
        ZoomTo(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    }

    private void ZoomBy(double multiplier, Point anchor)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
            return;
        _fitMode = false;
        ZoomTo(_view.Scale * multiplier, anchor);
    }

    private void ZoomTo(double scale, Point anchor)
    {
        _view = ImageViewportMath.Zoom(_view, scale, anchor.X, anchor.Y,
            _imageWidth, _imageHeight, Viewport.ActualWidth, Viewport.ActualHeight);
        RenderView();
    }

    private void RenderView()
    {
        CanvasImage.RenderTransform = new MatrixTransform(
            _view.Scale, 0, 0, _view.Scale, _view.X, _view.Y);
        ZoomText.Text = $"{Math.Round(_view.Scale * 100):0}%";
        Viewport.Cursor = _dragging ? Cursors.SizeAll :
            IsPannable ? Cursors.Hand : Cursors.Arrow;
    }

    private bool IsPannable =>
        _imageWidth * _view.Scale > Viewport.ActualWidth + 0.5 ||
        _imageHeight * _view.Scale > Viewport.ActualHeight + 0.5;

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
            return;
        if (_fitMode)
            FitToWindow();
        else
        {
            _view = ImageViewportMath.CenterOrClamp(_view,
                _imageWidth, _imageHeight, Viewport.ActualWidth, Viewport.ActualHeight);
            RenderView();
        }
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_fitMode) ActualSize();
            else FitToWindow();
            e.Handled = true;
            return;
        }
        if (!IsPannable)
            return;

        _dragging = true;
        _previousPointer = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopDrag();
            return;
        }

        var pointer = e.GetPosition(Viewport);
        _view = ImageViewportMath.Pan(_view,
            pointer.X - _previousPointer.X, pointer.Y - _previousPointer.Y,
            _imageWidth, _imageHeight, Viewport.ActualWidth, Viewport.ActualHeight);
        _previousPointer = pointer;
        RenderView();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        StopDrag();
        e.Handled = true;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
        Viewport.Cursor = IsPannable ? Cursors.Hand : Cursors.Arrow;
    }

    private void StopDrag()
    {
        _dragging = false;
        if (Viewport.IsMouseCaptured)
            Viewport.ReleaseMouseCapture();
        Viewport.Cursor = IsPannable ? Cursors.Hand : Cursors.Arrow;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomBy(1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomBy(1 / 1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();
    private void Actual_Click(object sender, RoutedEventArgs e) => ActualSize();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            _model.Move(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None)
        {
            _model.Move(1);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0)
        {
            FitToWindow();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D1)
        {
            ActualSize();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift
                 && e.Key is Key.Add or Key.OemPlus)
        {
            ZoomBy(1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
                 && e.Key is Key.Subtract or Key.OemMinus)
        {
            ZoomBy(1 / 1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
            e.Handled = true;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(ShowCurrentImage, DispatcherPriority.Loaded);
    }
}
