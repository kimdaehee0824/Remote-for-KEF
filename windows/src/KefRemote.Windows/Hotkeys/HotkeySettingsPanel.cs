namespace KefRemote.Windows;

/// <summary>
/// Settings section for global shortcuts and the volume flyout. Changes apply immediately (no Save button):
/// valid chords are registered at once and <see cref="Changed"/> is raised so the host can persist them.
/// </summary>
public sealed class HotkeySettingsPanel : UserControl
{
    static readonly int[] StepChoices = [1, 2, 3, 5];

    readonly HotkeyManager manager;
    readonly Dictionary<HotkeyAction, HotkeyBox> boxes = [];
    readonly Dictionary<HotkeyAction, Label> states = [];
    readonly ComboBox step = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly CheckBox showOsd = new() { Text = "음량을 바꿀 때 화면 오른쪽 아래에 표시", AutoSize = true };
    readonly Label problem = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    List<HotkeyBinding> bindings = HotkeyBinding.Defaults();
    bool loading;

    /// <summary>Raised after a valid change. Persist <see cref="Bindings"/>, <see cref="VolumeStep"/> and <see cref="OsdEnabled"/>.</summary>
    public event Action? Changed;

    public IReadOnlyList<HotkeyBinding> Bindings => bindings;
    public int VolumeStep => StepChoices[Math.Max(0, step.SelectedIndex)];
    public bool OsdEnabled => showOsd.Checked;

    public HotkeySettingsPanel(HotkeyManager manager)
    {
        this.manager = manager;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(table);

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var box = new HotkeyBox { Dock = DockStyle.Fill, AccessibleName = HotkeyBinding.ActionName(action) + " 단축키" };
            var captured = action;
            box.Validator = (mods, key) => Conflict(captured, mods, key);
            box.ChordChanged += () => OnChord(captured, box);
            box.ChordRejected += ShowProblem;
            box.Enter += (_, _) => manager.Suspend();
            box.Leave += (_, _) =>
            {
                // Focus may move straight to another HotkeyBox; only resume once none has it.
                BeginInvoke(() =>
                {
                    if (!boxes.Values.Any(b => b.Focused))
                    {
                        manager.Resume();
                        ShowStatus();
                    }
                });
            };
            var state = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
            boxes[action] = box;
            states[action] = state;

            table.Controls.Add(new Label { Text = HotkeyBinding.ActionName(action), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            table.Controls.Add(box);
            table.Controls.Add(state);
        }

        foreach (var n in StepChoices) step.Items.Add(n == 1 ? "1씩 (가장 세밀하게)" : $"{n}씩");
        step.SelectedIndexChanged += (_, _) => Raise();
        showOsd.CheckedChanged += (_, _) => Raise();

        var stepRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        stepRow.Controls.Add(new Label { Text = "한 번 누를 때 음량", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
        stepRow.Controls.Add(step);
        table.Controls.Add(stepRow);
        table.SetColumnSpan(stepRow, 3);

        table.Controls.Add(showOsd);
        table.SetColumnSpan(showOsd, 3);

        var reset = new Button { Text = "기본값으로", AutoSize = true };
        reset.Click += (_, _) => LoadSettings(HotkeyBinding.Defaults(), VolumeStep, OsdEnabled, raise: true);
        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = SystemColors.GrayText,
            Text = "칸을 클릭한 뒤 원하는 조합을 누르세요. Backspace는 지우기, Esc는 취소입니다. " +
                   "Ctrl·Alt·Shift 중 하나와 함께 누르거나, F13–F24·미디어 키는 단독으로 쓸 수 있습니다. 길게 누르면 계속 바뀝니다.",
        };
        table.Controls.Add(reset);
        table.Controls.Add(hint);
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(problem);
        table.SetColumnSpan(problem, 3);
    }

    /// <summary>Fills the panel from saved settings and registers the shortcuts.</summary>
    public void LoadSettings(IEnumerable<HotkeyBinding>? saved, int volumeStep, bool osdEnabled, bool raise = false)
    {
        loading = true;
        bindings = HotkeyBinding.Normalize(saved);
        foreach (var b in bindings) boxes[b.Action].SetChord(b.Modifiers, b.Key);
        int index = Array.IndexOf(StepChoices, volumeStep);
        step.SelectedIndex = index >= 0 ? index : 0;
        showOsd.Checked = osdEnabled;
        loading = false;
        HideProblem();
        manager.Apply(bindings);
        ShowStatus();
        if (raise) Changed?.Invoke();
    }

    string? Conflict(HotkeyAction action, HotkeyModifiers mods, int key)
    {
        var other = bindings.FirstOrDefault(b => b.Action != action && b.IsAssigned && b.Modifiers == mods && b.Key == key);
        return other == null ? null : $"이미 '{HotkeyBinding.ActionName(other.Action)}'에 쓰고 있습니다";
    }

    void OnChord(HotkeyAction action, HotkeyBox box)
    {
        var next = bindings.Select(b => b.Action == action ? new HotkeyBinding(action, box.Modifiers, box.Key) : b.Clone()).ToList();
        var error = HotkeyBinding.ValidateSet(next);
        if (error != null)
        {
            ShowProblem(error);
            return;
        }
        HideProblem();
        bindings = next;
        manager.Apply(bindings); // stored now, registered when capture ends
        ShowStatus();
        Raise();
    }

    void Raise()
    {
        if (!loading) Changed?.Invoke();
    }

    void ShowStatus()
    {
        foreach (var (action, label) in states)
        {
            var status = manager.Status.TryGetValue(action, out var s) ? s : HotkeyRegistration.Off;
            (label.Text, label.ForeColor) = status switch
            {
                HotkeyRegistration.Registered => ("사용 중", SystemColors.GrayText),
                HotkeyRegistration.InUse => ("다른 앱이 이미 사용 중", Color.Firebrick),
                _ => ("꺼짐", SystemColors.GrayText),
            };
        }
    }

    void ShowProblem(string text)
    {
        problem.Text = text;
        problem.Visible = true;
    }

    void HideProblem() => problem.Visible = false;

    Form? hostForm;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hostForm = FindForm();
        if (hostForm == null) return;
        // Capturing only makes sense while the settings window is in front. If the user switches away or hides
        // the window with a capture box focused, the shortcuts must work again.
        hostForm.Deactivate += OnHostDeactivate;
        hostForm.Activated += OnHostActivated;
    }

    void OnHostDeactivate(object? sender, EventArgs e)
    {
        manager.Resume();
        ShowStatus();
    }

    void OnHostActivated(object? sender, EventArgs e)
    {
        if (boxes.Values.Any(b => b.Focused)) manager.Suspend();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hostForm != null)
        {
            hostForm.Deactivate -= OnHostDeactivate;
            hostForm.Activated -= OnHostActivated;
        }
        manager.Resume();
        base.OnHandleDestroyed(e);
    }
}
