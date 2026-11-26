using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace CYDMonitor {
// English PDH paths work even on Windows with localized counter names.
public sealed class CpuUtility : IDisposable {
    [StructLayout(LayoutKind.Explicit, Size=16)] struct Value {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Number;
    }
    [DllImport("pdh.dll", CharSet=CharSet.Unicode)] static extern uint PdhOpenQuery(string source,UIntPtr data,out IntPtr query);
    [DllImport("pdh.dll", CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query,string path,UIntPtr data,out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter,uint format,out uint type,out Value value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    IntPtr query,counter;
    bool primed;
    public string Status="CPU: Processor Utility";
    public double? Sample() {
        if(query==IntPtr.Zero) {
            uint error=PdhOpenQuery(null,UIntPtr.Zero,out query);
            if(error==0) error=PdhAddEnglishCounter(query,@"\Processor Information(_Total)\% Processor Utility",UIntPtr.Zero,out counter);
            if(error!=0) { Dispose(); Status="CPU: N/A (Windows counter 0x"+error.ToString("X8")+")"; return null; }
        }
        uint result=PdhCollectQueryData(query);
        if(result!=0) { Dispose(); Status="CPU: N/A (Windows counter 0x"+result.ToString("X8")+")"; return null; }
        if(!primed) { primed=true; Status="CPU: Processor Utility (กำลังเก็บตัวอย่าง)"; return null; }
        uint type; Value value;
        result=PdhGetFormattedCounterValue(counter,0x200,out type,out value); // PDH_FMT_DOUBLE, capped at 100.
        if(result!=0 || value.Status>1 || double.IsNaN(value.Number) || double.IsInfinity(value.Number)) {
            Status="CPU: N/A (Windows counter ไม่พร้อม)"; return null;
        }
        Status="CPU: Processor Utility";
        return Math.Max(0,Math.Min(100,value.Number));
    }
    public void Dispose() { if(query!=IntPtr.Zero) PdhCloseQuery(query); query=counter=IntPtr.Zero; primed=false; }
}
public class Metric {
    public string Id {get;set;}
    public string Name {get;set;}
    public string Label {get;set;}
    public string Unit {get;set;}
    public double? Value;
    public bool Percent;
    public Metric(string id, string name, string label, string unit, bool percent) {
        Id=id; Name=name; Label=label; Unit=unit; Percent=percent;
    }
    public string Display {
        get { return !Value.HasValue ? "N/A" : Id=="uptime" ?
            string.Format(CultureInfo.InvariantCulture,"{0}:{1:00}", (int)(Value.Value/60), (int)(Value.Value%60)) :
            Value.Value.ToString(Value.Value>=1000 ? "0" : "0.0",CultureInfo.InvariantCulture); }
    }
}
public class Settings {
    public string Port="", Title="MY PC", Drive="C:\\", Network="AUTO";
    public string[] Slots={"cpu","cpu_temp","gpu","gpu_temp","ram","download"};
    public int[] Colors={0x50A7FF,0xFFBD59,0xAF87FF,0xFF7D8A,0x4BDAB4,0x50A7FF};
    public int Count=6, Interval=1000, Brightness=200;
    public static Settings Normalize(Settings s) {
        if(s==null) s=new Settings();
        var defaults=new Settings();
        if(s.Slots==null || s.Slots.Length!=6) s.Slots=defaults.Slots;
        if(s.Colors==null || s.Colors.Length!=6) s.Colors=defaults.Colors;
        var ids=new HashSet<string>(Collector.Catalog().Select(m=>m.Id));
        for(int i=0;i<6;i++) {
            if(!ids.Contains(s.Slots[i] ?? "")) s.Slots[i]=defaults.Slots[i];
            s.Colors[i]&=0xFFFFFF;
        }
        s.Count=s.Count==4 ? 4 : 6; s.Interval=Math.Max(500,Math.Min(5000,s.Interval));
        s.Brightness=Math.Max(20,Math.Min(255,s.Brightness));
        s.Title=Protocol.Ascii(s.Title ?? "MY PC",28);
        if(string.IsNullOrEmpty(s.Drive)) s.Drive="C:\\";
        if(string.IsNullOrEmpty(s.Network)) s.Network="AUTO";
        return s;
    }
}
public class Snapshot {
    public List<Metric> Metrics;
    public string SensorStatus;
    public DateTime Time=DateTime.Now;
}
public sealed class Collector : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Mem {
        public uint Length, Load;
        public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref Mem mem);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    readonly CpuUtility cpuUtility=new CpuUtility();
    readonly Dictionary<string,long[]> netOld=new Dictionary<string,long[]>();
    readonly Stopwatch netClock=Stopwatch.StartNew();
    double netTime;
    Computer sensors;
    string sensorState="กำลังอ่านเซ็นเซอร์...";
    bool opened=false;
    PerformanceCounter diskRead,diskWrite;
    public static List<Metric> Catalog() {
        return new List<Metric> {
            new Metric("cpu","CPU • การใช้งาน","CPU LOAD","%",true),
            new Metric("cpu_temp","CPU • อุณหภูมิ","CPU TEMP","C",false),
            new Metric("cpu_clock","CPU • ความถี่เฉลี่ย","CPU CLOCK","MHz",false),
            new Metric("gpu","GPU • การใช้งาน","GPU LOAD","%",true),
            new Metric("gpu_temp","GPU • อุณหภูมิ","GPU TEMP","C",false),
            new Metric("gpu_memory","GPU • หน่วยความจำที่ใช้","GPU MEMORY","GB",false),
            new Metric("ram","RAM • เปอร์เซ็นต์ที่ใช้","RAM LOAD","%",true),
            new Metric("ram_used","RAM • ปริมาณที่ใช้","RAM USED","GB",false),
            new Metric("ram_free","RAM • ปริมาณว่าง","RAM FREE","GB",false),
            new Metric("disk_free","ดิสก์ • พื้นที่ว่าง","DISK FREE","GB",false),
            new Metric("disk_used","ดิสก์ • เปอร์เซ็นต์ที่ใช้","DISK USED","%",true),
            new Metric("disk_read","ดิสก์ทั้งหมด • ความเร็วอ่าน","DISK READ","MB/s",false),
            new Metric("disk_write","ดิสก์ทั้งหมด • ความเร็วเขียน","DISK WRITE","MB/s",false),
            new Metric("download","เครือข่าย • ดาวน์โหลด","DOWNLOAD","MB/s",false),
            new Metric("upload","เครือข่าย • อัปโหลด","UPLOAD","MB/s",false),
            new Metric("uptime","ระยะเวลาที่เปิดคอม","UPTIME","h:mm",false),
            new Metric("off","เว้นช่องว่าง","EMPTY","",false)
        };
    }
    void Initialize() {
        opened=true;
        try {
            sensors=new Computer { IsCpuEnabled=true, IsGpuEnabled=true };
            sensors.Open(); sensorState="เซ็นเซอร์พร้อม • ค่า N/A หมายถึงอ่านไม่ได้";
        } catch(Exception e) { sensorState="เซ็นเซอร์บางส่วนอ่านไม่ได้: "+e.Message; }
        try {
            diskRead=new PerformanceCounter("PhysicalDisk","Disk Read Bytes/sec","_Total",true);
            diskWrite=new PerformanceCounter("PhysicalDisk","Disk Write Bytes/sec","_Total",true);
            diskRead.NextValue(); diskWrite.NextValue();
        } catch { if(diskRead!=null) diskRead.Dispose(); if(diskWrite!=null) diskWrite.Dispose(); diskRead=null; diskWrite=null; }
    }
    static IEnumerable<IHardware> All(IHardware h) {
        yield return h;
        foreach(var sub in h.SubHardware) foreach(var nested in All(sub)) yield return nested;
    }
    public Snapshot Sample(string drive,string network) {
        if(!opened) Initialize();
        var result=new Snapshot { Metrics=Catalog(), SensorStatus=sensorState };
        var map=result.Metrics.ToDictionary(m=>m.Id);
        map["cpu"].Value=cpuUtility.Sample();
        var mem=new Mem(); mem.Length=(uint)Marshal.SizeOf(typeof(Mem));
        if(GlobalMemoryStatusEx(ref mem)) {
            map["ram"].Value=mem.Load; map["ram_used"].Value=(mem.Total-mem.Available)/1073741824.0;
            map["ram_free"].Value=mem.Available/1073741824.0;
        }
        map["uptime"].Value=GetTickCount64()/60000.0;
        try {
            var d=new DriveInfo(drive);
            if(d.IsReady && d.TotalSize>0) {
                map["disk_free"].Value=d.AvailableFreeSpace/1073741824.0;
                map["disk_used"].Value=100.0*(d.TotalSize-d.TotalFreeSpace)/d.TotalSize;
            }
        } catch { }
        try {
            if(diskRead!=null) map["disk_read"].Value=diskRead.NextValue()/1000000.0;
            if(diskWrite!=null) map["disk_write"].Value=diskWrite.NextValue()/1000000.0;
        } catch { }
        double now=netClock.Elapsed.TotalSeconds, elapsed=now-netTime;
        long rx=0,tx=0; bool primed=false;
        try {
            foreach(var n in NetworkInterface.GetAllNetworkInterfaces()) {
                if(n.OperationalStatus!=OperationalStatus.Up || n.NetworkInterfaceType==NetworkInterfaceType.Loopback ||
                   n.NetworkInterfaceType==NetworkInterfaceType.Tunnel) continue;
                var stat=n.GetIPStatistics(); long[] old;
                if((network=="AUTO" || network==n.Id) && netOld.TryGetValue(n.Id,out old) && elapsed>0.05) {
                    rx+=Math.Max(0,stat.BytesReceived-old[0]); tx+=Math.Max(0,stat.BytesSent-old[1]); primed=true;
                }
                netOld[n.Id]=new long[]{stat.BytesReceived,stat.BytesSent};
            }
            if(primed) { map["download"].Value=rx/elapsed/1000000.0; map["upload"].Value=tx/elapsed/1000000.0; }
        } catch { }
        netTime=now;
        if(sensors!=null) {
            try {
                var hardware=sensors.Hardware;
                foreach(var root in hardware) foreach(var h in All(root)) h.Update();
                var cpu=hardware.Where(h=>h.HardwareType==HardwareType.Cpu).SelectMany(h=>All(h)).SelectMany(h=>h.Sensors).ToList();
                var temperatures=cpu.Where(s=>s.SensorType==SensorType.Temperature && s.Value.HasValue && s.Value.Value>0 && s.Value.Value<150).ToList();
                if(temperatures.Count>0) map["cpu_temp"].Value=temperatures.Max(s=>(double)s.Value.Value);
                var clocks=cpu.Where(s=>s.SensorType==SensorType.Clock && s.Value.HasValue && s.Value.Value>0 && s.Name.IndexOf("Core",StringComparison.OrdinalIgnoreCase)>=0).ToList();
                if(clocks.Count>0) map["cpu_clock"].Value=clocks.Average(s=>(double)s.Value.Value);
                // Use the first dedicated GPU where available; keep load, temperature and VRAM on the same device.
                var gpu=hardware.FirstOrDefault(h=>h.HardwareType==HardwareType.GpuNvidia || h.HardwareType==HardwareType.GpuAmd) ??
                    hardware.FirstOrDefault(h=>h.HardwareType==HardwareType.GpuIntel);
                if(gpu!=null) {
                    var gs=All(gpu).SelectMany(h=>h.Sensors).Where(s=>s.Value.HasValue).ToList();
                    var load=gs.FirstOrDefault(s=>s.SensorType==SensorType.Load && s.Name=="GPU Core");
                    var temp=gs.FirstOrDefault(s=>s.SensorType==SensorType.Temperature && s.Name=="GPU Core") ?? gs.FirstOrDefault(s=>s.SensorType==SensorType.Temperature);
                    var memory=gs.FirstOrDefault(s=>s.SensorType==SensorType.SmallData && s.Name=="GPU Memory Used");
                    if(load!=null) map["gpu"].Value=load.Value;
                    if(temp!=null && temp.Value.Value>0 && temp.Value.Value<150) map["gpu_temp"].Value=temp.Value;
                    if(memory!=null) map["gpu_memory"].Value=memory.Value/1024.0;
                    result.SensorStatus="GPU: "+gpu.Name+" • CPU TEMP ใช้ค่าสูงสุดที่อ่านได้";
                }
            } catch(Exception e) { result.SensorStatus="เซ็นเซอร์อ่านไม่ได้: "+e.Message; }
        }
        foreach(var m in result.Metrics) if(m.Value.HasValue && (double.IsNaN(m.Value.Value)||double.IsInfinity(m.Value.Value))) m.Value=null;
        result.SensorStatus+=" • "+cpuUtility.Status;
        return result;
    }
    public void Dispose() {
        cpuUtility.Dispose();
        if(sensors!=null) try { sensors.Close(); } catch { }
        if(diskRead!=null) diskRead.Dispose(); if(diskWrite!=null) diskWrite.Dispose();
    }
}
public static class Protocol {
    public static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=16384 };
    public static string Ascii(string s,int length) {
        return new string((s??"").Take(length).Select(c=>c>=32 && c<=126 ? c : '?').ToArray());
    }
    public static bool IsHello(Dictionary<string,object> msg) {
        object type,device,version;
        try { return msg!=null && msg.TryGetValue("type",out type) && (string)type=="hello" &&
            msg.TryGetValue("device",out device) && (string)device=="CYD_PC_MONITOR" &&
            msg.TryGetValue("v",out version) && Convert.ToInt32(version)==1; } catch { return false; }
    }
    public static bool AcceptAck(uint ack,uint sent,uint previous) { return ack>previous && ack<=sent; }
    public static Dictionary<string,object> Packet(Settings settings,Snapshot snapshot,uint seq) {
        var map=snapshot.Metrics.ToDictionary(m=>m.Id);
        var cards=new List<object>();
        for(int i=0;i<settings.Count;i++) {
            Metric m; if(!map.TryGetValue(settings.Slots[i],out m)) m=map["off"];
            cards.Add(new Dictionary<string,object> {
                {"label",m.Label},{"value",m.Id=="off" ? "--" : m.Display},{"unit",m.Unit},
                {"bar",m.Percent && m.Value.HasValue ? Math.Max(0,Math.Min(100,m.Value.Value)) : -1},
                {"color",settings.Colors[i]&0xFFFFFF}
            });
        }
        return new Dictionary<string,object> {
            {"type","frame"},{"v",1},{"seq",seq},{"title",Ascii(settings.Title,28)},
            {"clock",snapshot.Time.ToString("HH:mm:ss",CultureInfo.InvariantCulture)},
            {"brightness",settings.Brightness},{"cards",cards}
        };
    }
}
public class ScreenPreview : Control {
    public Settings Config=new Settings();
    public Snapshot Data=new Snapshot { Metrics=Collector.Catalog() };
    public ScreenPreview() { DoubleBuffered=true; Size=new Size(480,360); }
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics; g.ScaleTransform(Width/320f,Height/240f);
        g.Clear(Color.FromArgb(8,12,25));
        using(var small=new Font("Consolas",7,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var big=new Font("Consolas",22,FontStyle.Bold,GraphicsUnit.Pixel))
        using(var medium=new Font("Consolas",16,FontStyle.Bold,GraphicsUnit.Pixel)) {
            g.DrawString(Protocol.Ascii(Config.Title,28),small,Brushes.White,8,5);
            g.DrawString(Data.Time.ToString("HH:mm:ss"),small,Brushes.White,260,5);
            g.DrawString("USB / PC MONITOR",small,Brushes.MediumAquamarine,8,20);
            var map=Data.Metrics.ToDictionary(m=>m.Id);
            for(int i=0;i<Config.Count;i++) {
                int h=Config.Count==4 ? 86 : 60;
                int x=6+(i%2)*158,y=39+(i/2)*(h+5),cy=y+(h-60)/2;
                Metric m; if(!map.TryGetValue(Config.Slots[i],out m)) m=map["off"];
                using(var bg=new SolidBrush(Color.FromArgb(16,22,41))) g.FillRectangle(bg,x,y,152,h);
                using(var color=new SolidBrush(Color.FromArgb(Config.Colors[i]|unchecked((int)0xFF000000)))) {
                    g.DrawString(m.Label,small,color,x+8,cy+6);
                    var value=m.Id=="off" ? "--" : m.Display;
                    g.DrawString(value,value.Length>7 ? medium : big,Brushes.White,x+8,cy+17);
                    g.DrawString(m.Unit,small,Brushes.LightSlateGray,x+8,cy+46);
                    if(m.Percent && m.Value.HasValue) {
                        g.FillRectangle(Brushes.MidnightBlue,x+50,cy+49,94,4);
                        g.FillRectangle(color,x+50,cy+49,(float)(94*Math.Max(0,Math.Min(100,m.Value.Value))/100),4);
                    }
                }
            }
            g.DrawString("TAP: BRIGHTNESS    BOOT: INVERT COLORS",small,Brushes.LightSlateGray,8,229);
        }
    }
}
public class MonitorForm : Form {
    Settings config;
    readonly string configPath;
    readonly Collector collector=new Collector();
    readonly BackgroundWorker worker=new BackgroundWorker();
    readonly System.Windows.Forms.Timer ticker=new System.Windows.Forms.Timer();
    readonly ScreenPreview preview=new ScreenPreview();
    readonly ComboBox ports=new ComboBox(), layout=new ComboBox(), drives=new ComboBox(), networks=new ComboBox();
    readonly ComboBox[] slots=new ComboBox[6]; readonly Button[] colorButtons=new Button[6];
    readonly TextBox title=new TextBox();
    readonly NumericUpDown interval=new NumericUpDown(), brightness=new NumericUpDown();
    readonly Label status=new Label(), sensorStatus=new Label();
    readonly Button connect=new Button();
    readonly NotifyIcon tray=new NotifyIcon();
    SerialPort serial;
    bool identified=false, closing=false, restored=false;
    DateTime openedAt,lastPing,lastAck;
    uint seq=0, lastSent=0, lastAckSeq=0;
    string rx="",sampleDrive,sampleNetwork;
    Snapshot latest;
    public MonitorForm(string settingsOverride) {
        configPath=settingsOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CYD-PC-Monitor","settings.json");
        config=new Settings();
        string loadWarning="";
        try { if(File.Exists(configPath)) config=Settings.Normalize(Protocol.Json.Deserialize<Settings>(File.ReadAllText(configPath))); }
        catch { loadWarning="อ่านค่าที่บันทึกไม่ได้ • ใช้ค่าเริ่มต้น"; }
        BindingContext=new BindingContext();
        Text="CYD PC Monitor • ตั้งค่าจอคอมพิวเตอร์"; ClientSize=new Size(1050,710);
        MinimumSize=new Size(1070,750); AutoScaleMode=AutoScaleMode.Dpi;
        BackColor=Color.FromArgb(242,245,250); Font=new Font("Segoe UI",10);
        var heading=new Label { Text="CYD PC Monitor",Font=new Font("Segoe UI",22,FontStyle.Bold),Bounds=new Rectangle(24,18,640,42) }; Controls.Add(heading);
        LabelAt("เลือกค่าที่แสดงบนบอร์ด • รับข้อมูลจริงจากคอมผ่าน USB",24,64,930,28);
        LabelAt("พอร์ต USB",24,108,100,24); SetCombo(ports,125,104,125); Controls.Add(ports);
        var refresh=ButtonAt("ค้นหาพอร์ต",260,102,110,32); refresh.Click+=(s,e)=>RefreshPorts();
        connect.Text="เชื่อมต่อ"; connect.SetBounds(380,102,130,32); connect.Click+=(s,e)=>ToggleConnection(); Controls.Add(connect);
        LabelAt("ชื่อบนจอ",24,155,95,24); title.SetBounds(125,151,260,30); title.MaxLength=28; Controls.Add(title);
        LabelAt("จำนวนช่อง",395,155,85,24); SetCombo(layout,482,151,65); layout.Items.AddRange(new object[]{"6","4"}); Controls.Add(layout);
        LabelAt("อัปเดต (ms)",24,199,100,24); interval.SetBounds(125,195,125,30); interval.Minimum=500; interval.Maximum=5000; interval.Increment=500; Controls.Add(interval);
        LabelAt("ความสว่าง",270,199,110,24); brightness.SetBounds(395,195,152,30); brightness.Minimum=20; brightness.Maximum=255; Controls.Add(brightness);
        LabelAt("ดิสก์",24,243,95,24); SetCombo(drives,125,239,125); Controls.Add(drives);
        LabelAt("เครือข่าย",270,243,110,24); SetCombo(networks,395,239,152); Controls.Add(networks);
        foreach(var d in DriveInfo.GetDrives()) if(d.IsReady) drives.Items.Add(d.Name);
        networks.DisplayMember="Name"; networks.ValueMember="Id";
        networks.Items.Add(new NetChoice { Id="AUTO",Name="รวมทุกการเชื่อมต่อ" });
        try { foreach(var n in NetworkInterface.GetAllNetworkInterfaces()) if(n.NetworkInterfaceType!=NetworkInterfaceType.Loopback) networks.Items.Add(new NetChoice {Id=n.Id,Name=n.Name}); } catch { }
        var catalog=Collector.Catalog();
        for(int i=0;i<6;i++) {
            LabelAt("ช่อง "+(i+1),24,294+i*45,95,24);
            slots[i]=new ComboBox(); SetCombo(slots[i],125,290+i*45,330);
            slots[i].DisplayMember="Name"; slots[i].ValueMember="Id";
            slots[i].DataSource=Collector.Catalog(); Controls.Add(slots[i]);
            colorButtons[i]=ButtonAt("สี",465,289+i*45,82,32); int index=i;
            colorButtons[i].Click+=(s,e)=> {
                using(var dialog=new ColorDialog { Color=Color.FromArgb(config.Colors[index]|unchecked((int)0xFF000000)) }) {
                    if(dialog.ShowDialog(this)==DialogResult.OK) { config.Colors[index]=dialog.Color.ToArgb()&0xFFFFFF; Changed(); }
                }
            };
        }
        LabelAt("ตัวอย่างหน้าจอ • ค่าจริง",578,112,450,28);
        preview.SetBounds(578,151,448,336); Controls.Add(preview);
        LabelAt("N/A = ไม่มีข้อมูลจากเซ็นเซอร์ / กำลังเริ่มอ่าน\nอุณหภูมิ CPU อาจต้องติดตั้ง PawnIO และเปิดแบบ Administrator",578,505,450,58);
        var sensorHelp=ButtonAt("วิธีเปิดข้อมูลอุณหภูมิ",578,575,230,38);
        sensorHelp.Click+=(s,e)=> {
            MessageBox.Show(this,"หากอุณหภูมิเป็น N/A ให้ติดตั้ง PawnIO จากเว็บไซต์ทางการ แล้วปิดเปิดแอปด้วย Run as administrator\n\nแอปนี้ไม่ติดตั้งไดรเวอร์เอง และบางฮาร์ดแวร์อาจไม่รองรับ\n\nhttps://pawnio.eu/\nhttps://github.com/LibreHardwareMonitor/LibreHardwareMonitor", "ข้อมูลเซ็นเซอร์",MessageBoxButtons.OK,MessageBoxIcon.Information);
        };
        var save=ButtonAt("บันทึกการตั้งค่า",24,577,180,38); save.Click+=(s,e)=>SaveConfig();
        var minimize=ButtonAt("ซ่อนไว้ข้างนาฬิกา",214,577,190,38); minimize.Click+=(s,e)=> { tray.Visible=true; Hide(); };
        status.SetBounds(24,630,1000,28); status.ForeColor=Color.FromArgb(30,90,145); Controls.Add(status);
        sensorStatus.SetBounds(24,665,1000,28); sensorStatus.AutoEllipsis=true; Controls.Add(sensorStatus);
        title.Text=config.Title; interval.Value=config.Interval; brightness.Value=config.Brightness;
        layout.SelectedItem=config.Count.ToString(); drives.SelectedItem=config.Drive;
        if(drives.SelectedIndex<0 && drives.Items.Count>0) drives.SelectedIndex=0;
        networks.SelectedIndex=0;
        for(int i=0;i<networks.Items.Count;i++) if(((NetChoice)networks.Items[i]).Id==config.Network) networks.SelectedIndex=i;
        for(int i=0;i<6;i++) slots[i].SelectedValue=config.Slots[i];
        title.TextChanged+=(s,e)=>Changed(); interval.ValueChanged+=(s,e)=>Changed(); brightness.ValueChanged+=(s,e)=>Changed();
        layout.SelectedIndexChanged+=(s,e)=>Changed(); drives.SelectedIndexChanged+=(s,e)=>Changed(); networks.SelectedIndexChanged+=(s,e)=>Changed();
        foreach(var slot in slots) slot.SelectedIndexChanged+=(s,e)=>Changed();
        var initial=Settings.Normalize(Protocol.Json.Deserialize<Settings>(Protocol.Json.Serialize(config)));
        Load+=(s,e)=> {
            restored=false; config=initial;
            layout.SelectedIndex=config.Count==4 ? 1 : 0;
            drives.SelectedItem=config.Drive;
            if(drives.SelectedIndex<0 && drives.Items.Count>0) drives.SelectedIndex=0;
            networks.SelectedIndex=0;
            for(int n=0;n<networks.Items.Count;n++) if(((NetChoice)networks.Items[n]).Id==config.Network) networks.SelectedIndex=n;
            for(int n=0;n<6;n++) slots[n].SelectedIndex=Collector.Catalog().FindIndex(m=>m.Id==config.Slots[n]);
            restored=true; Changed(); RefreshPorts();
        };
        status.Text=loadWarning.Length>0 ? loadWarning : "เลือกพอร์ตแล้วกดเชื่อมต่อ • ดูตัวอย่างได้โดยไม่ต่อบอร์ด";
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon=Icon; tray.Text="CYD PC Monitor";
        var upload=ButtonAt("อัปโหลดเฟิร์มแวร์",24,705,190,36);
        upload.Click+=(s,e)=>UploadSelectedPort();
        var startup=new CheckBox { Text="เปิดพร้อม Windows (ซ่อนไว้ข้างนาฬิกา)",Bounds=new Rectangle(234,705,380,36) };
        Controls.Add(startup);
        try { startup.Checked=WindowsStartup.Enabled; }
        catch(Exception e) { startup.Enabled=false; status.Text=e.Message; }
        startup.Click+=(s,e)=> {
            try { WindowsStartup.SetEnabled(startup.Checked); }
            catch(Exception error) { startup.Checked=!startup.Checked; MessageBox.Show(this,error.Message,"ตั้งค่าเปิดพร้อม Windows ไม่สำเร็จ"); }
        };
        ClientSize=new Size(1050,755);
        MinimumSize=Size;
        var menu=new ContextMenuStrip(); menu.Items.Add("เปิดหน้าตั้งค่า",null,(s,e)=>Restore()); menu.Items.Add("ออกจากแอป",null,(s,e)=>Close()); tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(s,e)=>Restore();
        worker.DoWork+=(s,e)=> { e.Result=collector.Sample(sampleDrive,sampleNetwork); };
        worker.RunWorkerCompleted+=(s,e)=> {
            if(closing) { collector.Dispose(); return; }
            if(e.Error!=null) { sensorStatus.Text="อ่านข้อมูลไม่ได้: "+e.Error.Message; return; }
            latest=(Snapshot)e.Result; preview.Data=latest; preview.Invalidate(); sensorStatus.Text=latest.SensorStatus;
            SendFrame();
        };
        ticker.Interval=100; ticker.Tick+=(s,e)=>Tick(); ticker.Start();
        FormClosing+=(s,e)=> {
            closing=true; ticker.Stop(); Disconnect(""); tray.Dispose();
            if(!worker.IsBusy) collector.Dispose();
        };
    }
    class NetChoice { public string Id {get;set;} public string Name {get;set;} }
    Label LabelAt(string text,int x,int y,int width,int height) {
        var l=new Label {Text=text,Bounds=new Rectangle(x,y,width,height)}; Controls.Add(l); return l;
    }
    Button ButtonAt(string text,int x,int y,int width,int height) {
        var b=new Button {Text=text,Bounds=new Rectangle(x,y,width,height),FlatStyle=FlatStyle.Flat}; Controls.Add(b); return b;
    }
    static void SetCombo(ComboBox c,int x,int y,int width) {
        c.SetBounds(x,y,width,30); c.DropDownStyle=ComboBoxStyle.DropDownList;
        c.DrawMode=DrawMode.OwnerDrawFixed; c.ItemHeight=23;
        c.DrawItem+=(s,e)=> {
            e.DrawBackground();
            if(e.Index>=0) TextRenderer.DrawText(e.Graphics,c.GetItemText(c.Items[e.Index]),c.Font,e.Bounds,c.Enabled ? c.ForeColor : SystemColors.GrayText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
    }
    void Restore() { ShowInTaskbar=true; Show(); WindowState=FormWindowState.Normal; Activate(); tray.Visible=false; }
    public void StartInTray() {
        ShowInTaskbar=false;
        WindowState=FormWindowState.Minimized;
        Shown+=(s,e)=> {
            tray.Visible=true;
            Hide();
            if(ports.Items.Contains(config.Port)) ConnectTo(config.Port);
        };
    }
    void UploadSelectedPort() {
        string port=ports.SelectedItem as string;
        if(port==null) { status.Text="เลือกพอร์ต USB ก่อนอัปโหลด"; return; }
        if(MessageBox.Show(this,"ติดตั้ง PC Monitor 1.2 ลงบอร์ด CYD ESP32-2432S028R ที่ "+port+"?\nโปรแกรมเดิมในบอร์ดจะถูกแทนที่ กรุณาเสียบสาย USB ไว้จนเสร็จ",
            "อัปโหลดเฟิร์มแวร์",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.OK) return;
        bool wasConnected=serial!=null;
        Disconnect("กำลังอัปโหลดเฟิร์มแวร์");
        using(var dialog=new FirmwareUpload(port,Icon)) {
            dialog.ShowDialog(this);
            RefreshPorts();
            if((dialog.Succeeded || wasConnected) && ports.Items.Contains(port)) ConnectTo(port);
            else if(dialog.Succeeded) status.Text="อัปโหลดสำเร็จ • เลือกพอร์ต USB ของบอร์ดแล้วกดเชื่อมต่อ";
            else status.Text="อัปโหลดไม่สำเร็จ • ตรวจสาย USB หรือค้นหาพอร์ตอีกครั้ง";
        }
    }
    void Changed() {
        if(!restored) return;
        config.Title=title.Text; config.Count=layout.SelectedItem as string=="4" ? 4 : 6;
        config.Interval=(int)interval.Value; config.Brightness=(int)brightness.Value;
        config.Drive=drives.SelectedItem as string ?? "C:\\";
        var net=networks.SelectedItem as NetChoice; config.Network=net==null ? "AUTO" : net.Id;
        for(int i=0;i<6;i++) {
            config.Slots[i]=slots[i].SelectedValue as string ?? "off";
            slots[i].Enabled=i<config.Count; colorButtons[i].Enabled=i<config.Count;
            colorButtons[i].BackColor=Color.FromArgb(config.Colors[i]|unchecked((int)0xFF000000));
        }
        preview.Config=config; preview.Invalidate();
    }
    void SaveConfig() {
        try {
            config.Port=ports.SelectedItem as string ?? "";
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            var temp=configPath+".tmp"; File.WriteAllText(temp,Protocol.Json.Serialize(config),Encoding.UTF8);
            if(File.Exists(configPath)) File.Replace(temp,configPath,null); else File.Move(temp,configPath);
            status.Text="บันทึกแล้ว • เปิดครั้งถัดไปจะใช้ค่าชุดนี้";
        } catch(Exception e) { status.Text="บันทึกไม่ได้: "+e.Message; }
    }
    void RefreshPorts() {
        string previous=ports.SelectedItem as string ?? config.Port;
        ports.Items.Clear(); ports.Items.AddRange(SerialPort.GetPortNames().OrderBy(p=>p).Cast<object>().ToArray());
        if(ports.Items.Contains(previous)) ports.SelectedItem=previous;
        else if(ports.Items.Contains("COM19")) ports.SelectedItem="COM19";
        else if(ports.Items.Count>0) ports.SelectedIndex=0;
    }
    void ToggleConnection() {
        if(serial!=null) { Disconnect("ยกเลิกการเชื่อมต่อแล้ว"); return; }
        if(ports.SelectedItem==null) { status.Text="ยังไม่พบพอร์ต • ต่อสาย USB ที่ส่งข้อมูลได้ แล้วค้นหาอีกครั้ง"; return; }
        try {
            serial=new SerialPort((string)ports.SelectedItem,115200) { Encoding=Encoding.ASCII, NewLine="\n", DtrEnable=false,RtsEnable=false,ReadTimeout=100,WriteTimeout=250 };
            serial.Open();
            identified=false; rx=""; openedAt=DateTime.UtcNow; lastPing=DateTime.MinValue; lastAckSeq=seq;
            connect.Text="ยกเลิก"; ports.Enabled=false; status.Text="กำลังตรวจว่าบอร์ดใช้โปรแกรม PC Monitor...";
        } catch(Exception e) { Disconnect("เปิดพอร์ตไม่ได้: "+e.Message+" • ปิด Serial Monitor ของ Arduino ก่อน"); }
    }
    void Disconnect(string message) {
        if(serial!=null) { try { serial.Close(); serial.Dispose(); } catch { } serial=null; }
        identified=false; ports.Enabled=true; connect.Text="เชื่อมต่อ"; if(message.Length>0) status.Text=message;
    }
    DateTime lastSample=DateTime.MinValue;
    void Tick() {
        var now=DateTime.UtcNow;
        if(!worker.IsBusy && (now-lastSample).TotalMilliseconds>=config.Interval) {
            lastSample=now; sampleDrive=config.Drive; sampleNetwork=config.Network; worker.RunWorkerAsync();
        }
        if(serial==null) return;
        try {
            rx+=serial.ReadExisting();
            if(rx.Length>8192) { rx=""; Disconnect("ข้อมูลจากพอร์ตไม่ถูกต้อง • เชื่อมต่อใหม่"); return; }
            int newline;
            while((newline=rx.IndexOf('\n'))>=0) {
                string line=rx.Substring(0,newline).Trim(); rx=rx.Substring(newline+1);
                try {
                    var msg=Protocol.Json.Deserialize<Dictionary<string,object>>(line);
                    object type,v;
                    if(msg.TryGetValue("type",out type) && msg.TryGetValue("v",out v) && Convert.ToInt32(v)==1) {
                        if(Protocol.IsHello(msg)) {
                            identified=true; lastAck=now; status.Text="พบบอร์ดแล้ว • รอรับการยืนยันข้อมูล"; SendFrame();
                        } else if((string)type=="ack" && identified) {
                            object ackSeq;
                            if(msg.TryGetValue("seq",out ackSeq) && Protocol.AcceptAck(Convert.ToUInt32(ackSeq),lastSent,lastAckSeq)) {
                                lastAckSeq=Convert.ToUInt32(ackSeq); lastAck=now; status.Text="เชื่อมต่อ "+serial.PortName+" • บอร์ดยืนยันข้อมูลแล้ว • "+DateTime.Now.ToString("HH:mm:ss");
                            }
                        }
                    }
                } catch { /* Ignore boot logs and unrelated serial messages. */ }
            }
            if(!identified) {
                if((now-openedAt).TotalSeconds>6) { Disconnect("ยังไม่พบโปรแกรม PC Monitor • ต้องอัปโหลด CYD_PCMonitor ลงบอร์ดก่อน"); return; }
                if((now-lastPing).TotalSeconds>=1) { serial.WriteLine("PING"); lastPing=now; }
            } else if((now-lastAck).TotalSeconds>8) Disconnect("บอร์ดหยุดยืนยันข้อมูล • ตรวจสาย USB แล้วเชื่อมต่อใหม่");
        } catch(Exception e) { Disconnect("การเชื่อมต่อหลุด: "+e.Message); }
    }
    void SendFrame() {
        if(serial==null || !identified || latest==null) return;
        try { lastSent=++seq; serial.WriteLine(Protocol.Json.Serialize(Protocol.Packet(config,latest,seq))); }
        catch(Exception e) { Disconnect("ส่งข้อมูลไม่ได้: "+e.Message); }
    }
    public void SetTestSnapshot(Snapshot snapshot) {
        latest=snapshot; preview.Data=snapshot; sensorStatus.Text=snapshot.SensorStatus; preview.Invalidate();
        for(int i=0;i<6;i++) if((slots[i].SelectedValue as string)!=config.Slots[i]) throw new Exception("UI selection binding");
        if(config.Slots.All(id=>id=="off") || layout.SelectedIndex<0 || networks.SelectedIndex<0) throw new Exception("UI default selections");
    }
    public void ExerciseSettings() {
        title.Text="TEST PC"; layout.SelectedIndex=1; brightness.Value=77;
        slots[0].SelectedIndex=Collector.Catalog().FindIndex(m=>m.Id=="disk_free");
        config.Colors[0]=0x123456; Changed(); SaveConfig();
        var saved=Settings.Normalize(Protocol.Json.Deserialize<Settings>(File.ReadAllText(configPath)));
        if(saved.Title!="TEST PC" || saved.Count!=4 || saved.Brightness!=77 || saved.Slots[0]!="disk_free" || saved.Colors[0]!=0x123456)
            throw new Exception("UI save roundtrip");
    }
    public void CheckRestoredSettings() {
        if(title.Text!="TEST PC" || layout.SelectedIndex!=1 || brightness.Value!=77 ||
            slots[0].SelectedValue as string!="disk_free" || config.Colors[0]!=0x123456) throw new Exception("UI restore roundtrip");
    }
    public void ConnectTo(string port) {
        if(!ports.Items.Contains(port)) throw new Exception("Port not found: "+port);
        ports.SelectedItem=port; ToggleConnection();
    }
    public bool BoardConfirmed { get { return identified && lastAckSeq>0; } }
    public void CapturePreview(string path) {
        using(var bitmap=new Bitmap(Width,Height)) {
            DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));
            // Native dropdown text is omitted by WM_PRINT. Render the actual selected text in the export.
            Point origin=PointToScreen(Point.Empty); origin.Offset(-Left,-Top);
            using(var g=Graphics.FromImage(bitmap)) foreach(Control control in Controls) {
                var combo=control as ComboBox;
                if(combo==null || combo.SelectedItem==null) continue;
                var bounds=new Rectangle(origin.X+combo.Left+4,origin.Y+combo.Top+2,combo.Width-25,combo.Height-4);
                g.FillRectangle(combo.Enabled ? SystemBrushes.Window : SystemBrushes.Control,bounds);
                TextRenderer.DrawText(g,combo.GetItemText(combo.SelectedItem),combo.Font,bounds,
                    combo.Enabled ? combo.ForeColor : SystemColors.GrayText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            }
            bitmap.Save(path);
        }
    }
}
public static class Program {
    [STAThread] public static int Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>=3 && args[0]=="--app-test") {
            string output=Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
            try {
                string settings=Path.Combine(output,"app-test-settings.json"); File.WriteAllText(settings,Protocol.Json.Serialize(new Settings()));
                using(var form=new MonitorForm(settings)) {
                    form.Show(); Application.DoEvents(); form.ConnectTo(args[1]);
                    var watch=Stopwatch.StartNew();
                    while(watch.Elapsed.TotalSeconds<10) { Application.DoEvents(); Thread.Sleep(50); }
                    if(!form.BoardConfirmed) throw new Exception("No board ACK through app");
                    form.CapturePreview(Path.Combine(output,"App_Connected.png")); form.Close();
                }
                File.WriteAllText(Path.Combine(output,"app-device-test.txt"),"PASS: Windows app connected to "+args[1]+", sampled live metrics, transmitted frames and received board ACKs."); return 0;
            } catch(Exception e) { File.WriteAllText(Path.Combine(output,"app-device-test.txt"),"FAIL: "+e); return 1; }
        }
        if(args.Length>0 && args[0]=="--self-test") {
            string output=Path.GetFullPath(args.Length>1 ? args[1] : "."); Directory.CreateDirectory(output);
            try {
                var s=Settings.Normalize(new Settings()); Snapshot snapshot;
                using(var collector=new Collector()) { collector.Sample(s.Drive,s.Network); Thread.Sleep(1100); snapshot=collector.Sample(s.Drive,s.Network); }
                var packet=Protocol.Packet(s,snapshot,1); var text=Protocol.Json.Serialize(packet);
                if(Encoding.ASCII.GetByteCount(text)>=4095 || text.Any(c=>c>127) || ((List<object>)packet["cards"]).Count!=6) throw new Exception("Packet bounds");
                foreach(var id in new[]{"cpu","ram"}) {
                    var m=snapshot.Metrics.First(x=>x.Id==id);
                    if(!m.Value.HasValue || m.Value.Value<0 || m.Value.Value>100) throw new Exception("Windows metric: "+id);
                }
                var missing=Protocol.Packet(s,new Snapshot {Metrics=Collector.Catalog()},3);
                var missingCard=(Dictionary<string,object>)((List<object>)missing["cards"])[0];
                if((string)missingCard["value"]!="N/A" || Convert.ToDouble(missingCard["bar"])!=-1) throw new Exception("Unavailable metrics");
                File.WriteAllText(Path.Combine(output,"sample-frame.json"),text,Encoding.UTF8);
                File.WriteAllText(Path.Combine(output,"metrics.json"),Protocol.Json.Serialize(snapshot),Encoding.UTF8);
                var bad=Settings.Normalize(new Settings { Slots=null,Colors=null,Interval=90000,Count=99,Brightness=0 });
                if(bad.Interval!=5000 || bad.Count!=6 || bad.Brightness!=20 || bad.Slots.Length!=6) throw new Exception("Settings normalization");
                s.Count=4;
                if(((List<object>)Protocol.Packet(s,snapshot,2)["cards"]).Count!=4) throw new Exception("Four-card layout");
                if(Protocol.Ascii("ABC\nไทย",4)!="ABC?") throw new Exception("ASCII filtering");
                if(!Protocol.IsHello(Protocol.Json.Deserialize<Dictionary<string,object>>("{\"type\":\"hello\",\"device\":\"CYD_PC_MONITOR\",\"v\":1}")) ||
                    Protocol.IsHello(Protocol.Json.Deserialize<Dictionary<string,object>>("{\"type\":\"hello\",\"device\":\"WEATHER_CLOCK\",\"v\":1}")) ||
                    Protocol.IsHello(Protocol.Json.Deserialize<Dictionary<string,object>>("{\"type\":\"hello\",\"device\":\"CYD_PC_MONITOR\",\"v\":2}"))) throw new Exception("Device handshake");
                if(!Protocol.AcceptAck(4,5,3) || Protocol.AcceptAck(3,5,3) || Protocol.AcceptAck(6,5,3)) throw new Exception("Delayed/duplicate/future ACK");
                string testSettings=Path.Combine(output,"test-settings.json");
                File.WriteAllText(testSettings,Protocol.Json.Serialize(new Settings()));
                using(var form=new MonitorForm(testSettings)) {
                    form.Show(); Application.DoEvents(); form.SetTestSnapshot(snapshot); Application.DoEvents(); form.CapturePreview(Path.Combine(output,"App_Preview.png"));
                    form.ExerciseSettings(); form.Close();
                }
                using(var restored=new MonitorForm(testSettings)) { restored.Show(); Application.DoEvents(); restored.CheckRestoredSettings(); restored.Close(); }
                File.WriteAllText(Path.Combine(output,"self-test.txt"),"PASS: real Windows metrics, unavailable sensor handling, bounded ASCII JSON, four/six cards, settings validation, device identity/version, delayed/duplicate/future ACK, UI selection binding, save/reopen persistence, UI render.\r\nPorts: "+string.Join(", ",SerialPort.GetPortNames())+"\r\n"+snapshot.SensorStatus);
                return 0;
            } catch(Exception e) { File.WriteAllText(Path.Combine(output,"self-test.txt"),"FAIL: "+e); return 1; }
        }
        var mainForm=new MonitorForm(null);
        if(args.Length==1 && args[0]=="--startup") mainForm.StartInTray();
        if(args.Length>=2 && args[0]=="--connect") mainForm.Shown+=(s,e)=>mainForm.ConnectTo(args[1]);
        Application.Run(mainForm); return 0;
    }
}
}
