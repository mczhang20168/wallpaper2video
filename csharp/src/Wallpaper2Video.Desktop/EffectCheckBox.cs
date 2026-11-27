using System.Windows.Controls;

namespace Wallpaper2Video.Desktop;

/// <summary>Only input toggles edit the scene; binding refreshes never write back.
/// OnToggle is also used by the UI Automation Toggle pattern.</summary>
public sealed class EffectCheckBox : CheckBox
{
    protected override void OnToggle()
    {
        if (DataContext is EffectRow row)
        {
            if (IsEnabled && row.ToggleCommand.CanExecute(null)) row.ToggleCommand.Execute(null);
            GetBindingExpression(IsCheckedProperty)?.UpdateTarget();
        }
        else if (DataContext is SourceLayerRow source)
        { if (IsEnabled) source.Toggle(); GetBindingExpression(IsCheckedProperty)?.UpdateTarget(); }
        else base.OnToggle();
    }
}
