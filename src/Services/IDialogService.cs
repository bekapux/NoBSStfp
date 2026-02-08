using System.Threading.Tasks;

using NoBSSftp.Models;

namespace NoBSSftp.Services;

public interface IDialogService
{
    Task<string> PromptAsync(string title, string message, string defaultValue = "");
    Task<bool> ConfirmAsync(string title, string message);
    Task<bool> ConfirmHostKeyAsync(string title, string message, string details, bool isWarning);
    Task<ConflictChoice> ConfirmConflictAsync(string title, string message, string sourceDetails, string destinationDetails);
    Task<ConflictDialogResult> ConfirmConflictWithScopeAsync(
        string title,
        string message,
        string sourceDetails,
        string destinationDetails,
        bool allowApplyToAll);
    Task<ServerProfile?> ShowServerFormAsync(ServerProfile? existing = null);
    Task<ConnectInfo?> ShowConnectDialogAsync(string title, string host, ServerProfile? defaults = null);
    Task<string?> PickFolderAsync();
    Task<string?> PickFileAsync(string title);
}

public enum ConflictChoice
{
    Cancel,
    Overwrite,
    Duplicate
}

public readonly record struct ConflictDialogResult(ConflictChoice Choice, bool ApplyToAll);
