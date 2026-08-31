using System.Windows;

namespace ElApp.Watch.Wpf.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml. All state and behavior lives in MainViewModel and its
/// bound PumpTileViewModel tiles, resolved and disposed through the DI container in App.xaml.cs -
/// this code-behind has nothing left to do beyond InitializeComponent.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
