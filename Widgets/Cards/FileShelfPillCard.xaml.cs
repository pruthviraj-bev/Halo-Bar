using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Interfaces;

namespace DynamicIsland.Widgets.Cards;

public sealed partial class FileShelfPillCard : UserControl, IPillCard, INotifyPropertyChanged
{
    // ── IPillCard ────────────────────────────────────────────────────────────
    private bool _shouldShow;
    public bool ShouldShow
    {
        get => _shouldShow;
        private set
        {
            if (_shouldShow == value) return;
            _shouldShow = value;
            OnPropertyChanged();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CardWidth { get; } = 48;
    public UserControl View => this;
    public event EventHandler? StateChanged;

    // ── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Construction ─────────────────────────────────────────────────────────
    public FileShelfPillCard()
    {
        InitializeComponent();
        App.FileShelfStore.ItemsChanged += OnItemsChanged;
        Refresh();
    }

    private void OnItemsChanged(object? sender, EventArgs e)
        => Refresh();

    private void Refresh()
    {
        int count = App.FileShelfStore.Items.Count;
        ShouldShow = count > 0;

        if (CountText != null)
            CountText.Text = count.ToString();

        if (CountBadge != null)
            CountBadge.Visibility = count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnShelfButtonClick(object sender, RoutedEventArgs e)
    {
        // Fire an event so PillDashboard can toggle the inline shelf panel.
        ShelfButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the shelf button is clicked.
    /// PillDashboard subscribes and expands/collapses the inline shelf panel.
    /// </summary>
    public event EventHandler? ShelfButtonClicked;
}