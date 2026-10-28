using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KefRemote.Windows;

/// <summary>
/// Read-only text box that records the next key chord pressed while it has focus.
/// Tab / Shift+Tab still move focus, Esc restores the previous chord, Backspace or Delete clears it.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    HotkeyModifiers modifiers;
    int key;
    HotkeyModifiers committedModifiers;
    int committedKey;

    /// <summary>Raised with the new chord. Return an error message to reject it, or null to accept.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<HotkeyModifiers, int, string?>? Validator { get; set; }

    public event Action? ChordChanged;
    public event Action<string>? ChordRejected;

    public HotkeyModifiers Modifiers => modifiers;
    public int Key => key;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        BackColor = SystemColors.Window;
        TextAlign = HorizontalAlignment.Center;
    }

    public void SetChord(HotkeyModifiers newModifiers, int newKey)
    {
        modifiers = committedModifiers = newModifiers;
        key = committedKey = newKey;
        ShowChord();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Text = "단축키를 누르세요";
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        ShowChord();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        SelectionLength = 0;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        var mods = Current(keyData);

        if (code == Keys.Tab && (mods & ~HotkeyModifiers.Shift) == HotkeyModifiers.None)
            return base.ProcessCmdKey(ref msg, keyData);

        if (VirtualKeys.IsModifier((int)code))
        {
            Text = Preview(mods);
            return true;
        }

        if (mods == HotkeyModifiers.None)
        {
            if (code == Keys.Escape)
            {
                modifiers = committedModifiers;
                key = committedKey;
                ShowChord();
                return true;
            }
            if (code is Keys.Back or Keys.Delete)
            {
                Commit(HotkeyModifiers.None, 0);
                return true;
            }
        }

        string? error = Validator?.Invoke(mods, (int)code)
            ?? new HotkeyBinding(HotkeyAction.VolumeUp, mods, (int)code).Validate();
        if (error != null)
        {
            Text = new HotkeyBinding(HotkeyAction.VolumeUp, mods, (int)code).ToString();
            ChordRejected?.Invoke(error);
            return true;
        }
        Commit(mods, (int)code);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Releasing modifiers without completing a chord: go back to the prompt.
        if (Focused && VirtualKeys.IsModifier((int)e.KeyCode))
        {
            var mods = Current(e.Modifiers);
            Text = mods == HotkeyModifiers.None ? "단축키를 누르세요" : Preview(mods);
        }
        e.Handled = true;
    }

    void Commit(HotkeyModifiers newModifiers, int newKey)
    {
        modifiers = committedModifiers = newModifiers;
        key = committedKey = newKey;
        ShowChord();
        ChordChanged?.Invoke();
    }

    void ShowChord()
    {
        Text = new HotkeyBinding(HotkeyAction.VolumeUp, modifiers, key).ToString();
        SelectionLength = 0;
    }

    static string Preview(HotkeyModifiers mods)
    {
        var text = new HotkeyBinding(HotkeyAction.VolumeUp, mods, VirtualKeys.Space).ToString();
        return text[..text.LastIndexOf('+')] + "+ …";
    }

    static HotkeyModifiers Current(Keys keyData)
    {
        var mods = HotkeyModifiers.None;
        if (keyData.HasFlag(Keys.Control)) mods |= HotkeyModifiers.Control;
        if (keyData.HasFlag(Keys.Alt)) mods |= HotkeyModifiers.Alt;
        if (keyData.HasFlag(Keys.Shift)) mods |= HotkeyModifiers.Shift;
        // Keys has no Win modifier flag.
        if ((GetKeyState(VirtualKeys.LWin) & 0x8000) != 0 || (GetKeyState(VirtualKeys.RWin) & 0x8000) != 0) mods |= HotkeyModifiers.Win;
        return mods;
    }

    [DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);
}
