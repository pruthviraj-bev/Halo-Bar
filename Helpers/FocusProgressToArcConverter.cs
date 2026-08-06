using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DynamicIsland.Helpers;

/// <summary>
/// Converts a 0.0-1.0 progress fraction into the arc geometry for the
/// Focus Session progress ring (a 100x100 ring, radius 42, starting at the top
/// and sweeping clockwise). Handles the 0 (empty) and full-circle (1.0) edge
/// cases explicitly.
/// </summary>
public class FocusProgressToArcConverter : IValueConverter
{
    private const double Center = 50;
    private const double Radius = 42;
    private const double TopY = 8;   // Center - Radius
    private const double BottomY = 92; // Center + Radius

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double fraction = 0;
        if (value is double d)
            fraction = Math.Clamp(d, 0, 1);

        var geometry = new PathGeometry();

        // No visible arc at 0%.
        if (fraction <= 0)
            return geometry;

        // A single ArcSegment cannot represent a complete 360° circle without
        // rendering artifacts — split the full ring into two half-arcs.
        if (fraction >= 1)
        {
            geometry.Figures.Add(CreateHalfArc(new Point(Center, TopY), new Point(Center, BottomY)));
            geometry.Figures.Add(CreateHalfArc(new Point(Center, BottomY), new Point(Center, TopY)));
            return geometry;
        }

        double angle = 360.0 * fraction;
        double radians = angle * Math.PI / 180.0;
        var end = new Point(
            Center + Radius * Math.Sin(radians),
            Center - Radius * Math.Cos(radians));

        var figure = new PathFigure
        {
            StartPoint = new Point(Center, TopY),
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = angle > 180
        });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathFigure CreateHalfArc(Point startPoint, Point endPoint)
    {
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });
        return figure;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
