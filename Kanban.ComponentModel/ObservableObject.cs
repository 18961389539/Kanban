using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kanban.ComponentModel;

/// <summary>
/// 最小属性变更通知。采集模型仍要被 WPF 绑定，但不引用 CommunityToolkit.Mvvm。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        => SetProperty(ref field, value, alsoNotify: null, propertyName);

    protected bool SetProperty<T>(ref T field, T value, string[]? alsoNotify, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (alsoNotify is not null)
        {
            foreach (var name in alsoNotify)
                OnPropertyChanged(name);
        }
        return true;
    }
}
