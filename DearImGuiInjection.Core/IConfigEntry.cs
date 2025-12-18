namespace DearImGuiInjection;

internal interface IConfigEntry<T>
{
    public T Get();
    public void Set(T value);
}