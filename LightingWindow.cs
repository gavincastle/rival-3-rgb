using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LightingWindow : Form
{
    private static readonly Color Background = Color.FromArgb(23, 26, 34);
    private static readonly Color PanelColor = Color.FromArgb(37, 42, 54);
    private static readonly Color TextColor = Color.FromArgb(241, 244, 250);
    private static readonly Color Muted = Color.FromArgb(174, 185, 204);
    private static readonly Color Accent = Color.FromArgb(174, 221, 255);
    private readonly Button[] zoneButtons = new Button[3];
    private readonly Button[] powerButtons = new Button[3];
    private readonly Color[] lastColors = { Color.Red, Color.Lime, Color.Blue };
    private readonly bool[] lightsDisabled = new bool[3];
    private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
    private readonly ComboBox startupMode = new ComboBox();
    private readonly Button saveStartup;
    private readonly ComboBox presets = new ComboBox();
    private readonly Button savePreset;
    private readonly Label status;
    private readonly Timer reconnectTimer = new Timer { Interval = 400 };
    private Color[] appliedPalette;
    private IntPtr deviceNotification;
    private bool applying;
    private int connectionVersion;
    private int reconnectAttempts;

    private const int DeviceChange = 0x0219;
    private const int DeviceArrival = 0x8000;
    private const int DeviceRemovalComplete = 0x8004;
    private const int DeviceInterfaceType = 5;
    private const int InterfaceNameOffset = 28;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new LightingWindow())
            Application.Run(window);
    }

    internal LightingWindow()
    {
        Text = "Rival 3 RGB";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(452, 545);
        MinimumSize = Size;
        BackColor = Background;
        ForeColor = TextColor;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 14),
            ColumnCount = 1, RowCount = 12
        };
        Controls.Add(layout);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var title = MakeLabel("Rival 3 Gen 2", TextColor);
        title.Font = new Font("Segoe UI", 23, FontStyle.Bold);
        AddRow(layout, title, 44);
        AddRow(layout, MakeLabel("Pick a color or switch each zone on and off.", Muted), 39);
        string[] names = { "Top", "Middle", "Bottom" };
        for (int zone = 0; zone < names.Length; zone++)
            AddRow(layout, MakeZoneRow(zone, names[zone]), 53);

        actions.Dock = DockStyle.Fill;
        actions.Margin = new Padding(0, 6, 0, 0);
        actions.WrapContents = false;
        var setAll = MakeButton("Set all zones", Accent, Background);
        setAll.Size = new Size(140, 38);
        setAll.Click += async delegate { await PickAndApply(RivalMouse.AllZones, lastColors[0]); };
        actions.Controls.Add(setAll);
        var lightsOff = MakeButton("Lights off", PanelColor, TextColor);
        lightsOff.Size = new Size(120, 38);
        lightsOff.Margin = new Padding(8, 0, 0, 0);
        lightsOff.Click += async delegate { await Apply(RivalMouse.AllZones, Color.Black, true); };
        actions.Controls.Add(lightsOff);
        AddRow(layout, actions, 55);

        AddRow(layout, MakeLabel("Presets", TextColor), 27);
        var presetActions = MakeSelectionRow();
        ConfigureDropdown(presets, "Presets");
        presets.SelectionChangeCommitted += async delegate { await ApplyNumberedPreset(); };
        presetActions.Controls.Add(presets, 0, 0);
        savePreset = MakeButton("Save preset", Accent, Background);
        savePreset.Dock = DockStyle.Fill;
        savePreset.Enabled = false;
        savePreset.Click += delegate { SaveNumberedPreset(); };
        presetActions.Controls.Add(savePreset, 1, 0);
        AddRow(layout, presetActions, 40);

        AddRow(layout, MakeLabel("Mouse startup lighting", TextColor), 27);
        var startupActions = MakeSelectionRow();
        ConfigureDropdown(startupMode, "Mouse startup lighting");
        startupMode.Items.AddRange(new object[] { "Off", "Click reactive", "Rainbow", "Rainbow + click reactive" });
        startupActions.Controls.Add(startupMode, 0, 0);
        saveStartup = MakeButton("Save to mouse", PanelColor, TextColor);
        saveStartup.Dock = DockStyle.Fill;
        saveStartup.Enabled = false;
        startupMode.SelectedIndexChanged += delegate { saveStartup.Enabled = !applying && startupMode.SelectedIndex >= 0; };
        saveStartup.Click += async delegate { await SaveStartupLighting(); };
        startupActions.Controls.Add(saveStartup, 1, 0);
        AddRow(layout, startupActions, 36);
        AddRow(layout, MakeLabel("Applies before the app loads your saved colors.", Muted), 29);

        status = MakeLabel("The first change switches to manual lighting.", Muted);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(status, 0, 11);
        reconnectTimer.Tick += RestoreOnReconnect;
        if (PalettePreferences.Load(lastColors, lightsDisabled))
        {
            appliedPalette = MakePalette(0, Color.Black);
            ShowPalette(appliedPalette);
        }
        RefreshPresets(-1);
    }

    private void RefreshPresets(int selectedNumber)
    {
        try
        {
            int[] numbers = PalettePreferences.GetPresetNumbers();
            presets.Items.Clear();
            foreach (int number in numbers)
                presets.Items.Add(number);
            presets.SelectedItem = selectedNumber;
        }
        catch (IOException) { ShowPresetError("Could not list presets."); }
        catch (UnauthorizedAccessException) { ShowPresetError("Could not list presets."); }
        SetEnabled(!applying);
    }

    private void SaveNumberedPreset()
    {
        try
        {
            int number = PalettePreferences.SaveNew(lastColors, lightsDisabled);
            RefreshPresets(number);
            status.ForeColor = Accent;
            status.Text = "Preset " + number + " saved.";
        }
        catch (IOException) { ShowPresetError("Could not save the preset."); }
        catch (UnauthorizedAccessException) { ShowPresetError("Could not save the preset."); }
    }

    private async Task ApplyNumberedPreset()
    {
        if (!(presets.SelectedItem is int))
            return;

        int number = (int)presets.SelectedItem;
        var rememberedColors = new Color[lastColors.Length];
        var off = new bool[lightsDisabled.Length];
        if (!PalettePreferences.LoadNumbered(number, rememberedColors, off))
        {
            RefreshPresets(-1);
            ShowPresetError("Could not open preset " + number + ".");
            return;
        }

        var palette = new Color[rememberedColors.Length];
        for (int zone = 0; zone < palette.Length; zone++)
            palette[zone] = off[zone] ? Color.Black : rememberedColors[zone];

        await ApplyPalette(palette, rememberedColors, number, "Preset " + number + " applied.");
    }

    private void ShowPresetError(string message)
    {
        status.ForeColor = Color.FromArgb(255, 171, 155);
        status.Text = message;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (appliedPalette != null)
        {
            status.Text = "Restoring saved lighting…";
            reconnectAttempts = 5;
            reconnectTimer.Start();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var filter = new DeviceInterfaceFilter { Type = DeviceInterfaceType, InterfaceClass = RivalMouse.HidInterfaceClass };
        filter.Size = Marshal.SizeOf(filter);
        deviceNotification = RegisterDeviceNotificationW(Handle, ref filter, 0);
        if (deviceNotification == IntPtr.Zero && status != null)
            status.Text = "Automatic reconnect detection is unavailable.";
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        connectionVersion++;
        reconnectTimer.Stop();
        if (deviceNotification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(deviceNotification);
            deviceNotification = IntPtr.Zero;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            reconnectTimer.Dispose();
    }

    protected override void WndProc(ref Message message)
    {
        long change = message.WParam.ToInt64();
        if (message.Msg == DeviceChange && appliedPalette != null &&
            (change == DeviceArrival || change == DeviceRemovalComplete) && message.LParam != IntPtr.Zero &&
            Marshal.ReadInt32(message.LParam, 4) == DeviceInterfaceType)
        {
            int nameBytes = Marshal.ReadInt32(message.LParam) - InterfaceNameOffset;
            if (nameBytes >= sizeof(char) && RivalMouse.IsLightingInterface(Marshal.PtrToStringUni(
                IntPtr.Add(message.LParam, InterfaceNameOffset), nameBytes / sizeof(char))))
            {
                connectionVersion++;
                reconnectTimer.Stop();
                reconnectAttempts = change == DeviceArrival ? 5 : 0;
                if (reconnectAttempts > 0)
                    reconnectTimer.Start();
                status.ForeColor = Muted;
                status.Text = change == DeviceArrival ? "Mouse reconnected. Restoring lighting…" : "Waiting for the mouse to reconnect…";
            }
        }
        base.WndProc(ref message);
    }

    private async void RestoreOnReconnect(object sender, EventArgs args)
    {
        if (applying || reconnectAttempts == 0)
            return;

        reconnectTimer.Stop();
        int version = connectionVersion;
        reconnectAttempts--;
        SetEnabled(false);
        try
        {
            Color[] palette = appliedPalette;
            await Task.Run(() => RivalMouse.SetColors(palette));
            if (version == connectionVersion && !IsDisposed)
            {
                status.ForeColor = Accent;
                status.Text = "Saved lighting restored.";
            }
        }
        catch (Exception error)
        {
            if (version != connectionVersion || IsDisposed)
                return;
            if (reconnectAttempts > 0)
                reconnectTimer.Start();
            else
            {
                status.ForeColor = Color.FromArgb(255, 171, 155);
                status.Text = "Could not restore lighting. " + error.Message;
            }
        }
        finally
        {
            if (!IsDisposed)
                SetEnabled(true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceFilter
    {
        internal int Size, Type, Reserved;
        internal Guid InterfaceClass;
        internal ushort Name;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr window, ref DeviceInterfaceFilter filter, int flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr notification);

    private Control MakeZoneRow(int zone, string name)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(12, 6, 6, 6), BackColor = PanelColor,
            ColumnCount = 3, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 89));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(MakeLabel(name, TextColor), 0, 0);

        var button = MakeButton("Choose color", Background, TextColor);
        button.Dock = DockStyle.Fill;
        button.AccessibleName = "Choose " + name.ToLowerInvariant() + " zone color";
        button.Click += async delegate { await PickAndApply((byte)(1 << zone), lastColors[zone]); };
        zoneButtons[zone] = button;
        row.Controls.Add(button, 1, 0);

        var power = MakeButton("Turn off", Background, TextColor);
        power.Dock = DockStyle.Fill;
        power.Margin = new Padding(6, 0, 0, 0);
        power.AccessibleName = "Turn off " + name.ToLowerInvariant() + " zone";
        power.Tag = name.ToLowerInvariant();
        power.Click += async delegate
        {
            Color color = lightsDisabled[zone] ? lastColors[zone] : Color.Black;
            await Apply((byte)(1 << zone), color, false);
        };
        powerButtons[zone] = power;
        row.Controls.Add(power, 2, 0);
        return row;
    }

    private async Task PickAndApply(byte zones, Color initial)
    {
        using (var picker = new ColorDialog { FullOpen = true, Color = initial })
        {
            if (picker.ShowDialog(this) == DialogResult.OK)
                await Apply(zones, picker.Color, false);
        }
    }

    private async Task SaveStartupLighting()
    {
        int mode = startupMode.SelectedIndex;
        if (mode < 0)
            return;

        SetEnabled(false);
        status.ForeColor = Muted;
        status.Text = "Saving startup lighting to the mouse…";
        bool saved = false;
        int version = connectionVersion;
        try
        {
            await Task.Run(() => RivalMouse.SaveStartupMode((mode & 2) != 0, (mode & 1) != 0));
            saved = true;
            if (!IsDisposed && version == connectionVersion && appliedPalette != null)
            {
                Color[] palette = appliedPalette;
                await Task.Run(() => RivalMouse.SetColors(palette));
            }

            if (!IsDisposed)
            {
                status.ForeColor = Accent;
                status.Text = "Startup lighting saved to the mouse.";
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                status.ForeColor = Color.FromArgb(255, 171, 155);
                status.Text = saved ? "Startup lighting saved. Could not restore your current colors." : error.Message;
            }
        }
        finally
        {
            if (!IsDisposed)
                SetEnabled(true);
        }
    }

    private Task Apply(byte zones, Color color, bool lightsOff)
    {
        return ApplyPalette(MakePalette(zones, color), null, -1,
            lightsOff ? "Mouse accepted the lights-off command." : "Mouse accepted the zone update.");
    }

    private async Task ApplyPalette(Color[] palette, Color[] rememberedColors, int presetNumber, string successMessage)
    {
        SetEnabled(false);
        int version = connectionVersion;
        status.ForeColor = Muted;
        status.Text = "Sending…";
        try
        {
            await Task.Run(() => RivalMouse.SetColors(palette));
            if (IsDisposed)
                return;
            appliedPalette = palette;
            if (rememberedColors != null)
                rememberedColors.CopyTo(lastColors, 0);
            ShowPalette(palette);
            if (presetNumber > 0)
                presets.SelectedItem = presetNumber;
            else
                presets.SelectedIndex = -1;

            if (version == connectionVersion)
            {
                status.ForeColor = Accent;
                status.Text = successMessage;
            }
            try { PalettePreferences.Save(lastColors, lightsDisabled); }
            catch (IOException) { ShowSaveError(); }
            catch (UnauthorizedAccessException) { ShowSaveError(); }
        }
        catch (Exception error)
        {
            if (!IsDisposed && version == connectionVersion)
            {
                if (presetNumber > 0)
                    presets.SelectedIndex = -1;
                status.ForeColor = Color.FromArgb(255, 171, 155);
                status.Text = error.Message;
            }
        }
        finally
        {
            if (!IsDisposed)
                SetEnabled(true);
        }
    }

    private Color[] MakePalette(byte changedZones, Color color)
    {
        var palette = new Color[lastColors.Length];
        for (int zone = 0; zone < palette.Length; zone++)
            palette[zone] = (changedZones & (1 << zone)) != 0 ? color :
                lightsDisabled[zone] ? Color.Black : lastColors[zone];
        return palette;
    }

    private void ShowPalette(Color[] palette)
    {
        for (int zone = 0; zone < palette.Length; zone++)
        {
            Color zoneColor = palette[zone];
            bool off = zoneColor.ToArgb() == Color.Black.ToArgb();
            lightsDisabled[zone] = off;
            powerButtons[zone].Text = off ? "Turn on" : "Turn off";
            powerButtons[zone].AccessibleName = powerButtons[zone].Text + " " + powerButtons[zone].Tag + " zone";
            if (!off)
                lastColors[zone] = zoneColor;
            Button button = zoneButtons[zone];
            button.BackColor = zoneColor;
            double luminance = 0.2126 * LinearChannel(zoneColor.R) + 0.7152 * LinearChannel(zoneColor.G) + 0.0722 * LinearChannel(zoneColor.B);
            button.ForeColor = luminance > 0.179 ? Color.Black : Color.White;
            button.Text = off ? "Choose color" :
                string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", zoneColor.R, zoneColor.G, zoneColor.B);
        }
    }

    private void ShowSaveError()
    {
        status.ForeColor = Color.FromArgb(255, 171, 155);
        status.Text = "Lighting applied. Could not save the preset beside the app.";
    }

    private void SetEnabled(bool enabled)
    {
        applying = !enabled;
        actions.Enabled = enabled;
        startupMode.Enabled = enabled;
        saveStartup.Enabled = enabled && startupMode.SelectedIndex >= 0;
        presets.Enabled = enabled && presets.Items.Count > 0;
        savePreset.Enabled = enabled && appliedPalette != null;
        for (int zone = 0; zone < zoneButtons.Length; zone++)
        {
            zoneButtons[zone].Enabled = enabled;
            powerButtons[zone].Enabled = enabled;
        }
    }

    private static double LinearChannel(byte channel)
    {
        double srgb = channel / 255.0;
        return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    private static void AddRow(TableLayoutPanel layout, Control control, int height)
    {
        int index = layout.RowStyles.Count;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(control, 0, index);
    }

    private static TableLayoutPanel MakeSelectionRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return row;
    }

    private static void ConfigureDropdown(ComboBox dropdown, string name)
    {
        dropdown.AccessibleName = name;
        dropdown.DropDownStyle = ComboBoxStyle.DropDownList;
        dropdown.Dock = DockStyle.Fill;
        dropdown.Margin = new Padding(0, 4, 10, 0);
    }

    private static Label MakeLabel(string text, Color color)
    {
        return new Label
        {
            Text = text, ForeColor = color, Dock = DockStyle.Fill, Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static Button MakeButton(string text, Color background, Color foreground)
    {
        var button = new Button
        {
            Text = text, BackColor = background, ForeColor = foreground, FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false, Cursor = Cursors.Hand, Margin = Padding.Empty
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(100, 132, 153);
        return button;
    }
}
