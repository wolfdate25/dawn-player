namespace DawnPlayer.App.Services;

/// <summary>Severity of a user-facing notification, decoupled from WinUI's InfoBarSeverity so
/// the presentation rules stay testable headlessly. Mapping to InfoBar lives in MainWindow.</summary>
public enum UiSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// U2 notification policy for the single InfoBar slot. Rules (from the implementation plan:
/// "자동 닫힘·재시도 액션 규칙 명시"):
///   * Informational/Success auto-close after <see cref="AutoCloseMs"/> — transient confirmations
///     must not linger.
///   * Warning/Error stay until dismissed — problems demand acknowledgment, and auto-hiding a
///     warning reads as "handled" when it is not.
/// A newer notification always replaces the current one (single slot) and cancels any pending
/// auto-close; dismissing cancels it too.
/// </summary>
public sealed class NotificationPresenter
{
    /// <summary>Auto-close delay for transient (informational/success) notifications.</summary>
    public const int AutoCloseMs = 6000;

    /// <summary>Current message, or null when the slot is closed.</summary>
    public string? Message { get; private set; }

    public UiSeverity Severity { get; private set; } = UiSeverity.Informational;

    /// <summary>Whether the current notification auto-closes (false ⇒ stays until dismissed).</summary>
    public bool WillAutoClose { get; private set; }

    public bool IsOpen => Message != null;

    /// <summary>Raised whenever the visible state may have changed.</summary>
    public event Action? Changed;

    public void Show(string message, UiSeverity severity)
    {
        if (string.IsNullOrEmpty(message)) return;
        Message = message;
        Severity = severity;
        WillAutoClose = IsTransient(severity);
        Changed?.Invoke();
    }

    public void Dismiss()
    {
        if (Message == null) return;
        Message = null;
        WillAutoClose = false;
        Changed?.Invoke();
    }

    /// <summary>Transient severities auto-close; problems do not.</summary>
    public static bool IsTransient(UiSeverity severity) =>
        severity is UiSeverity.Informational or UiSeverity.Success;
}
