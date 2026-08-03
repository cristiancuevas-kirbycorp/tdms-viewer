using System.Windows;
using System.Windows.Controls;
using TdmsViewer.Models;

namespace TdmsViewer;

/// <summary>Lets the user choose which between-cursor calculations appear in the readout.</summary>
public partial class CursorCalcWindow : Window
{
    private readonly CursorCalcSettings _settings;
    private readonly List<CheckBox> _boxes = new();

    public CursorCalcWindow(CursorCalcSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var (key, _, label) in CursorCalcSettings.Columns)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = settings.IsEnabled(key),
                Margin = new Thickness(0, 4, 0, 4),
                Tag = key,
            };
            _boxes.Add(cb);
            CalcList.Children.Add(cb);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _boxes)
            _settings.Set((string)cb.Tag, cb.IsChecked == true);
        DialogResult = true;
    }
}
