namespace Orbitale.Engine.Tests.Support;

/// <summary>An <see cref="IProgress{T}"/> that invokes its handler synchronously on the reporting thread (unlike <see cref="Progress{T}"/>).</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
