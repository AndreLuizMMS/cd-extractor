using System.Windows.Controls;
using System.Windows.Input;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App.Views;

public partial class ExtractView : UserControl
{
    public ExtractView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // In a small window the right column scrolls; when the "last extraction" card appears, bring it into view (it is the news).
    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ExtractViewModel old) { old.PropertyChanged -= OnVmChanged; old.Mp4.PropertyChanged -= OnMp4Changed; }
        if (e.NewValue is ExtractViewModel vm) { vm.PropertyChanged += OnVmChanged; vm.Mp4.PropertyChanged += OnMp4Changed; }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtractViewModel.HasResult)) ScrollResultIntoView();
    }

    private void OnMp4Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Mp4ConversionViewModel.IsActive)) ScrollResultIntoView();
    }

    private void ScrollResultIntoView()
    {
        if (DataContext is not ExtractViewModel { HasResult: true, Progress.IsActive: false, Mp4.IsActive: false }) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => RightScroll.ScrollToEnd()));
    }

    /// <summary>Keyboard support for the content list: Space toggles the check, Right/Left expand/collapse folders.</summary>
    private void ContentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ContentList.SelectedItem is not ContentItemViewModel item) return;
        switch (e.Key)
        {
            case Key.Space:
                item.IsChecked = item.IsChecked != true;
                e.Handled = true;
                break;
            case Key.Right when item.IsContainer && item.HasChildren && !item.IsExpanded:
                item.IsExpanded = true;
                e.Handled = true;
                break;
            case Key.Left when item.IsContainer && item.IsExpanded:
                item.IsExpanded = false;
                e.Handled = true;
                break;
        }
    }
}
