using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DynamicIsland.Widgets;

public class TaskItem : INotifyPropertyChanged
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    private bool _isCompleted = false;
    public bool IsCompleted
    {
        get => _isCompleted;
        set { _isCompleted = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
