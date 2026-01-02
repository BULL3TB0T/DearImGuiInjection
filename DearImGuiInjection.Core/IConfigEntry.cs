namespace DearImGuiInjection;

public interface IConfigEntry<T>
{
    public T GetValue();
    public T SetValue(T value);
}