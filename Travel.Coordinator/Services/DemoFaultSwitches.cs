namespace Travel.Coordinator.Services;

// Learning-only switch. Do NOT expose this API in production.
public sealed class DemoFaultSwitches
{
    private volatile bool _pauseNotifications;
    public bool PauseNotifications
    {
        get => _pauseNotifications;
        set => _pauseNotifications = value;
    }
}
