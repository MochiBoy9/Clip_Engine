using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The "Your in-game name" card on the Games page.
public sealed class PlayerNamesViewModel : ObservableObject
{
    private readonly string _file;
    private string _names;
    private string _saved;
    private string _message = "";

    internal PlayerNamesViewModel(string file)
    {
        _file = file;
        _saved = _names = string.Join(", ", Load(file));
        SaveCommand = new RelayCommand(Save, () => HasChanges);
    }

    public string Names
    {
        get => _names;
        set
        {
            if (!Set(ref _names, value)) return;
            Message = "";
            Raise(nameof(HasChanges));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasChanges => _names.Trim() != _saved;

    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message.Length > 0;

    public RelayCommand SaveCommand { get; }

    private void Save()
    {
        var names = PlayerNames.Split(_names);
        try
        {
            PlayerNames.Save(_file, names);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't save: {e.Message}";
            return;
        }
        _saved = _names = string.Join(", ", names);
        Raise(nameof(Names));
        Raise(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
        Message = names.Count == 0 ? "Cleared." : "Saved. It's used from the next time a game starts.";
    }

    private static IReadOnlyList<string> Load(string file)
    {
        try
        {
            return PlayerNames.Load(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
