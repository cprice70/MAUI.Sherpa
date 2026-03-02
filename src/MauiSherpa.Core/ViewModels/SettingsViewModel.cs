namespace MauiSherpa.Core.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    public string Title => "Settings";

    private bool _enableNotifications = true;
    public bool EnableNotifications
    {
        get => _enableNotifications;
        set => SetProperty(ref _enableNotifications, value);
    }

    private string _theme = "System";
    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    private double _fontScale = 1.0;
    public double FontScale
    {
        get => _fontScale;
        set => SetProperty(ref _fontScale, value);
    }

    public SettingsViewModel(Interfaces.IAlertService? alertService = null, Interfaces.ILoggingService? loggingService = null)
        : base(alertService ?? new StubAlertService(), loggingService ?? new StubLoggingService())
    {
    }

    public async Task SaveSettingsAsync()
    {
        await Task.Delay(500);
    }

    public async Task ResetSettingsAsync()
    {
        EnableNotifications = true;
        Theme = "System";
        FontScale = 1.0;
        await Task.Delay(500);
    }

    private class StubAlertService : Interfaces.IAlertService
    {
        public Task ShowAlertAsync(string title, string message, string? cancel = null) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message, string? confirm = null, string? cancel = null) => Task.FromResult(true);
        public Task<string?> ShowActionSheetAsync(string title, string? cancel, string? destruction, params string[] buttons) => Task.FromResult<string?>(null);
        public Task ShowToastAsync(string message) => Task.CompletedTask;
    }

    private class StubLoggingService : Interfaces.ILoggingService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? exception = null) { }
        public void LogDebug(string message) { }
        public IReadOnlyList<Interfaces.LogEntry> GetRecentLogs(int maxCount = 500) => [];
        public void ClearLogs() { }
        public event Action? OnLogAdded;
    }
}
