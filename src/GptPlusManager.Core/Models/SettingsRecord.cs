namespace GptPlusManager.Core.Models;

public sealed class SettingsRecord
{
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 8;
    public double LastKeepAlive { get; set; }

    public void Normalize()
    {
        if (!double.IsFinite(IntervalHours))
        {
            IntervalHours = 8;
        }

        IntervalHours = Math.Clamp(IntervalHours, 6, 24);
        if (!double.IsFinite(LastKeepAlive) || LastKeepAlive < 0)
        {
            LastKeepAlive = 0;
        }
    }
}
