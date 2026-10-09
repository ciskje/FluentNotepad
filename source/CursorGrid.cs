using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FluentNotepad;

/// <summary>
/// Grid che espone il cursore di sistema. UIElement.ProtectedCursor e' protected,
/// quindi serve una sottoclasse per poterlo assegnare (pattern indicato da Microsoft).
/// </summary>
public sealed class CursorGrid : Grid
{
    public void SetBusy(bool busy) =>
        ProtectedCursor = busy ? InputSystemCursor.Create(InputSystemCursorShape.Wait) : null;
}
