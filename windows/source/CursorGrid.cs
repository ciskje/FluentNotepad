using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FluentNotepad;

/// <summary>
/// Grid exposing the system cursor. UIElement.ProtectedCursor is protected,
/// so a subclass is needed to assign it (Microsoft-recommended pattern).
/// </summary>
public sealed class CursorGrid : Grid
{
    public void SetBusy(bool busy) =>
        ProtectedCursor = busy ? InputSystemCursor.Create(InputSystemCursorShape.Wait) : null;
}
