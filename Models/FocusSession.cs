namespace DynamicIsland.Models;

public class FocusSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Focus";
    public int DurationSeconds { get; set; } = 1500;
}
