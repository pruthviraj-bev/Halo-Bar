using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DynamicIsland.ViewModels;

/// <summary>
/// MainPageViewModel using C# 13 partial properties for MVVM data-binding.
/// This pattern is fully AOT-compatible for WinUI 3 environments.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Hello, WinUI!";

    [ObservableProperty]
    public partial int Counter { get; set; }

    [RelayCommand]
    private void Increment()
    {
        Counter++;
    }

    [RelayCommand]
    private void Decrement()
    {
        Counter--;
    }
}
