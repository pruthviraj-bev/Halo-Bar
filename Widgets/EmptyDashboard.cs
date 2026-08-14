using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicIsland.Widgets;

/// <summary>
/// Pass 16 diagnostic (Mode B — empty dashboard): a deliberately trivial visual
/// stand-in for <see cref="ExpandedDashboard"/> — one Grid with a single
/// Rectangle — so the first-expand hitch can be attributed to the real
/// dashboard's visual-tree/layout cost versus the window/motion system itself.
/// The window still grows to the same expanded profile and the Pass 9
/// choreography (opacity/scale on the host Border) is untouched.
/// Exists only under HALO_P16_EMPTY=1; production always constructs
/// ExpandedDashboard.
/// </summary>
public sealed class EmptyDashboard : UserControl
{
    public EmptyDashboard()
    {
        var grid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
        };
        grid.Children.Add(new Rectangle
        {
            Width = 200,
            Height = 120,
            RadiusX = 16,
            RadiusY = 16,
            Fill = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Content = grid;
    }
}
