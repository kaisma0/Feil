using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Services.Achievements;
using SteamKit2.Authentication;
using Irihi.Avalonia.Shared.Contracts;

namespace Feil.ViewModels;

public enum SignInStep
{
    Credentials,
    Connecting,
    GuardCode,
    Working,
    Done,
}

public partial class SteamSignInDialogViewModel : ObservableObject, IAuthenticator, IDialogContext
{
    private readonly uint _appId;
    private SteamStatsClient? _client;
    private TaskCompletionSource<string>? _guardCodeTcs;

    public event EventHandler<object?>? RequestClose;

    public void Close()
    {
    }

    // ── Bindable Properties ──────────────────────────────────────

    [ObservableProperty] private string  _username = string.Empty;
    [ObservableProperty] private string  _password = string.Empty;
    [ObservableProperty] private string  _guardCode = string.Empty;
    [ObservableProperty] private string  _statusMessage = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string  _progressText = string.Empty;
    [ObservableProperty] private double  _progressValue;
    [ObservableProperty] private double  _progressMax = 100;

    [ObservableProperty] private SignInStep _currentStep = SignInStep.Credentials;

    // ── Derived Visibility ───────────────────────────────────────

    public bool IsCredentialsStep => CurrentStep == SignInStep.Credentials;
    public bool IsConnecting     => CurrentStep == SignInStep.Connecting;
    public bool IsGuardStep      => CurrentStep == SignInStep.GuardCode;
    public bool IsWorking        => CurrentStep == SignInStep.Working;
    public bool IsDone           => CurrentStep == SignInStep.Done;
    public bool CanInteract      => CurrentStep is SignInStep.Credentials or SignInStep.GuardCode;
    public bool HasError         => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsSuccess        { get; private set; }
    public bool ShowProgressBar  => CurrentStep is SignInStep.Working;
    public bool ShowSignIn       => CurrentStep == SignInStep.Credentials;
    public bool ShowGuardSubmit  => CurrentStep == SignInStep.GuardCode;

    partial void OnCurrentStepChanged(SignInStep value)
    {
        OnPropertyChanged(nameof(IsCredentialsStep));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsGuardStep));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(ShowProgressBar));
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowGuardSubmit));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public SteamSignInDialogViewModel(uint appId)
    {
        _appId = appId;

        // Pre-fill username from saved credentials
        var saved = SteamCredentialStore.Load();
        if (!string.IsNullOrWhiteSpace(saved?.Username))
        {
            Username = saved.Username;
        }
    }

    // ── Commands ─────────────────────────────────────────────────

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Serilog.Log.Warning("User attempted to sign in to Steam without providing credentials");
            ErrorMessage = "Please enter your Steam username and password.";
            return;
        }

        ErrorMessage = null;
        CurrentStep = SignInStep.Connecting;
        StatusMessage = "Connecting to Steam...";

        _client = new SteamStatsClient();

        try
        {
            bool connected = await _client.ConnectAndLogOnAsync(
                Username.Trim(), Password, null, this);

            if (!connected)
            {
                Serilog.Log.Warning("Steam authentication failed for user {Username}", Username.Trim());
                CurrentStep = SignInStep.Credentials;
                ErrorMessage = "Authentication failed. Check your credentials and try again.";
                _client.Dispose();
                _client = null;
                return;
            }

            // Save credentials
            if (_client.ReceivedRefreshToken != null)
            {
                SteamCredentialStore.Save(new SteamCredentials
                {
                    Username = Username.Trim(),
                    RefreshToken = _client.ReceivedRefreshToken,
                });
                Serilog.Log.Information("Saved Steam credentials for user {Username}", Username.Trim());
            }

            // Now fetch the schema
            await FetchSchemaAsync();
        }
        catch (Exception ex)
        {
            CurrentStep = SignInStep.Credentials;
            ErrorMessage = $"Connection error: {ex.Message}";
            _client?.Dispose();
            _client = null;
        }
    }

    [RelayCommand]
    private void SubmitGuardCode()
    {
        if (string.IsNullOrWhiteSpace(GuardCode) || _guardCodeTcs == null) return;
        Serilog.Log.Information("User submitted Steam Guard code");

        _guardCodeTcs.TrySetResult(GuardCode.Trim().ToUpperInvariant());
        CurrentStep = SignInStep.Connecting;
        StatusMessage = "Verifying Steam Guard code...";
    }

    [RelayCommand]
    private void Cancel()
    {
        Serilog.Log.Information("User cancelled Steam sign-in");
        _guardCodeTcs?.TrySetCanceled();
        _client?.Dispose();
        _client = null;
        RequestClose?.Invoke(this, null);
    }

    // Called from code-behind when PinCode completes (all 5 chars entered).
    public void OnGuardCodeCompleted(string code)
    {
        GuardCode = code;
        SubmitGuardCode();
    }

    // ── Schema Fetching ──────────────────────────────────────────

    private async Task FetchSchemaAsync()
    {
        CurrentStep = SignInStep.Working;
        StatusMessage = "Fetching achievement schema...";

        var statsDir = StatsSchemaService.ResolveSteamStatsDirectory();
        if (statsDir == null)
        {
            CurrentStep = SignInStep.Done;
            ErrorMessage = "Could not locate Steam stats directory.";
            IsSuccess = false;
            OnPropertyChanged(nameof(IsSuccess));
            ScheduleAutoClose();
            return;
        }

        var progress = new Progress<SchemaProgress>(p =>
        {
            ProgressValue = p.CheckedOwners;
            ProgressMax = p.TotalOwners;
            ProgressText = $"Checking owner {p.CheckedOwners}/{p.TotalOwners}...";
        });

        bool found = await Task.Run(() =>
            StatsSchemaService.FetchAndSaveSchemaAsync(_appId, statsDir, _client!, progress));

        CurrentStep = SignInStep.Done;
        IsSuccess = found;
        OnPropertyChanged(nameof(IsSuccess));

        if (found)
        {
            Serilog.Log.Information("Successfully fetched achievement schema for AppId {AppId}", _appId);
            StatusMessage = "Achievement schema generated successfully!";
            ErrorMessage = null;
        }
        else
        {
            Serilog.Log.Warning("No achievement schema found for AppId {AppId}", _appId);
            StatusMessage = "Schema generation complete.";
            ErrorMessage = $"No achievement schema found for app {_appId}.";
        }

        _client?.Dispose();
        _client = null;

        ScheduleAutoClose();
    }

    private void ScheduleAutoClose()
    {
        if (IsSuccess)
        {
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    RequestClose?.Invoke(this, IsSuccess));
            });
        }
    }

    // ── IAuthenticator (SteamKit2 2FA Bridge) ────────────────────

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        return RequestGuardCodeFromUiAsync(
            previousCodeWasIncorrect
                ? "Incorrect code. Enter your Steam Guard code:"
                : "Enter the code from your Steam Guard app:");
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        return RequestGuardCodeFromUiAsync(
            previousCodeWasIncorrect
                ? "Incorrect code. Check your email for a new code:"
                : $"Enter the code sent to {email}:");
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = "Confirm login on your mobile device...";
        });

        // SteamKit2 will poll — we just show the message
        return Task.FromResult(true);
    }

    private Task<string> RequestGuardCodeFromUiAsync(string message)
    {
        _guardCodeTcs = new TaskCompletionSource<string>();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            GuardCode = string.Empty;
            StatusMessage = message;
            ErrorMessage = null;
            CurrentStep = SignInStep.GuardCode;
        });

        return _guardCodeTcs.Task;
    }
}
