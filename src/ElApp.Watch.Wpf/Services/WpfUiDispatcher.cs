using ElApp.Watch.Wpf.Services.Interface;
using System.Windows.Threading;

namespace ElApp.Watch.Wpf.Services;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void BeginInvoke(Action action) => dispatcher.BeginInvoke(action);
}
