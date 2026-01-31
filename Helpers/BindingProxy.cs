using System.Windows;

namespace CodeBridge.Helpers;

/// <summary>
/// BindingProxy - 用于解决 DataContext 无法跨视觉树传递的问题
/// 特别适用于 Popup、ContextMenu、Drawer 等独立视觉树的场景
/// </summary>
public class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore()
    {
        return new BindingProxy();
    }

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(
            nameof(Data),
            typeof(object),
            typeof(BindingProxy),
            new UIPropertyMetadata(null));
}
