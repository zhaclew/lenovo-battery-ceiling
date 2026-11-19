// ============================================================
//  Lenovo Battery Ceiling — 联想笔记本电池充电上限工具
//  GitHub: https://github.com/zhaclew/lenovo-battery-ceiling
//  License: MIT (见仓库根目录 LICENSE)
//
//  说明：通过联想 ACPI 虚拟电源控制器暴露的设备 \\.\EnergyDrv
//        发送 IOCTL 0x831020F8，调用固件里的 GBMD / SBMC 方法，
//        开关“电池养护模式”（约 60% 停充）。
//        不做任何固件、驱动层面的修改，只是调用官方已有的开关。
// ============================================================
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryCeiling
{
    internal static class EnergyDrv
    {
        private const uint IoctlBatteryMode = 0x831020F8;
        private const uint GenericRead = 0x80000000u;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr device, uint ioControlCode,
            byte[] inBuffer, uint inBufferSize,
            byte[] outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr Open()
        {
            IntPtr h = CreateFileW("\\\\.\\EnergyDrv", GenericRead, 0, IntPtr.Zero,
                OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 \\\\.\\EnergyDrv");
            return h;
        }

        public static bool GetConservation()
        {
            IntPtr h = Open();
            try
            {
                byte[] input = new byte[] { 0xFF };
                byte[] output = new byte[4];
                uint returned = 0;
                if (!DeviceIoControl(h, IoctlBatteryMode, input, 1, output, 4, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "读取电池模式失败");
                int mode = BitConverter.ToInt32(output, 0);
                return ((mode >> 5) & 1) == 1;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static void SetConservation(bool on)
        {
            IntPtr h = Open();
            try
            {
                byte[] input = new byte[] { (byte)(on ? 3 : 5) };
                byte[] output = new byte[4];
                uint returned = 0;
                if (!DeviceIoControl(h, IoctlBatteryMode, input, 1, output, 4, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置电池模式失败");
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }

    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    internal static class NativePower
    {
        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }

    internal sealed class MainForm : Form
    {
        private const int ModeOff = 0;
        private const int ModeHardware = 1;
        private const int ModeCustom = 2;
        private const string AppTitle = "联想小新笔记本电源管理";

        private readonly Label _lblBattery = new Label();
        private readonly Label _lblPower = new Label();
        private readonly Label _lblCons = new Label();
        private readonly Label _lblMode = new Label();
        private readonly RadioButton _rbOff = new RadioButton();
        private readonly RadioButton _rbHardware = new RadioButton();
        private readonly RadioButton _rbCustom = new RadioButton();
        private readonly NumericUpDown _numTarget = new NumericUpDown();
        private readonly NumericUpDown _numHyst = new NumericUpDown();
        private readonly CheckBox _chkAuto = new CheckBox();
        private readonly Button _btnApply = new Button();
        private readonly Button _btnHide = new Button();
        private readonly Button _btnReset = new Button();
        private readonly ToolTip _tips = new ToolTip();
        private readonly TextBox _txtLog = new TextBox();
        private readonly Timer _uiTimer = new Timer();
        private readonly Timer _ctrlTimer = new Timer();
        private readonly NotifyIcon _tray = new NotifyIcon();

        private int _mode = ModeOff;
        private int _target = 80;
        private int _hyst = 5;
        private bool _loading = true;
        private bool _exiting = false;
        private Icon _appIcon;
        private int _lastPct = -1;
        private bool _lastAc = false;
        private bool _lastCharging = false;
        private bool _lastCons = false;
        private string _lastError = "";

        public MainForm(bool startMinimized)
        {
            BuildUi();
            LoadSettings();
            UpdateRadioState();
            RefreshStatus();
            AppendLog("就绪。选择模式后点“应用”。");
            StartTimers();
            if (startMinimized)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                Hide();
            }
        }

        private void BuildUi()
        {
            Text = AppTitle;
            ClientSize = new Size(500, 462);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;

            _appIcon = CreateBatteryIcon();
            Icon = _appIcon;

            _lblBattery.SetBounds(16, 12, 210, 22);
            _lblBattery.Text = "电量: --";
            _lblBattery.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            Controls.Add(_lblBattery);

            _lblPower.SetBounds(235, 12, 250, 22);
            _lblPower.Text = "电源: --";
            Controls.Add(_lblPower);

            _lblCons.SetBounds(16, 38, 220, 22);
            _lblCons.Text = "硬件养护: --";
            Controls.Add(_lblCons);

            _lblMode.SetBounds(235, 38, 260, 22);
            _lblMode.Text = "模式: --";
            Controls.Add(_lblMode);

            GroupBox gb = new GroupBox();
            gb.Text = "充电模式";
            gb.SetBounds(16, 74, 468, 172);
            Controls.Add(gb);

            _rbOff.SetBounds(16, 24, 430, 24);
            _rbOff.Text = "不限制（允许充满 - 默认状态）";
            _rbOff.CheckedChanged += RadioChanged;
            gb.Controls.Add(_rbOff);

            _rbHardware.SetBounds(16, 52, 430, 24);
            _rbHardware.Text = "硬件养护（固定约60% - 长久生效）";
            _rbHardware.CheckedChanged += RadioChanged;
            gb.Controls.Add(_rbHardware);

            _rbCustom.SetBounds(16, 80, 430, 24);
            _rbCustom.Text = "自定义上限（软件浮动维持 - 需挂后台）";
            _rbCustom.CheckedChanged += RadioChanged;
            gb.Controls.Add(_rbCustom);

            Label lblTarget = new Label();
            lblTarget.SetBounds(42, 111, 52, 22);
            lblTarget.Text = "上限 %";
            gb.Controls.Add(lblTarget);

            _numTarget.SetBounds(98, 108, 64, 24);
            _numTarget.Minimum = 60;
            _numTarget.Maximum = 100;
            _numTarget.Value = 80;
            _numTarget.ValueChanged += TargetChanged;
            gb.Controls.Add(_numTarget);

            Label lblHyst = new Label();
            lblHyst.SetBounds(180, 111, 60, 22);
            lblHyst.Text = "回差 %";
            gb.Controls.Add(lblHyst);

            _numHyst.SetBounds(242, 108, 56, 24);
            _numHyst.Minimum = 1;
            _numHyst.Maximum = 10;
            _numHyst.Value = 5;
            gb.Controls.Add(_numHyst);

            Label lblNote = new Label();
            lblNote.SetBounds(42, 134, 414, 18);
            lblNote.Text = "到上限后停充；EC 会向 60% 放电，低于下限时恢复充电。";
            lblNote.ForeColor = Color.DimGray;
            gb.Controls.Add(lblNote);

            Label lblNote2 = new Label();
            lblNote2.SetBounds(42, 150, 414, 16);
            lblNote2.Text = "自定义上限是浮动区间；稳定 60% 请用硬件养护。";
            lblNote2.ForeColor = Color.DimGray;
            gb.Controls.Add(lblNote2);

            _chkAuto.SetBounds(16, 252, 300, 24);
            _chkAuto.Text = "开机自动启动（最小化到托盘）";
            _chkAuto.CheckedChanged += AutoChanged;
            Controls.Add(_chkAuto);

            _btnApply.SetBounds(16, 284, 100, 30);
            _btnApply.Text = "应用";
            _btnApply.Click += ApplyClick;
            Controls.Add(_btnApply);

            _btnHide.SetBounds(128, 284, 150, 30);
            _btnHide.Text = "最小化到托盘";
            _btnHide.Click += delegate { HideToTray(); };
            Controls.Add(_btnHide);

            _btnReset.SetBounds(290, 284, 150, 30);
            _btnReset.Text = "回到默认";
            _btnReset.Click += ResetClick;
            Controls.Add(_btnReset);

            _tips.SetToolTip(_btnReset, "恢复：不限制、上限 80%、回差 5%、关闭开机自启。");

            _txtLog.SetBounds(16, 324, 468, 122);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.WhiteSmoke;
            Controls.Add(_txtLog);

            _tray.Text = AppTitle;
            _tray.Icon = _appIcon;
            _tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { ShowWindow(); });
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowWindow(); };

            FormClosing += FormClosingHandler;
            SystemEvents.PowerModeChanged += PowerModeChanged;
        }

        private static Icon CreateBatteryIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.Clear(Color.Transparent);
                    g.DrawRectangle(Pens.White, 1, 4, 11, 8);
                    g.FillRectangle(Brushes.LimeGreen, 3, 6, 7, 4);
                    g.FillRectangle(Brushes.White, 12, 6, 2, 4);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void StartTimers()
        {
            _uiTimer.Interval = 2000;
            _uiTimer.Tick += delegate { RefreshStatus(); };
            _uiTimer.Start();

            _ctrlTimer.Interval = 30000;
            _ctrlTimer.Tick += delegate { ControlTick(); };
            if (_mode == ModeCustom)
                _ctrlTimer.Start();
        }

        private void LoadSettings()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCeiling"))
            {
                if (k != null)
                {
                    _mode = (int)k.GetValue("Mode", -1);
                    _target = (int)k.GetValue("Target", 80);
                    _hyst = (int)k.GetValue("Hysteresis", 5);
                }
                else
                {
                    _mode = -1;
                }
            }

            if (_target < 60) _target = 60;
            if (_target > 100) _target = 100;
            if (_hyst < 1) _hyst = 1;
            if (_hyst > 10) _hyst = 10;

            _numTarget.Value = _target;
            _numHyst.Value = _hyst;

            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                _chkAuto.Checked = k != null && k.GetValue("BatteryCeiling") != null;
            }

            if (_mode < ModeOff || _mode > ModeCustom)
            {
                bool cons = false;
                try { cons = EnergyDrv.GetConservation(); } catch { }
                _mode = cons ? ModeHardware : ModeOff;
            }

            _loading = false;
        }

        private void SaveSettings()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryCeiling"))
            {
                k.SetValue("Mode", _mode, RegistryValueKind.DWord);
                k.SetValue("Target", _target, RegistryValueKind.DWord);
                k.SetValue("Hysteresis", _hyst, RegistryValueKind.DWord);
            }
        }

        private void UpdateRadioState()
        {
            _loading = true;
            _rbOff.Checked = _mode == ModeOff;
            _rbHardware.Checked = _mode == ModeHardware;
            _rbCustom.Checked = _mode == ModeCustom;
            _loading = false;
            UpdateTargetEnabled();
        }

        private void UpdateTargetEnabled()
        {
            bool custom = _rbCustom.Checked;
            _numTarget.Enabled = custom;
            _numHyst.Enabled = custom;
        }

        private void RadioChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            UpdateTargetEnabled();
        }

        private void TargetChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            if (_numTarget.Value < 60) _numTarget.Value = 60;
        }

        private void AutoChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            SetAutoStart(_chkAuto.Checked);
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enabled)
                        k.SetValue("BatteryCeiling",
                            "\"" + Application.ExecutablePath + "\" --minimized");
                    else
                        k.DeleteValue("BatteryCeiling", false);
                }
                AppendLog(enabled ? "已设置开机自动启动。" : "已取消开机自动启动。");
            }
            catch (Exception ex)
            {
                AppendLog("设置开机启动失败: " + ex.Message);
            }
        }

        private void ApplyClick(object sender, EventArgs e)
        {
            if (_rbHardware.Checked) _mode = ModeHardware;
            else if (_rbCustom.Checked) _mode = ModeCustom;
            else _mode = ModeOff;

            _target = (int)_numTarget.Value;
            _hyst = (int)_numHyst.Value;
            if (_target < 60)
            {
                _target = 60;
                _numTarget.Value = 60;
            }

            SaveSettings();
            try
            {
                if (_mode == ModeOff)
                {
                    _ctrlTimer.Stop();
                    EnergyDrv.SetConservation(false);
                    AppendLog("已关闭限制，允许充满。");
                }
                else if (_mode == ModeHardware)
                {
                    _ctrlTimer.Stop();
                    EnergyDrv.SetConservation(true);
                    AppendLog("已开启硬件养护（约 60% 停充）。");
                }
                else
                {
                    AppendLog(String.Format("已启用自定义上限: {0}%，浮动下限 {1}%（回差 {2}%）。",
                        _target, _target - _hyst, _hyst));
                    _ctrlTimer.Start();
                    ControlTick();
                }
            }
            catch (Exception ex)
            {
                AppendLog("应用失败: " + ex.Message);
                ShowError("应用失败: " + ex.Message);
            }
            RefreshStatus();
        }

        private void ResetClick(object sender, EventArgs e)
        {
            _loading = true;
            _mode = ModeOff;
            _target = 80;
            _hyst = 5;
            _numTarget.Value = 80;
            _numHyst.Value = 5;
            _rbOff.Checked = true;
            _chkAuto.Checked = false;
            _loading = false;
            UpdateTargetEnabled();
            SaveSettings();
            SetAutoStart(false);
            _ctrlTimer.Stop();
            try
            {
                EnergyDrv.SetConservation(false);
                AppendLog("已回到默认：不限制充电、上限 80%、回差 5%、关闭开机自启。");
            }
            catch (Exception ex)
            {
                AppendLog("恢复默认失败: " + ex.Message);
                ShowError("恢复默认失败: " + ex.Message);
            }
            RefreshStatus();
        }

        private bool ReadStatus(out int pct, out bool ac, out bool charging, out bool cons, out string error)
        {
            pct = -1;
            ac = false;
            charging = false;
            cons = false;
            error = "";
            SYSTEM_POWER_STATUS sps;
            if (!NativePower.GetSystemPowerStatus(out sps))
            {
                error = "GetSystemPowerStatus 失败: " + Marshal.GetLastWin32Error();
                return false;
            }
            pct = sps.BatteryLifePercent == 255 ? -1 : (int)sps.BatteryLifePercent;
            ac = sps.ACLineStatus == 1;
            charging = (sps.BatteryFlag & 8) != 0;
            try
            {
                cons = EnergyDrv.GetConservation();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return true;
        }

        private void RefreshStatus()
        {
            int pct; bool ac; bool charging; bool cons; string err;
            if (!ReadStatus(out pct, out ac, out charging, out cons, out err))
            {
                if (err != _lastError)
                {
                    _lastError = err;
                    AppendLog("读取状态失败: " + err);
                }
                _lblBattery.Text = "电量: --";
                _lblPower.Text = "电源: --";
                _lblCons.Text = "硬件养护: 不可用";
                _lblCons.ForeColor = Color.Firebrick;
                return;
            }
            _lastError = "";
            _lastPct = pct;
            _lastAc = ac;
            _lastCharging = charging;
            _lastCons = cons;

            _lblBattery.Text = pct < 0 ? "电量: 未知" : "电量: " + pct + "%";
            _lblBattery.ForeColor = Color.Black;
            _lblPower.Text = (ac ? "电源: 已接通" : "电源: 电池供电") + (charging ? " / 充电中" : " / 未充电");
            _lblPower.ForeColor = charging ? Color.DarkOrange : Color.DimGray;
            _lblCons.Text = "硬件养护: " + (cons ? "开启（约 60% 停充）" : "关闭");
            _lblCons.ForeColor = cons ? Color.ForestGreen : Color.DimGray;

            string modeText;
            if (_mode == ModeOff) modeText = "不限制";
            else if (_mode == ModeHardware) modeText = "硬件养护 60%";
            else modeText = String.Format("自定义上限 {0}%（下限 {1}%）", _target, _target - _hyst);
            _lblMode.Text = "模式: " + modeText;
            _lblMode.ForeColor = _mode == ModeCustom ? Color.RoyalBlue : Color.DimGray;
        }

        private void ControlTick()
        {
            if (_mode != ModeCustom || _exiting) return;
            int pct; bool ac; bool charging; bool cons; string err;
            if (!ReadStatus(out pct, out ac, out charging, out cons, out err))
            {
                AppendLog("检查失败: " + err);
                return;
            }
            if (!ac) return;
            if (pct < 0) return;
            if (_target >= 100)
            {
                if (cons)
                {
                    SetCons(false, "目标为 100%，解除限制");
                }
                return;
            }
            if (pct >= _target && !cons)
            {
                SetCons(true, String.Format("电量 {0}% ≥ 上限 {1}%，停止充电（EC 会缓慢放电到下限 {2}%）",
                    pct, _target, _target - _hyst));
                Notify("已达到上限", String.Format("电量 {0}% 达到上限 {1}%，已停止充电；电量会缓慢下降到 {2}%。",
                    pct, _target, _target - _hyst));
            }
            else if (pct <= _target - _hyst && cons)
            {
                SetCons(false, String.Format("电量 {0}% ≤ 下限 {1}%，恢复充电", pct, _target - _hyst));
            }
        }

        private void SetCons(bool on, string reason)
        {
            try
            {
                EnergyDrv.SetConservation(on);
                AppendLog(reason + "（硬件养护 " + (on ? "开" : "关") + "）");
                _lastCons = on;
            }
            catch (Exception ex)
            {
                AppendLog("设置失败: " + ex.Message);
            }
        }

        private void Notify(string title, string text)
        {
            try
            {
                if (_tray.Visible)
                    _tray.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        private void AppendLog(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BatteryCeiling");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }

            if (_txtLog != null)
            {
                _txtLog.AppendText(line + Environment.NewLine);
                if (_txtLog.Lines.Length > 200)
                {
                    string[] lines = _txtLog.Lines;
                    string[] keep = new string[120];
                    Array.Copy(lines, lines.Length - 120, keep, 0, 120);
                    _txtLog.Lines = keep;
                }
            }
        }

        private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.StatusChange && !_exiting && IsHandleCreated)
            {
                try { BeginInvoke(new MethodInvoker(delegate { ControlTick(); RefreshStatus(); })); }
                catch { }
            }
        }

        private void ShowWindow()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshStatus();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            Notify(AppTitle, "程序已最小化到托盘，限制仍在后台维持。");
        }

        private void FormClosingHandler(object sender, FormClosingEventArgs e)
        {
            if (_exiting) return;
            if (e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                _exiting = true;
                _ctrlTimer.Stop();
                _uiTimer.Stop();
                if (_mode == ModeCustom)
                {
                    try { EnergyDrv.SetConservation(false); } catch { }
                }
                _tray.Visible = false;
                return;
            }
            e.Cancel = true;
            HideToTray();
        }

        private void ExitApp()
        {
            _exiting = true;
            _ctrlTimer.Stop();
            _uiTimer.Stop();
            if (_mode == ModeCustom)
            {
                try { EnergyDrv.SetConservation(false); } catch { }
            }
            _tray.Visible = false;
            _tray.Dispose();
            Close();
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static class Program
    {
        private static string DiagPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BatteryCeiling", "diag.txt");
        }

        private static void WriteDiag(string text)
        {
            string path = DiagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        private static string StatusText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            SYSTEM_POWER_STATUS sps;
            if (NativePower.GetSystemPowerStatus(out sps))
            {
                sb.AppendLine("battery_percent=" + (sps.BatteryLifePercent == 255 ? "unknown" : sps.BatteryLifePercent.ToString()));
                sb.AppendLine("ac_line=" + sps.ACLineStatus);
                sb.AppendLine("battery_flag=" + sps.BatteryFlag);
                sb.AppendLine("charging=" + (((sps.BatteryFlag & 8) != 0) ? "yes" : "no"));
            }
            else
            {
                sb.AppendLine("power_status_error=" + Marshal.GetLastWin32Error());
            }
            try
            {
                sb.AppendLine("conservation=" + (EnergyDrv.GetConservation() ? "on" : "off"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("conservation_error=" + ex.Message);
            }
            return sb.ToString();
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (arg == "--diag")
            {
                try { WriteDiag(StatusText()); }
                catch (Exception ex) { WriteDiag("error=" + ex); }
                return;
            }
            if (arg == "--set-on" || arg == "--set-off")
            {
                try
                {
                    EnergyDrv.SetConservation(arg == "--set-on");
                    WriteDiag(StatusText());
                }
                catch (Exception ex)
                {
                    WriteDiag("error=" + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (arg == "--shot")
            {
                try
                {
                    MainForm f = new MainForm(false);
                    f.Show();
                    Application.DoEvents();
                    Bitmap bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    string path = Path.Combine(Path.GetTempPath(), "BatteryCeiling_ui.png");
                    bmp.Save(path, ImageFormat.Png);
                    bmp.Dispose();
                    f.Close();
                    WriteDiag("shot=" + path);
                }
                catch (Exception ex)
                {
                    WriteDiag("shot_error=" + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(arg == "--minimized"));
        }
    }
}
