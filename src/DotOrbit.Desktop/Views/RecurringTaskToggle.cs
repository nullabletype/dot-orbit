using Avalonia.Controls.Primitives;

namespace DotOrbit.Desktop.Views;

/// <summary>Requests a change through its command; checked state follows the committed definition.</summary>
public sealed class RecurringTaskToggle : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);
    protected override void Toggle()
    {
        // A guarded, pending, or failed change must not alter the persisted-state indicator.
    }
}
