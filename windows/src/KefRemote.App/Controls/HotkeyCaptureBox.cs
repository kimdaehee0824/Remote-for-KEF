using KefRemote.App.Native;
using KefRemote.Desktop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace KefRemote.App.Controls;

/// <summary>
/// A button that shows a shortcut and records a new one: click, press the chord. Esc cancels, Backspace or
/// Delete turns the shortcut off. Global hotkeys are released while recording so the current chord reaches it.
/// </summary>
sealed class HotkeyCaptureBox
{
    readonly Button button;
    readonly HotkeyManager hotkeys;
    HotkeyBinding binding;
    bool capturing;

    /// <summary>The recorded binding. Key 0 means turned off. Not validated yet.</summary>
    public event Action<HotkeyBinding>? Captured;

    public FrameworkElement View => button;

    public HotkeyCaptureBox(HotkeyBinding binding, HotkeyManager hotkeys)
    {
        this.binding = binding.Clone();
        this.hotkeys = hotkeys;
        button = new Button { MinWidth = 180, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(button, HotkeyBinding.ActionName(binding.Action) + " 단축키");
        ToolTipService.SetToolTip(button, "눌러서 새 키 조합 지정. Esc 취소, Backspace 끄기");
        button.Click += (_, _) => Start();
        button.PreviewKeyDown += OnPreviewKeyDown;
        button.LostFocus += (_, _) => Cancel();
        Show();
    }

    public void SetBinding(HotkeyBinding next)
    {
        binding = next.Clone();
        if (!capturing) Show();
    }

    void Start()
    {
        if (capturing) return;
        capturing = true;
        hotkeys.Suspend();
        button.Content = "키 조합을 누르세요";
    }

    void Cancel()
    {
        if (!capturing) return;
        capturing = false;
        hotkeys.Resume();
        Show();
    }

    void Show() => button.Content = binding.ToString();

    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!capturing) return;
        e.Handled = true;
        int vk = (int)e.Key;
        var modifiers = CurrentModifiers();
        if (e.Key == VirtualKey.Escape && modifiers == HotkeyModifiers.None)
        {
            Cancel();
            return;
        }
        if (VirtualKeys.IsModifier(vk))
        {
            // Show the modifiers held so far, e.g. "Ctrl + Alt + ...".
            var partial = new HotkeyBinding(binding.Action, modifiers, VirtualKeys.Space).ToString();
            button.Content = partial[..partial.LastIndexOf(" + ", StringComparison.Ordinal)] + " + ...";
            return;
        }
        if (e.Key is VirtualKey.Back or VirtualKey.Delete && modifiers == HotkeyModifiers.None)
        {
            Finish(new HotkeyBinding(binding.Action, HotkeyModifiers.None, 0));
            return;
        }
        Finish(new HotkeyBinding(binding.Action, modifiers, vk));
    }

    void Finish(HotkeyBinding next)
    {
        capturing = false;
        hotkeys.Resume();
        Captured?.Invoke(next);
        Show();
    }

    static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var result = HotkeyModifiers.None;
        if (Down(VirtualKey.Control)) result |= HotkeyModifiers.Control;
        if (Down(VirtualKey.Menu)) result |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) result |= HotkeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) result |= HotkeyModifiers.Win;
        return result;
    }
}
