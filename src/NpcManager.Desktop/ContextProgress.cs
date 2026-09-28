namespace NpcManager.Desktop;

internal sealed class ContextProgress<T>(
    SynchronizationContext? context,
    Action<T> present) : IProgress<T>
{
    public void Report(T value)
    {
        if (context is null || ReferenceEquals(
                SynchronizationContext.Current, context))
        {
            present(value);
            return;
        }

        context.Post(static state =>
        {
            var (callback, item) = ((Action<T>, T))state!;
            callback(item);
        }, (present, value));
    }
}
