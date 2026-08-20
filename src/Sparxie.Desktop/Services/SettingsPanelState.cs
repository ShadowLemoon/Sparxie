namespace Sparxie.Desktop.Services;

public sealed class SettingsPanelState
{
    public bool IsOpen { get; private set; }

    public event Action? Changed;

    public void Open()
    {
        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        Changed?.Invoke();
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        Changed?.Invoke();
    }
}
