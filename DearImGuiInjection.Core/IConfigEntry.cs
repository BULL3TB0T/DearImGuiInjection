namespace DearImGuiInjection;

internal interface IConfigEntry<T>
{
    public T GetValue();
}