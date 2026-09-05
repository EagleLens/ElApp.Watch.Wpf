using System.Windows;
using System.Windows.Media.Animation;

namespace ElApp.Watch.Wpf.Views;

/// <summary>
/// Shown by App.xaml.cs for the (brief but non-instant) span between process start and the main window
/// appearing - covers the internet connectivity check and host/DI startup, both of which would otherwise
/// leave the user staring at nothing. Not resolved through DI: it exists before the host is built.
/// </summary>
public partial class SplashWindow : Window
{
    private const double HighlightWidth = 70;

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartProgressAnimation();
    }

    /// <summary>Updates the status line shown under the progress bar.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// A looping highlight sweeping across the track, standing in for real progress (there's no
    /// meaningful percentage to report for "check internet" + "build DI host") - travel distance is the
    /// track's actual width, only known once it's laid out.
    /// </summary>
    private void StartProgressAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = -HighlightWidth,
            To = ProgressTrack.ActualWidth,
            Duration = TimeSpan.FromSeconds(1.1),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ProgressTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }
}
