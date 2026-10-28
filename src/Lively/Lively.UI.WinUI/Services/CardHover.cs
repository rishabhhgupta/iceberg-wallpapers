using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// Small helper for the "Set wallpaper" hint that appears when the pointer
    /// rests on a wallpaper card. The hint is a child of each card template, so
    /// it is found by name instead of being bound to a property.
    /// </summary>
    public static class CardHover
    {
        public static void Show(object sender, double opacity)
        {
            if (sender is DependencyObject root
                && FindChild(root, "HoverLayer") is UIElement layer)
            {
                layer.Opacity = opacity;
            }
        }

        private static FrameworkElement FindChild(DependencyObject root, string name)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element && element.Name == name)
                {
                    return element;
                }

                var found = FindChild(child, name);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
