using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Shell;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App.Views;

/// <summary>double -> Thickness(left, 0, 0, 0): indentation of tree rows.</summary>
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true -> Collapsed, false -> Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Logical NOT for boolean bindings.</summary>
public sealed class BoolNotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>0 -> Visible, anything else -> Collapsed (empty-list hints).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Non-empty string / non-null object -> Visible, otherwise Collapsed.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch { null => Visibility.Collapsed, string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible, _ => Visibility.Visible };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Empty string / null -> Visible (used for "nothing here" hints).</summary>
public sealed class IsEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch { null => Visibility.Visible, string s => string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed, _ => Visibility.Collapsed };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProgressVisualToTaskbarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ProgressVisual v
            ? v switch
            {
                ProgressVisual.Indeterminate => TaskbarItemProgressState.Indeterminate,
                ProgressVisual.Normal => TaskbarItemProgressState.Normal,
                ProgressVisual.Paused => TaskbarItemProgressState.Paused,
                ProgressVisual.Error => TaskbarItemProgressState.Error,
                _ => TaskbarItemProgressState.None,
            }
            : TaskbarItemProgressState.None;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ProgressVisual.Error -> true (selects the red progress bar style).</summary>
public sealed class IsErrorVisualConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ProgressVisual.Error;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}


