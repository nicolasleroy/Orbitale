using Microsoft.UI.Dispatching;

namespace Orbitale.App.Services;

/// <summary><see cref="IUiDispatcher"/> over a window's <see cref="DispatcherQueue"/>.</summary>
public sealed class DispatcherQueueUi(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());
}
