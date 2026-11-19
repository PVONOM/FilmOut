using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmOut.App.Controls;

/// <summary>
/// A colour-balance wheel. The puck position (inside the unit disc) is a direction and amount of
/// colour shift. Dragging is relative, so grabbing the wheel never makes the puck jump.
/// Hold Shift for fine control; double-click to centre.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    public const double Diameter = 128;

    private static readonly BitmapSource s_disc = CreateDisc((int)Diameter);

    private Vector _puck;
    private bool _dragging;
    private Point _dragStart;
    private Vector _puckAtDragStart;

    public ColorWheel()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Drag to shift the colour. Hold Shift for fine control. Double-click to reset.";
    }

    /// <summary>Raised when the user moves the puck (not when Puck is set in code).</summary>
    public event EventHandler? PuckDragged;

    public Vector Puck
    {
        get => _puck;
        set
        {
            _puck = value.Length > 1 ? value / value.Length : value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    protected override void OnRender(DrawingContext dc)
    {
        double r = Diameter / 2;
        dc.DrawImage(s_disc, new Rect(0, 0, Diameter, Diameter));

        var guide = new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), 1);
        dc.DrawLine(guide, new Point(r, 8), new Point(r, Diameter - 8));
        dc.DrawLine(guide, new Point(8, r), new Point(Diameter - 8, r));

        var centre = new Point(r + _puck.X * r, r - _puck.Y * r);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), centre, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), centre, 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Puck = default;
            PuckDragged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _puckAtDragStart = _puck;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        double sensitivity = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.2 : 1.0;
        Vector moved = (e.GetPosition(this) - _dragStart) / (Diameter / 2) * sensitivity;
        Puck = _puckAtDragStart + new Vector(moved.X, -moved.Y);
        PuckDragged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _dragging = false;

    /// <summary>A muted hue disc, drawn with the same channel directions the grade maths uses.</summary>
    private static BitmapSource CreateDisc(int size)
    {
        var pixels = new byte[size * size * 4];
        double r = size / 2.0;
        double ar = GradeEditor.ChannelAngles[0] * Math.PI / 180;
        double ag = GradeEditor.ChannelAngles[1] * Math.PI / 180;
        double ab = GradeEditor.ChannelAngles[2] * Math.PI / 180;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - r, dy = r - (y + 0.5);
                double distance = Math.Sqrt(dx * dx + dy * dy) / r;
                double alpha = Math.Clamp((1 - distance) * r + 0.5, 0, 1);
                if (alpha <= 0) continue;

                double theta = Math.Atan2(dy, dx), m = Math.Min(distance, 1);
                double red = 0.40 + 0.28 * m * Math.Cos(theta - ar);
                double green = 0.40 + 0.28 * m * Math.Cos(theta - ag);
                double blue = 0.40 + 0.28 * m * Math.Cos(theta - ab);

                int i = (y * size + x) * 4; // premultiplied BGRA
                pixels[i] = (byte)(blue * alpha * 255);
                pixels[i + 1] = (byte)(green * alpha * 255);
                pixels[i + 2] = (byte)(red * alpha * 255);
                pixels[i + 3] = (byte)(alpha * 255);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
