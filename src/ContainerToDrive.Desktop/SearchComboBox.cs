using System.Windows;
using System.Windows.Controls;

namespace ContainerToDrive.Desktop;

public sealed class SearchComboBox : ComboBox, IDisposable
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(SearchComboBox));
    public static readonly DependencyProperty SearchStatusProperty = DependencyProperty.Register(nameof(SearchStatus), typeof(string), typeof(SearchComboBox), new PropertyMetadata(""));
    public static readonly DependencyProperty HasSearchStatusProperty = DependencyProperty.Register(nameof(HasSearchStatus), typeof(bool), typeof(SearchComboBox));
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchComboBox), new PropertyMetadata("Search"));
    public static readonly DependencyProperty FailureMessageProperty = DependencyProperty.Register(nameof(FailureMessage), typeof(string), typeof(SearchComboBox), new PropertyMetadata("Results could not be loaded. Refresh to retry."));
    private readonly AsyncSearch _search = new();
    private bool _updating;
    private bool _hasSource;

    public SearchComboBox()
    {
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        IsEnabled = false;
        _search.Changed += OnSearchChanged;
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnEditorTextChanged));
    }

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); private set => SetValue(IsLoadingProperty, value); }
    public string SearchStatus { get => (string)GetValue(SearchStatusProperty); private set => SetValue(SearchStatusProperty, value); }
    public bool HasSearchStatus { get => (bool)GetValue(HasSearchStatusProperty); private set => SetValue(HasSearchStatusProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string FailureMessage { get => (string)GetValue(FailureMessageProperty); set => SetValue(FailureMessageProperty, value); }
    public bool HasSearchError => _search.HasError;
    public event EventHandler? SearchStateChanged;

    internal void SetSource(Func<string, CancellationToken, Task<IReadOnlyList<object>>>? source)
    {
        _updating = true;
        try
        {
            _hasSource = source is not null;
            IsDropDownOpen = false;
            SelectedItem = null;
            Text = "";
            _search.SetSource(source);
            IsEnabled = _hasSource;
        }
        finally { _updating = false; }
    }

    internal Task RefreshAsync(bool open = false)
    {
        var task = _search.RunAsync(Text, TimeSpan.Zero);
        if (open && IsEnabled) IsDropDownOpen = true;
        return task;
    }

    internal void ClearSelection()
    {
        _updating = true;
        try { SelectedItem = null; Text = ""; IsDropDownOpen = false; }
        finally { _updating = false; }
        _ = _search.RunAsync("", TimeSpan.Zero);
    }

    internal void CancelSearch() { if (_search.IsLoading) _search.Cancel(); }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        if (_hasSource && !_search.IsLoading && _search.Items.Count == 0 && _search.Status.Length == 0)
            _ = RefreshAsync();
    }

    private async void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !_hasSource || e.OriginalSource is not TextBox { Name: "PART_EditableTextBox", IsKeyboardFocusWithin: true } editor) return;
        var query = editor.Text;
        if (SelectedItem is { } selected && string.Equals(selected.ToString(), query, StringComparison.Ordinal)) return;
        var caret = editor.CaretIndex;
        _updating = true;
        try
        {
            SelectedItem = null;
            Text = query;
            editor.CaretIndex = Math.Min(caret, editor.Text.Length);
        }
        finally { _updating = false; }
        var task = _search.RunAsync(query);
        IsDropDownOpen = true;
        await task;
    }

    private void OnSearchChanged()
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var text = Text;
            if (!ReferenceEquals(ItemsSource, _search.Items)) ItemsSource = _search.Items;
            Text = text;
            IsLoading = _search.IsLoading;
            SearchStatus = _search.HasError ? FailureMessage : _search.Status;
            HasSearchStatus = !IsLoading && SearchStatus.Length != 0;
        }
        finally { _updating = wasUpdating; }
        SearchStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _search.Changed -= OnSearchChanged;
        _search.Dispose();
    }
}