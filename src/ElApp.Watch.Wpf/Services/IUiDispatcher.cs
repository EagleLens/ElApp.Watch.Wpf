namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Thin abstraction over <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate)"/>
/// so background-thread code (capture loops, ONNX inference callbacks) can marshal a UI-bound
/// property update onto the UI thread without taking a direct dependency on WPF's Dispatcher type.
/// Fire-and-forget, matching the original code's Dispatcher.BeginInvoke usage throughout.
/// </summary>
public interface IUiDispatcher
{
    void BeginInvoke(Action action);
}
