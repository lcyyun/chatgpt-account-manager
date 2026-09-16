using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GptPlusManager.Wpf.Infrastructure;

public sealed class DragGhostAdorner : Adorner
{
    private readonly ImageSource _image;
    private readonly Size _size;
    private Point _position;
    private readonly Vector _grabOffset;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source, Point grabPoint)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        _size = new Size(source.ActualWidth, source.ActualHeight);
        _grabOffset = new Vector(grabPoint.X, grabPoint.Y);
        var dpi = VisualTreeHelper.GetDpi(source);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(source);
        bitmap.Freeze();
        _image = bitmap;
    }

    public void MoveTo(Point point)
    {
        _position = point;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var origin = _position - _grabOffset;
        drawingContext.PushOpacity(0.82);
        drawingContext.DrawImage(_image, new Rect(origin, _size));
        drawingContext.Pop();
    }
}
