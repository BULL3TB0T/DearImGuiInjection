namespace DearImGuiInjection;

public interface IConfigEntry<T>
{
    public T GetValue();
}