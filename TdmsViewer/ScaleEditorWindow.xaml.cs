using System.Globalization;
using System.Windows;
using TdmsViewer.ViewModels;

namespace TdmsViewer;

/// <summary>Edits a Y-scale's name and min/max; opened by double-clicking the axis on the plot.</summary>
public partial class ScaleEditorWindow : Window
{
    private readonly AxisViewModel _scale;

    public ScaleEditorWindow(AxisViewModel scale, bool focusName)
    {
        InitializeComponent();
        _scale = scale;

        NameBox.Text = scale.Name;
        AutoBox.IsChecked = scale.Auto;
        MinBox.Text = scale.Min.ToString("G6", CultureInfo.CurrentCulture);
        MaxBox.Text = scale.Max.ToString("G6", CultureInfo.CurrentCulture);
        UpdateMinMaxEnabled();

        Loaded += (_, _) =>
        {
            if (focusName) { NameBox.Focus(); NameBox.SelectAll(); }
            else { MinBox.Focus(); MinBox.SelectAll(); }
        };
    }

    private void Auto_Changed(object sender, RoutedEventArgs e) => UpdateMinMaxEnabled();

    private void UpdateMinMaxEnabled()
    {
        var manual = AutoBox.IsChecked != true;
        MinBox.IsEnabled = manual;
        MaxBox.IsEnabled = manual;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            _scale.Name = NameBox.Text.Trim();

        if (AutoBox.IsChecked == true)
        {
            _scale.Auto = true;
        }
        else if (double.TryParse(MinBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var min)
                 && double.TryParse(MaxBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var max)
                 && max > min)
        {
            // Setting Min/Max flips the scale to manual (Auto = false).
            _scale.Min = min;
            _scale.Max = max;
        }

        DialogResult = true;
    }
}
