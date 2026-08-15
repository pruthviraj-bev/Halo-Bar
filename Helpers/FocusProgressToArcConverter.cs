using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DynamicIsland.Helpers;

/// <summary>
/// Converts a 0.0-1.0 progress fraction into the arc geometry for the
/// Focus Session progress ring (a 160x160 ring, stroke-centerline radius 73 —
/// PASS 4, matching the 14 DIP stroke on the 160 DIP ring, starting at the top
/// and sweeping clockwise). Handles the 0 (empty) and full-circle (1.0) edge
/// cases explicitly. Center/Radius stay in sync with the drag/proximity math in
/// ExpandedDashboard (FocusRingCenter / FocusRingRadius).
/// </summary>
public class FocusProgressToArcConverter : IValueConverter
{
    private const double Center = 80;
    private const double Radius = 73;
    private const double TopY = 7;   // Center - Radius
    private const double BottomY = 153; // Center + Radius

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
