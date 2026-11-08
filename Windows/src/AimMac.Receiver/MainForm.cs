using AimMac.Core.Session;

namespace AimMac.Receiver;

internal sealed class MainForm : Form
{
    private readonly Win32PointerSink _pointer;
    private readonly ReceiverEngine _engine;
    private readonly BleServer _ble = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };

    private readonly Label _banner = new();
    private readonly Label _status = new();
    private readonly Label _rate = new();
    private readonly ListView _peers = new();
    private readonly TextBox _log = new();
    private readonly Button _start = new() { Text = "Start" };
    private readonly Button _stop = new() { Text = "Stop" };
    private readonly Button _arm = new() { Text = "Arm input" };
    private readonly Button _disarm = new() { Text = "Disarm" };
    private readonly Button _pause = new() { Text = "Pause" };
    private readonly Button _center = new() { Text = "Center" };
    private readonly Button _approve = new() { Text = "Approve" };
    private readonly Button _deny = new() { Text = "Deny" };
    private readonly Button _revoke = new() { Text = "Revoke" };
    private readonly Button _prototype = new() { Text = "Prototype mode…" };
    private readonly Button _secure = new() { Text = "Secure mode" };
    private readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _smooth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _invertX = new() { Text = "Invert left/right", AutoSize = true };
    private readonly CheckBox _invertY = new() { Text = "Invert up/down", AutoSize = true };

    private int _logLines;
    private int _ticks;

    public MainForm(Win32PointerSink pointer)
    {
        _pointer = pointer;
        var approvals = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AimMac",
            "secure-approvals.json");
        _engine = new ReceiverEngine(new JsonApprovalStore(approvals), new WinDisplays(), new SystemClock(), hostName: Environment.MachineName);

        Text = "AimMac Receiver";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        ClientSize = new Size(860, 640);
        Font = new Font("Segoe UI", 9f);

        _banner.Dock = DockStyle.Top;
        _banner.AutoSize = true;
        _banner.Padding = new Padding(10);
        _banner.MaximumSize = new Size(1200, 0);
        _status.Dock = DockStyle.Top;
        _status.AutoSize = true;
        _status.Padding = new Padding(10, 8, 10, 0);
        _rate.Dock = DockStyle.Top;
        _rate.AutoSize = true;
        _rate.Padding = new Padding(10, 0, 10, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            WrapContents = true,
        };
        foreach (var button in new[] { _start, _stop, _arm, _disarm, _pause, _center, _approve, _deny, _revoke, _prototype, _secure })
        {
            button.AutoSize = true;
            button.Margin = new Padding(4);
            buttons.Controls.Add(button);
        }

        _range.Items.AddRange(["Fast (small turns)", "Normal", "Precise (big turns)"]);
        _smooth.Items.AddRange(["Smoothing low", "Smoothing medium", "Smoothing high"]);
        _range.SelectedIndex = 1;
        _smooth.SelectedIndex = 1;
        _range.Margin = new Padding(12, 6, 4, 4);
        _smooth.Margin = new Padding(4, 6, 4, 4);
        buttons.Controls.Add(_range);
        buttons.Controls.Add(_smooth);
        buttons.Controls.Add(_invertX);
        buttons.Controls.Add(_invertY);

        _peers.Dock = DockStyle.Fill;
        _peers.View = View.Details;
        _peers.FullRowSelect = true;
        _peers.MultiSelect = false;
        _peers.Columns.Add("Device", 180);
        _peers.Columns.Add("Phase", 110);
        _peers.Columns.Add("Link", 160);
        _peers.Columns.Add("Hello label (not an identity)", 280);

        _log.Dock = DockStyle.Bottom;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Height = 160;

        Controls.Add(_peers);
        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(_rate);
        Controls.Add(_status);
        Controls.Add(_banner);

        _start.Click += async (_, _) => await Guard(StartAsync);
        _stop.Click += (_, _) => StopAll("Stopped from the window.");
        _arm.Click += (_, _) => Arm();
        _disarm.Click += (_, _) => Consume(_engine.Disarm());
        _pause.Click += (_, _) => Consume(_engine.SetPaused(!_engine.GlobalPaused));
        _center.Click += (_, _) => Consume(_engine.Center());
        _approve.Click += (_, _) => Approve();
        _deny.Click += (_, _) => ActOnSelection(_engine.Deny, "Select a device to deny.");
        _revoke.Click += (_, _) => Revoke();
        _prototype.Click += async (_, _) => await Guard(EnablePrototypeAsync);
        _secure.Click += async (_, _) => await Guard(EnableSecureAsync);
        _range.SelectedIndexChanged += (_, _) => ApplyTuning();
        _smooth.SelectedIndexChanged += (_, _) => ApplyTuning();
        _invertX.CheckedChanged += (_, _) => ApplyTuning();
        _invertY.CheckedChanged += (_, _) => ApplyTuning();
        _timer.Tick += (_, _) => OnTick();

        _ble.Subscribed += (_, id) => Post(() => OnSubscribed(id));
        _ble.Unsubscribed += (_, id) => Post(() => OnUnsubscribed(id));
        _ble.Written += (_, write) => Post(() => OnWritten(write));
        _ble.Faulted += (_, message) => Post(() =>
        {
            _pointer.Release();
            Log("Bluetooth fault: " + message + " Mouse button released if AimMac was holding it.");
            RefreshState();
        });

        FormClosing += (_, _) =>
        {
            _timer.Stop();
            _ble.Stop();
            _engine.Stop();
            _pointer.Release();
        };
        _timer.Start();
        Log("Cursor control is off. Start chooses secure mode until you explicitly turn on prototype mode.");
        RefreshState();
    }

    private async Task StartAsync()
    {
        var ok = await _ble.StartAsync(!_engine.PrototypeEnabled, _engine.StatusFor);
        Log(_ble.Summary);
        if (!ok)
        {
            StopAll(null);
            return;
        }

        Consume(_engine.Start());
    }

    private async Task EnablePrototypeAsync()
    {
        ReleaseGesture();
        var answer = MessageBox.Show(
            this,
            "Prototype mode turns off Bluetooth encryption so the current iPhone app can connect the way the Mac app does.\n\nThe hello UUID is created by the phone and is not a secret. Any nearby device can send one. Approve only records that you clicked the button. This is not authentication.\n\nCursor control stays off until you arm it separately.\n\nTurn on prototype mode?",
            "Prototype mode is not secure",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            Log("Prototype mode was left off.");
            return;
        }

        _ble.Stop();
        Consume(_engine.EnablePrototype(true));
        await StartAsync();
    }

    private async Task EnableSecureAsync()
    {
        _ble.Stop();
        Consume(_engine.EnableSecureMode());
        await StartAsync();
    }

    private void Arm()
    {
        if (_engine.PrototypeEnabled)
        {
            ReleaseGesture();
            var answer = MessageBox.Show(
                this,
                "Arm cursor control while prototype mode is on?\n\nThe Bluetooth link is not encrypted, and the phone's UUID does not prove who is sending. Arm it only while you can see this PC and the phone.",
                "Arm unauthenticated cursor control",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                Log("Cursor control stayed off.");
                return;
            }
        }

        Consume(_engine.Arm(_engine.PrototypeEnabled));
    }

    private void Approve()
    {
        var id = SelectedId();
        if (id is null)
        {
            Log("Select a device that is waiting for approval.");
            return;
        }

        if (_engine.PrototypeEnabled)
        {
            ReleaseGesture();
            var answer = MessageBox.Show(
                this,
                "Approve this device in prototype mode?\n\nIt can move the cursor only after input is also armed. The hello UUID is not authentication, and this approval is forgotten when the connection closes.",
                "Approve unauthenticated device",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        Consume(_engine.Approve(id));
        _ble.Notify(id);
    }

    private void Revoke()
    {
        var id = SelectedId();
        if (id is null)
        {
            Log("Select a device to revoke.");
            return;
        }

        ReleaseGesture();
        var answer = MessageBox.Show(
            this,
            "Revoke this device, drop any saved secure-mode identity, and release the mouse button?",
            "Revoke device",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        Consume(_engine.Revoke(id));
        _ble.Notify(id);
    }

    private void ActOnSelection(Func<string, SessionOutcome> action, string empty)
    {
        var id = SelectedId();
        if (id is null)
        {
            Log(empty);
            return;
        }

        Consume(action(id));
        _ble.Notify(id);
    }

    private void OnSubscribed(string id)
    {
        if (!_engine.Running)
        {
            _pointer.Release();
            return;
        }

        var osId = id.StartsWith("anon-", StringComparison.Ordinal) ? null : id;
        Consume(_engine.OnConnected(id, osId, _ble.RequiresEncryption));
        _ble.Notify(id);
    }

    private void OnUnsubscribed(string id)
    {
        Consume(_engine.OnDisconnected(id));
    }

    private void OnWritten(BleWriteEventArgs write)
    {
        if (!_engine.Running)
        {
            _pointer.Release();
            return;
        }

        try
        {
            var outcome = _engine.ReceiveFrame(write.ConnectionId, write.OsDeviceId, _ble.RequiresEncryption, write.Payload);
            Consume(outcome);
            _ble.Notify(write.ConnectionId);
        }
        catch (Exception ex)
        {
            Consume(_engine.OnTransportError(write.ConnectionId, ex.Message));
        }
    }

    private void OnTick()
    {
        if (_engine.Running)
        {
            var outcome = _engine.Tick();
            _pointer.Consume(outcome, _engine.IsCommandCurrent);
            foreach (var note in outcome.Notes)
            {
                Log(note);
            }
        }

        _ticks++;
        if (_ticks % 10 == 0)
        {
            RefreshState();
        }
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            _pointer.Release();
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                _pointer.Release();
                return;
            }

            action();
        });
    }

    private void ReleaseGesture() => Consume(_engine.ReleaseGesture());

    private void ApplyTuning()
    {
        if (_range.SelectedIndex < 0 || _smooth.SelectedIndex < 0)
        {
            return;
        }

        _engine.SetTuning(_range.SelectedIndex, _smooth.SelectedIndex, _invertX.Checked, _invertY.Checked);
    }

    private void Consume(SessionOutcome outcome)
    {
        _pointer.Consume(outcome, _engine.IsCommandCurrent);
        foreach (var note in outcome.Notes)
        {
            Log(note);
        }

        RefreshState();
    }

    private void StopAll(string? note)
    {
        _ble.Stop();
        var outcome = _engine.Stop();
        _pointer.Consume(outcome, _engine.IsCommandCurrent);
        _pointer.Release();
        if (note is not null)
        {
            Log(note);
        }

        foreach (var line in outcome.Notes)
        {
            Log(line);
        }

        RefreshState();
    }

    private string? SelectedId() =>
        _peers.SelectedItems.Count == 0 ? null : _peers.SelectedItems[0].Tag as string;

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _pointer.Release();
            _ble.Stop();
            _engine.Stop();
            Log("Error: " + ex.Message + " Mouse button released if AimMac was holding it.");
            RefreshState();
        }
    }

    private void RefreshState()
    {
        var mode = _engine.PrototypeEnabled ? "Prototype (not authenticated)" : "Secure (encryption requested, untested)";
        var input = _engine.InputArmed ? "ARMED" : "off";
        var running = _engine.Running ? "started" : "stopped";
        _status.Text = $"Receiver {running}. Mode: {mode}. Cursor control: {input}. Host name in status packets: {_engine.HostName}.";
        _rate.Text = _ble.Advertising
            ? $"Advertising. Aims in the last second: {_engine.RecentAimCount}. Rejected frames: {_engine.RejectedFrames}. {_ble.Summary}"
            : $"Not advertising. Rejected frames: {_engine.RejectedFrames}. {_ble.Summary}";
        _banner.Text = _engine.PrototypeEnabled
            ? "PROTOTYPE MODE. The link is unencrypted. The hello UUID is not authentication. Any nearby device can present one. Cursor control moves the pointer only while input is armed and you have approved that connection."
            : "Secure mode asks Windows for authenticated BLE encryption before a write is delivered. That OS pairing was not tested on this adapter. The hello UUID is only a label. Cursor control stays off until you arm input and approve a device.";
        _banner.BackColor = _engine.PrototypeEnabled ? SystemColors.Info : SystemColors.Control;
        _pause.Text = _engine.GlobalPaused ? "Resume" : "Pause";
        _start.Enabled = !_engine.Running;
        _stop.Enabled = _engine.Running;
        _arm.Enabled = _engine.Running && !_engine.InputArmed;
        _disarm.Enabled = _engine.InputArmed;
        _prototype.Enabled = !_engine.PrototypeEnabled;
        _secure.Enabled = _engine.PrototypeEnabled;

        var selected = SelectedId();
        _peers.BeginUpdate();
        _peers.Items.Clear();
        foreach (var peer in _engine.Peers())
        {
            var link = peer.LinkEncrypted ? "encryption requested" : "plaintext";
            if (!string.IsNullOrEmpty(peer.OsDeviceId))
            {
                link += ", OS id present";
            }

            var item = new ListViewItem(peer.Name);
            item.SubItems.Add(peer.Phase.ToString());
            item.SubItems.Add(link);
            item.SubItems.Add(string.IsNullOrEmpty(peer.HelloLabel) ? "—" : peer.HelloLabel);
            item.Tag = peer.ConnectionId;
            if (peer.ConnectionId == selected)
            {
                item.Selected = true;
            }

            _peers.Items.Add(item);
        }

        _peers.EndUpdate();
    }

    private void Log(string message)
    {
        _logLines++;
        if (_logLines > 200)
        {
            _log.Clear();
            _logLines = 1;
        }

        _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
    }
}
