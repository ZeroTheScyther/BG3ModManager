# Vendored: Avalonia.Controls.TreeDataGrid

Copied from https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid at commit c11d4a3183162075879ebdfd806ce35e6e1d9232
(the open-source repository was archived in 2025; newer NuGet releases are commercial).
MIT licensed, see licence.md. Only the library, its build props and licence are kept.

Local changes for stable Avalonia 12 (12.1.x):
- OnLostFocus/OnGotFocus take FocusChangedEventArgs.
- Avalonia.Utilities.MathUtilities is internal: local copy in Utils/MathUtilities.cs.
- TopLevel.PlatformSettings is not public: use Application.Current.PlatformSettings.
- DragDrop.DoDragDropAsync needs the PointerPressedEventArgs of the gesture: rows remember it and
  TreeDataGridRowDragStartedEventArgs exposes it as PressedEvent.
