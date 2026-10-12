using System.Windows;
using System.Windows.Controls;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App.Views;

public partial class MessageDialogWindow : Window
{
    private string _resultKey = "close";

    public MessageDialogWindow(DialogRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;

        var (glyph, tone) = request.Icon switch
        {
            DialogIcon.Success => ("✔", StatusTone.Ok),
            DialogIcon.Warning => ("!", StatusTone.Warn),
            DialogIcon.Error => ("✖", StatusTone.Danger),
            DialogIcon.Question => ("?", StatusTone.Accent),
            _ => ("i", StatusTone.Accent),
        };
        IconGlyph.Text = glyph;
        Ui.SetTone(IconBox, tone);
        Ui.SetTone(IconGlyph, tone);

        if (!string.IsNullOrWhiteSpace(request.Details))
        {
            DetailsText.Text = request.Details;
            DetailsExpander.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(request.CheckBoxText))
        {
            Check.Content = request.CheckBoxText;
            Check.Visibility = Visibility.Visible;
        }

        var cancel = request.Buttons.FirstOrDefault(b => b.IsCancel);
        if (cancel != null) _resultKey = cancel.Key;

        Button? defaultButton = null;
        foreach (var b in request.Buttons)
        {
            var key = b.Key;
            var button = new Button
            {
                Content = b.Text,
                IsDefault = b.IsDefault,
                IsCancel = b.IsCancel,
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 96,
                Style = (Style)FindResource(b.IsDanger ? "DangerButton" : b.IsDefault ? "PrimaryButton" : typeof(Button)),
            };
            button.Click += (_, _) => { _resultKey = key; DialogResult = true; };
            ButtonBar.Children.Add(button);
            if (b.IsDefault) defaultButton = button;
        }
        // Esc always closes the dialog; without an explicit cancel button it answers "close" (no action taken).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && cancel == null) { DialogResult = false; e.Handled = true; }
        };
        Loaded += (_, _) => (defaultButton ?? ButtonBar.Children.OfType<Button>().FirstOrDefault())?.Focus();
        SourceInitialized += (_, _) => WpfThemeService.ApplyTitleBar(this);
    }

    public DialogResponse Response => new(_resultKey, Check.IsChecked == true);
}

