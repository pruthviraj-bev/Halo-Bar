using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace DynamicIsland.Controls;

/// <summary>
/// Shared card shell for every widget.
///
/// Exposes four content slots — <see cref="HeaderContent"/>, <see cref="BodyContent"/>,
/// <see cref="FooterContent"/> and <see cref="OverlayContent"/> — plus the Default /
/// Hover / Pressed / Focused interaction states. All visuals are built exclusively
/// from the design tokens in Resources/Tokens.xaml; no raw values.
/// </summary>
public sealed partial class WidgetCard : UserControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent),
        typeof(object),
        typeof(WidgetCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent),
        typeof(object),
        typeof(WidgetCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent),
        typeof(object),
        typeof(WidgetCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty OverlayContentProperty = DependencyProperty.Register(
        nameof(OverlayContent),
        typeof(object),
        typeof(WidgetCard),
        new PropertyMetadata(null));

    /// <summary>Top strip of the card (icon + title, ~40px tall).</summary>
    public object HeaderContent
    {
        get => (object)GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>Primary widget body.</summary>
    public object BodyContent
    {
        get => (object)GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    /// <summary>Action ribbon below the body.</summary>
    public object FooterContent
    {
        get => (object)GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <summary>Overlay rendered above the body slot.</summary>
    public object OverlayContent
    {
        get => (object)GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    private bool _isPointerOver;
    private bool _isPressed;
    private bool _isFocused;

    public WidgetCard()
    {
        InitializeComponent();

        // Structural values with no token: the header slot height is a spec constant
        // (02-Design-System.md §4, ~40px), and Thickness/GridLength properties cannot
        // consume the x:Double Spacing tokens via StaticResource, so they are
        // materialized here from the token resources.
        HeaderRow.Height = new GridLength(40);
        CardSurface.Padding = new Thickness(Token("Spacing.L"));
        ApplyMotionDurations();

        UpdateVisualState(false);
    }

    private static double Token(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value))
        {
            return Convert.ToDouble(value);
        }

        throw new InvalidOperationException($"Missing design token '{key}'.");
    }

    private void ApplyMotionDurations()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(Token("Motion.Micro")));
        foreach (var name in new[] { "DefaultStoryboard", "HoverStoryboard", "PressedStoryboard", "FocusedStoryboard" })
        {
            if (FindName(name) is not Storyboard storyboard)
            {
                continue;
            }

            foreach (var child in storyboard.Children)
            {
                if (child is DoubleAnimation animation)
                {
                    animation.Duration = duration;
                }
            }
        }
    }

    private void UpdateVisualState(bool useTransitions)
    {
        var state = _isPressed ? "Pressed"
            : _isFocused ? "Focused"
            : _isPointerOver ? "Hover"
            : "Default";
        VisualStateManager.GoToState(this, state, useTransitions);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisualState(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        _isPressed = false;
        UpdateVisualState(true);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isPressed = true;
            UpdateVisualState(true);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        UpdateVisualState(true);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        _isPressed = false;
        UpdateVisualState(true);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = true;
        UpdateVisualState(true);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = false;
        UpdateVisualState(true);
    }
}
