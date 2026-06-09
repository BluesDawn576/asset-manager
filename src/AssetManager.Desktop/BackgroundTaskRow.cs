using System.ComponentModel;
using System.Globalization;
using AssetManager.Application.BackgroundTasks;
using AssetManager.Desktop.Localization;

namespace AssetManager.Desktop;

public sealed class BackgroundTaskRow : INotifyPropertyChanged
{
    private BackgroundTaskSnapshot _snapshot;

    public BackgroundTaskRow(BackgroundTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public Guid Id => _snapshot.Id;

    public string Title => _snapshot.Title;

    public string StateText => _snapshot.State switch
    {
        BackgroundTaskState.Running => LocalizationManager.Get("BackgroundTaskStateRunning"),
        BackgroundTaskState.Completed => LocalizationManager.Get("BackgroundTaskStateCompleted"),
        BackgroundTaskState.PartialSuccess => LocalizationManager.Get("BackgroundTaskStatePartialSuccess"),
        BackgroundTaskState.Failed => LocalizationManager.Get("BackgroundTaskStateFailed"),
        BackgroundTaskState.Canceled => LocalizationManager.Get("BackgroundTaskStateCanceled"),
        _ => _snapshot.State.ToString()
    };

    public string StatusText => _snapshot.StatusText;

    public string ProgressText => FormatProgress(_snapshot.Progress);

    public string StartedAtText => _snapshot.StartedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    public string FinishedAtText => _snapshot.FinishedAt?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? string.Empty;

    public string ErrorText => string.IsNullOrWhiteSpace(_snapshot.ErrorMessage)
        ? string.Empty
        : string.IsNullOrWhiteSpace(_snapshot.ErrorType)
            ? _snapshot.ErrorMessage
            : $"{_snapshot.ErrorType}: {_snapshot.ErrorMessage}";

    public bool CanCancel => _snapshot.State == BackgroundTaskState.Running && _snapshot.IsCancelable;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(BackgroundTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StartedAtText));
        OnPropertyChanged(nameof(FinishedAtText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(CanCancel));
    }

    private static string FormatProgress(BackgroundTaskProgress progress)
    {
        if (progress.IsIndeterminate)
        {
            return LocalizationManager.Get("BackgroundTaskProgressIndeterminate");
        }

        var unit = progress.UnitLabel;
        if (progress.TotalUnits is long totalUnits)
        {
            return string.IsNullOrWhiteSpace(unit)
                ? string.Format(CultureInfo.CurrentCulture, "{0}/{1}", progress.CompletedUnits, totalUnits)
                : string.Format(CultureInfo.CurrentCulture, "{0}/{1} {2}", progress.CompletedUnits, totalUnits, unit);
        }

        return string.IsNullOrWhiteSpace(unit)
            ? progress.CompletedUnits.ToString(CultureInfo.CurrentCulture)
            : string.Format(CultureInfo.CurrentCulture, "{0} {1}", progress.CompletedUnits, unit);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
