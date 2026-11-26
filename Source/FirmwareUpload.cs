using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CYDMonitor {
public static class WindowsStartup {
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "CYD PC Monitor";
    static string Command { get { return "\"" + Application.ExecutablePath + "\" --startup"; } }

    public static bool Enabled {
        get {
            using (var key = Registry.CurrentUser.OpenSubKey(Key))
                return key != null && String.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled) {
        using (var key = Registry.CurrentUser.CreateSubKey(Key)) {
            if (enabled) key.SetValue(Name, Command, RegistryValueKind.String);
            else if (String.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(Name, false);
        }
    }
}

public sealed class FirmwareUpload : Form {
    static readonly string[] Names = {
        "CYD_PCMonitor.ino.bootloader.bin", "CYD_PCMonitor.ino.partitions.bin",
        "boot_app0.bin", "CYD_PCMonitor.ino.bin"
    };
    static readonly string[] Addresses = { "0x1000", "0x8000", "0xe000", "0x10000" };
    static readonly Regex ProgressLine = new Regex(@"Writing at.*?\s(\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant);
    readonly ProgressBar bar = new ProgressBar { Bounds = new Rectangle(16, 43, 584, 24) };
    readonly Label phase = new Label { Bounds = new Rectangle(16, 14, 584, 25) };
    readonly TextBox log = new TextBox { Bounds = new Rectangle(16, 79, 584, 223), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false };
    readonly Button close = new Button { Text = "ปิด", Bounds = new Rectangle(500, 314, 100, 32), Enabled = false };
    bool busy = true;
    public bool Succeeded { get; private set; }

    public FirmwareUpload(string port, Icon icon) {
        Text = "อัปโหลดเฟิร์มแวร์ • " + port;
        Icon = icon;
        ClientSize = new Size(616, 360);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Controls.AddRange(new Control[] { phase, bar, log, close });
        close.Click += (s, e) => Close();
        FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        Shown += async (s, e) => {
            UpdateProgress(0, "กำลังเชื่อมต่อบอร์ด");
            var progress = new Progress<FlashUpdate>(update => {
                if (update.Line.Length != 0) log.AppendText(update.Line + Environment.NewLine);
                if (busy && update.Percent >= 0) UpdateProgress(update.Percent, update.Stage);
            });
            try {
                await Task.Run(() => Run(port, progress));
                Succeeded = true;
                UpdateProgress(100, "อัปโหลดและตรวจสอบข้อมูลสำเร็จ");
            } catch (Exception error) {
                phase.Text = "อัปโหลดไม่สำเร็จ";
                log.AppendText(error.Message + Environment.NewLine);
            } finally {
                busy = false;
                close.Enabled = true;
            }
        };
    }

    void UpdateProgress(int percent, string stage) {
        bar.Value = Math.Max(bar.Value, percent);
        phase.Text = stage + " • " + bar.Value + "%";
    }

    public sealed class FlashUpdate {
        public readonly int Percent;
        public readonly string Stage, Line;
        public FlashUpdate(int percent, string stage, string line) {
            Percent = percent;
            Stage = stage;
            Line = line;
        }
    }

    public static void Run(string port, IProgress<FlashUpdate> updates) {
        if (!Regex.IsMatch(port, @"\ACOM[0-9]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("พอร์ต USB ไม่ถูกต้อง");
        string app = AppDomain.CurrentDomain.BaseDirectory;
        string tool = Path.Combine(app, "FlashTool", "esptool.exe");
        string[] folders = { Path.Combine(app, "Firmware", "Binaries"), Path.GetFullPath(Path.Combine(app, "..", "Firmware")) };
        string folder = folders.FirstOrDefault(dir => Names.All(name => File.Exists(Path.Combine(dir, name))));
        if (!File.Exists(tool) || folder == null)
            throw new FileNotFoundException("ไฟล์อัปโหลดไม่ครบ กรุณาติดตั้ง CYD PC Monitor ใหม่หรือแตก ZIP ให้ครบทั้งโฟลเดอร์");

        var args = new StringBuilder("--chip esp32 --port " + port + " --baud 115200 --before default-reset --after hard-reset write-flash -z --flash-mode dio --flash-freq 40m --flash-size 4MB");
        long[] sizes = Names.Select(name => new FileInfo(Path.Combine(folder, name)).Length).ToArray();
        long total = sizes.Sum();
        for (int i = 0; i < Names.Length; i++)
            args.AppendFormat(" {0} \"{1}\"", Addresses[i], Path.Combine(folder, Names[i]));
        var info = new ProcessStartInfo(tool, args.ToString()) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.EnvironmentVariables["NO_COLOR"] = "1";
        int image = 0;
        long verified = 0;
        using (var toolProcess = new Process { StartInfo = info }) {
            toolProcess.OutputDataReceived += (s, e) => {
                if (String.IsNullOrWhiteSpace(e.Data)) return;
                int percent = -1;
                string stage = "กำลังเขียนเฟิร์มแวร์";
                var match = ProgressLine.Match(e.Data);
                if (match.Success && image < sizes.Length) {
                    double fraction = Math.Min(100, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) / 100;
                    percent = (int)(98 * (verified + sizes[image] * fraction) / total);
                } else if (e.Data.Contains("Verifying written data")) {
                    stage = "กำลังตรวจสอบข้อมูล";
                    percent = (int)(98 * (verified + (image < sizes.Length ? sizes[image] : 0)) / total);
                } else if (e.Data.Contains("Hash of data verified") && image < sizes.Length) {
                    verified += sizes[image++];
                    percent = (int)(98 * verified / total);
                    stage = image == sizes.Length ? "กำลังเริ่มบอร์ดใหม่" : "ตรวจสอบข้อมูลแล้ว";
                }
                updates.Report(new FlashUpdate(percent, stage, e.Data.Trim()));
            };
            toolProcess.ErrorDataReceived += (s, e) => {
                if (!String.IsNullOrWhiteSpace(e.Data)) updates.Report(new FlashUpdate(-1, "", e.Data.Trim()));
            };
            toolProcess.Start();
            toolProcess.BeginOutputReadLine();
            toolProcess.BeginErrorReadLine();
            if (!toolProcess.WaitForExit(180000)) {
                toolProcess.Kill();
                toolProcess.WaitForExit();
                throw new TimeoutException("หมดเวลาอัปโหลด ตรวจสาย USB แล้วลองอีกครั้ง");
            }
            toolProcess.WaitForExit();
            if (toolProcess.ExitCode != 0)
                throw new InvalidOperationException("ตรวจพอร์ตและสาย USB ปิดโปรแกรมอื่นที่ใช้พอร์ต แล้วลองอีกครั้ง (รหัส " + toolProcess.ExitCode + ")");
        }
    }
}
}
