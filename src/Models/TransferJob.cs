using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoBSSftp.Models;

public enum TransferJobType
{
    Upload,
    Download,
    Delete
}

public enum TransferJobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public partial class TransferJob : ObservableObject
{
    public TransferJob(TransferJobType jobType,
        string title)
    {
        Id = Guid.NewGuid();
        CreatedUtc = DateTime.UtcNow;
        _jobType = jobType;
        _title = title;
        _state = TransferJobState.Pending;
        _status = "Queued";
    }

    public Guid Id { get; }
    public DateTime CreatedUtc { get; }

    [ObservableProperty]
    private TransferJobType _jobType;

    [ObservableProperty]
    private TransferJobState _state;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isRetryable;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsPending => State == TransferJobState.Pending;
    public bool IsRunning => State == TransferJobState.Running;
    public bool IsCompleted => State == TransferJobState.Completed;
    public bool IsFailed => State == TransferJobState.Failed;
    public bool IsCancelled => State == TransferJobState.Cancelled;

    public string StateLabel =>
        State switch
        {
            TransferJobState.Pending => "Pending",
            TransferJobState.Running => "Running",
            TransferJobState.Completed => "Completed",
            TransferJobState.Failed => "Failed",
            TransferJobState.Cancelled => "Cancelled",
            _ => State.ToString()
        };

    partial void OnStateChanged(TransferJobState value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(StateLabel));
    }
}
