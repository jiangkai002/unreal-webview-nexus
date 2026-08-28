using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DigitalTwin.Host.Protocol;

namespace DigitalTwin.Host.Services;

public sealed class HitRegionService
{
    private static readonly Brush HitBackingBrush = CreateHitBackingBrush();
    private readonly Canvas _backingLayer;
    private string? _lastRegionKey;

    public HitRegionService(Canvas backingLayer)
    {
        _backingLayer = backingLayer;
    }

    public void ApplyFull(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (_lastRegionKey == "full")
        {
            return;
        }

        _backingLayer.Children.Clear();
        _backingLayer.Background = HitBackingBrush;
        _lastRegionKey = "full";
    }

    public void ApplyRegions(Window window, IReadOnlyList<WebHitRegion> regions)
    {
        window.Dispatcher.VerifyAccess();
        var maximumWidth = Math.Max(0, window.ActualWidth);
        var maximumHeight = Math.Max(0, window.ActualHeight);
        var normalized = regions
            .Select(region => Clip(region, maximumWidth, maximumHeight))
            .Where(region => region.Width > 0 && region.Height > 0)
            .Take(512)
            .OrderBy(region => region.X)
            .ThenBy(region => region.Y)
            .ToArray();
        var key = string.Join('|', normalized.Select(region =>
            $"{region.X:F2},{region.Y:F2},{region.Width:F2},{region.Height:F2}"));
        if (key == _lastRegionKey)
        {
            return;
        }

        _backingLayer.Background = null;
        _backingLayer.Children.Clear();
        foreach (var item in normalized)
        {
            var rectangle = new Rectangle
            {
                Width = item.Width,
                Height = item.Height,
                Fill = HitBackingBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, item.X);
            Canvas.SetTop(rectangle, item.Y);
            _backingLayer.Children.Add(rectangle);
        }

        _lastRegionKey = key;
    }

    private static WebHitRegion Clip(WebHitRegion region, double maximumWidth, double maximumHeight)
    {
        var left = Math.Clamp(region.X, 0, maximumWidth);
        var top = Math.Clamp(region.Y, 0, maximumHeight);
        var right = Math.Clamp(region.X + region.Width, 0, maximumWidth);
        var bottom = Math.Clamp(region.Y + region.Height, 0, maximumHeight);
        return new WebHitRegion(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Brush CreateHitBackingBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }
}
