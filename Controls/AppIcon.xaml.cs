using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicIsland.Controls;

/// <summary>
/// Reusable icon component backed by the centralized Fluent System Icons
/// geometry in <see cref="AppIcons"/> (see also <see cref="AppIconKind"/>).
///
/// Usage: <c>&lt;controls:AppIcon Kind="Play" Filled="True" Width="20" Height="20"/&gt;</c>
///
/// The default fill is <c>TextPrimaryBrush</c> (theme aware); override via the
/// <see cref="Fill"/> property.
/// </summary>
public sealed partial class AppIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(AppIconKind),
        typeof(AppIcon),
        new PropertyMetadata(AppIconKind.None, OnVisualPropertyChanged));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled),
        typeof(bool),
        typeof(AppIcon),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(AppIcon),
        new PropertyMetadata(null, OnFillPropertyChanged));

    /// <summary>Logical icon to render (see <see cref="AppIconKind"/>).</summary>
    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Renders the Filled variant when available; otherwise falls back to Regular.</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>Brush used to paint the icon. Defaults to the theme TextPrimaryBrush.</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public AppIcon()
    {
        InitializeComponent();
        ApplyGeometry();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AppIcon)d).ApplyGeometry();

    private static void OnFillPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AppIcon)d).ApplyFill();

    private void ApplyGeometry()
    {
        IconPath.Data = AppIcons.GetGeometry(Kind, Filled);
    }

    private void ApplyFill()
    {
        if (Fill != null)
        {
            IconPath.Fill = Fill;
        }
    }
}
