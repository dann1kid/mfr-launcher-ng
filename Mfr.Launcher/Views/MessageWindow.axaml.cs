using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;

namespace Mfr.Launcher.Views;

/// <summary>
/// Skinned modal dialog (Kotlin: NotificationController/QuestionController).
/// With HasCancel=true it acts as a question and returns true only for Ок.
/// </summary>
public partial class MessageWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    /// <summary>Banner title (named differently from Window.Title to avoid clashing).</summary>
    public string HeaderTitle { get; }

    public string Description { get; }

    public bool HasCancel { get; }

    public RelayCommand OkCommand { get; }

    public RelayCommand CancelCommand { get; }

    public MessageWindow(string title, string description, bool hasCancel = false)
    {
        HeaderTitle = title;
        Description = description;
        HasCancel = hasCancel;
        OkCommand = new RelayCommand(() =>
        {
            _result.TrySetResult(true);
            Close();
        });
        CancelCommand = new RelayCommand(() =>
        {
            _result.TrySetResult(false);
            Close();
        });
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        // window closed without pressing a button (e.g. alt+f4) → treat as cancel
        if (!_result.Task.IsCompleted)
        {
            _result.TrySetResult(false);
        }
        base.OnClosing(args);
    }

    public Task<bool> ShowDialog(Window owner)
    {
        _ = base.ShowDialog(owner);
        return _result.Task;
    }
}
