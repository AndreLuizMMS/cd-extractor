using System.Windows;
using System.Windows.Controls;
using MediaRescue.App.Infrastructure;

namespace MediaRescue.App.Views;

public partial class NamePromptWindow : Window
{
    public NamePromptWindow(NamePromptRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        NameBox.Text = request.SuggestedName;
        DontAsk.Visibility = request.OfferDontAsk ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        SourceInitialized += (_, _) => WpfThemeService.ApplyTitleBar(this);
    }

    public NamePromptResult? Result { get; private set; }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new NamePromptResult(NameBox.Text.Trim(), DontAsk.IsChecked == true);
        DialogResult = true;
    }
}
