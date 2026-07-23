using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MdPad;

/// <summary>
/// One open document — one tab. The editor and preview are shared controls that
/// swap between documents, so everything that must survive a tab switch lives here.
/// </summary>
public sealed class MdDocument : INotifyPropertyChanged
{
    private string? _path;
    private bool _isDirty;

    /// <summary>Current editor contents, refreshed as the user types and when a tab is left.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Contents as last loaded or saved, newline-normalised, for dirty comparison.</summary>
    public string SavedText { get; set; } = string.Empty;

    /// <summary>Caret offset, so returning to a tab lands where the user left off.</summary>
    public int SelectionStart { get; set; }

    /// <summary>Preview scroll offset, restored on tab switch.</summary>
    public double PreviewOffset { get; set; }

    public string? Path
    {
        get => _path;
        set
        {
            _path = value;
            Notify();
            Notify(nameof(Name));
            Notify(nameof(Header));
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
            {
                return;
            }
            _isDirty = value;
            Notify();
            Notify(nameof(Header));
        }
    }

    public string Name => _path is null ? "Untitled" : System.IO.Path.GetFileName(_path);

    /// <summary>Tab caption: the file name, marked when there are unsaved changes.</summary>
    public string Header => _isDirty ? Name + " •" : Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
