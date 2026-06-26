using CortexTransl.App.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace CortexTransl.App.Views;

public partial class RegionSelectorWindow : Window
{
    private WpfPoint? _startPoint;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    public CaptureRegion? SelectedRegion { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Activate();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(SelectionCanvas);
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionCanvas.CaptureMouse();
        UpdateSelection(_startPoint.Value, _startPoint.Value);
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_startPoint is null || !SelectionCanvas.IsMouseCaptured)
        {
            return;
        }

        UpdateSelection(_startPoint.Value, e.GetPosition(SelectionCanvas));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            return;
        }

        var endPoint = e.GetPosition(SelectionCanvas);
        SelectionCanvas.ReleaseMouseCapture();

        var x = Math.Min(_startPoint.Value.X, endPoint.X);
        var y = Math.Min(_startPoint.Value.Y, endPoint.Y);
        var width = Math.Abs(endPoint.X - _startPoint.Value.X);
        var height = Math.Abs(endPoint.Y - _startPoint.Value.Y);

        if (width < 8 || height < 8)
        {
            DialogResult = false;
            Close();
            return;
        }

        var topLeft = PointToScreen(new WpfPoint(x, y));
        var bottomRight = PointToScreen(new WpfPoint(x + width, y + height));

        SelectedRegion = new CaptureRegion(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X),
            (int)Math.Round(bottomRight.Y - topLeft.Y));

        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void UpdateSelection(WpfPoint start, WpfPoint current)
    {
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }
}
