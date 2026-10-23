// HasladDNS v1 — customizable IPv4-only DNS profile manager.
// Based on the public project's documented behavior; original project is MIT licensed.
// Profiles remain stored in the existing %AppData%\HB AI Smart DNS\profiles.xml folder to preserve user settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace HbAiSmartDnsCustom
{
    [Serializable]
    public class DnsProfile
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Website { get; set; }
        [XmlArrayItem("Address")]
        public List<string> Addresses { get; set; }
        public DnsProfile() { Name = ""; Description = ""; Website = ""; Addresses = new List<string>(); }
        public override string ToString() { return Name; }
    }

    [Serializable]
    public class ProfileStore
    {
        [XmlArrayItem("Profile")]
        public List<DnsProfile> Profiles { get; set; }
        public ProfileStore() { Profiles = new List<DnsProfile>(); }
    }

    [DataContract]
    public class AppSettings
    {
        [DataMember] public List<string> RecentProfiles { get; set; }
        [DataMember] public bool NotifyDnsChanges { get; set; }
        [DataMember] public bool WarnedAboutIpv6 { get; set; }
        [DataMember] public int HotkeyModifiers { get; set; }
        [DataMember] public int HotkeyKey { get; set; }

        public AppSettings()
        {
            RecentProfiles = new List<string>();
            NotifyDnsChanges = true;
            WarnedAboutIpv6 = false;
            HotkeyModifiers = 0x0001 | 0x0002;
            HotkeyKey = (int)Keys.D;
        }

        public static AppSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                        var loaded = serializer.ReadObject(stream) as AppSettings;
                        if (loaded != null)
                        {
                            if (loaded.RecentProfiles == null) loaded.RecentProfiles = new List<string>();
                            loaded.RecentProfiles = loaded.RecentProfiles.Where(x => !String.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
                            if (loaded.HotkeyModifiers == 0) loaded.HotkeyModifiers = 0x0001 | 0x0002;
                            if (loaded.HotkeyKey == 0) loaded.HotkeyKey = (int)Keys.D;
                            return loaded;
                        }
                    }
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save(string path)
        {
            string temp = path + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(temp))
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, this);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public void RecordProfile(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return;
            RecentProfiles.RemoveAll(x => String.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            RecentProfiles.Insert(0, name);
            if (RecentProfiles.Count > 3) RecentProfiles.RemoveRange(3, RecentProfiles.Count - 3);
        }
    }

    static class DnsEngine
    {
        public static string RunPowerShell(string script)
        {
            // Return PowerShell exceptions as UTF-8 Base64 on stdout to avoid garbled localized stderr.
            string wrapped = "$ErrorActionPreference='Stop'; try {\n" + script +
                "\n} catch { $m = ($_ | Out-String); [Console]::Out.WriteLine('HB_AI_ERROR:' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($m))); exit 1 }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                string marker = "HB_AI_ERROR:";
                int markerAt = stdout.IndexOf(marker, StringComparison.Ordinal);
                if (markerAt >= 0)
                {
                    string b64 = stdout.Substring(markerAt + marker.Length).Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    try { throw new Exception(Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Trim()); }
                    catch (FormatException) { throw new Exception(stdout.Trim()); }
                }
                if (p.ExitCode != 0)
                    throw new Exception(!String.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : (!String.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "PowerShell завершился с кодом " + p.ExitCode + " без текста ошибки."));
                return stdout.Trim();
            }
        }

        public static void Apply(List<string> addresses)
        {
            if (addresses == null || addresses.Count == 0) throw new Exception("В профиле должен быть хотя бы один IPv4 DNS-адрес.");
            // Use netsh's IPv4-only interface. Build every argument explicitly so PowerShell
            // doesn't accidentally pass the literal text "$dns[0]" to netsh.
            string list = String.Join(",", addresses.Select(x => "'" + x + "'").ToArray());
            string script = "$ErrorActionPreference='Stop'; " +
                "$dns=@(" + list + "); " +
                "$a=@(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }); " +
                "if($a.Count -eq 0){throw 'Не найдено активных сетевых адаптеров.'}; " +
                "foreach($x in $a){ " +
                "  $n='name=\"' + $x.Name + '\"'; " +
                "  $out = & netsh.exe interface ipv4 set dnsservers $n source=static ('address=' + [string]$dns[0]) register=primary validate=no 2>&1; " +
                "  if($LASTEXITCODE -ne 0){throw ('Не удалось задать основной IPv4 DNS для адаптера ' + $x.Name + '. netsh: ' + ($out -join ' '))}; " +
                "  for($i=1; $i -lt $dns.Count; $i++){ $out = & netsh.exe interface ipv4 add dnsservers $n ('address=' + [string]$dns[$i]) ('index=' + [string]($i+1)) validate=no 2>&1; if($LASTEXITCODE -ne 0){throw ('Не удалось добавить IPv4 DNS для адаптера ' + $x.Name + '. netsh: ' + ($out -join ' '))} }; " +
                "}; Clear-DnsClientCache -ErrorAction Stop; 'OK'";
            RunPowerShell(script);
        }

        public static void Disable()
        {
            // Reset IPv4 DNS only; leave all IPv6 DNS settings untouched.
            string script = "$ErrorActionPreference='Stop'; " +
                "$a=@(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }); " +
                "if($a.Count -eq 0){throw 'Не найдено активных сетевых адаптеров.'}; " +
                "foreach($x in $a){ $n='name=\"' + $x.Name + '\"'; $out = & netsh.exe interface ipv4 set dnsservers $n source=dhcp 2>&1; if($LASTEXITCODE -ne 0){throw ('Не удалось вернуть автоматический IPv4 DNS для адаптера ' + $x.Name + '. netsh: ' + ($out -join ' '))} }; " +
                "Clear-DnsClientCache -ErrorAction Stop; 'OK'";
            RunPowerShell(script);
        }

        public static bool HasIpv6Dns()
        {
            string output = RunPowerShell("Get-DnsClientServerAddress -AddressFamily IPv6 | Where-Object { $_.ServerAddresses.Count -gt 0 } | ForEach-Object { $_.ServerAddresses }");
            return !String.IsNullOrWhiteSpace(output);
        }

        public static string GetCurrentDns()
        {
            string output = RunPowerShell("Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | ForEach-Object { $_.InterfaceAlias + ': ' + ($_.ServerAddresses -join ', ') }");
            return String.IsNullOrWhiteSpace(output) ? "IPv4 DNS: не обнаружен" : output.Replace("\r\n", " | ").Replace("\n", " | ");
        }

        public static void FlushCache()
        {
            RunPowerShell("Clear-DnsClientCache -ErrorAction Stop; 'DNS-кэш очищен'");
        }

        public static string Nslookup(string domain)
        {
            if (String.IsNullOrWhiteSpace(domain) || domain.Any(Char.IsWhiteSpace) || domain.StartsWith("-") || domain.Contains(";") || domain.Contains("&") || domain.Contains("|"))
                throw new Exception("Введи корректное доменное имя, например example.com.");
            string safe = domain.Replace("'", "''");
            return RunPowerShell("$r = & nslookup.exe '" + safe + "' 2>&1; $r | Out-String");
        }

        public static int DetectProfile(List<DnsProfile> profiles)
        {
            try
            {
                var lines = RunPowerShell("$ErrorActionPreference='Stop'; Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ } | Sort-Object -Unique").Split(new[] { '\r','\n' }, StringSplitOptions.RemoveEmptyEntries);
                var current = new HashSet<string>(lines.Select(Normalize), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < profiles.Count; i++)
                {
                    var expected = new HashSet<string>(profiles[i].Addresses.Select(Normalize), StringComparer.OrdinalIgnoreCase);
                    if (expected.Count > 0 && expected.IsSubsetOf(current)) return i;
                }
            } catch { }
            return -1;
        }

        public static string Normalize(string s)
        {
            IPAddress ip;
            return IPAddress.TryParse((s ?? "").Trim(), out ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : (s ?? "").Trim();
        }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    static class ThemeSettings
    {
        public static bool Dark = true;
        public static string SettingsPath;

        public static void Load(string dataDir)
        {
            SettingsPath = Path.Combine(dataDir, "theme.txt");
            try {
                if (File.Exists(SettingsPath)) {
                    bool parsed;
                    if (Boolean.TryParse(File.ReadAllText(SettingsPath).Trim(), out parsed)) Dark = parsed;
                }
            } catch { Dark = true; }
        }

        public static void Save()
        {
            try {
                if (!String.IsNullOrEmpty(SettingsPath)) File.WriteAllText(SettingsPath, Dark ? "true" : "false");
            } catch { }
        }

        public static Color Background { get { return Dark ? Color.FromArgb(12,12,17) : Color.FromArgb(244,246,250); } }
        public static Color Surface { get { return Dark ? Color.FromArgb(23,23,32) : Color.FromArgb(255,255,255); } }
        public static Color Input { get { return Dark ? Color.FromArgb(10,10,15) : Color.FromArgb(255,255,255); } }
        public static Color Foreground { get { return Dark ? Color.FromArgb(242,242,248) : Color.FromArgb(32,36,46); } }
        public static Color Muted { get { return Dark ? Color.FromArgb(155,155,170) : Color.FromArgb(94,101,116); } }
        public static Color Accent { get { return Dark ? Color.FromArgb(124,92,255) : Color.FromArgb(78,91,210); } }
        public static Color AccentText { get { return Color.White; } }
        public static Color Border { get { return Dark ? Color.FromArgb(65,65,78) : Color.FromArgb(205,211,223); } }

        public static void Apply(Control root)
        {
            root.BackColor = root is Form ? Background : root is Panel ? Surface : root.BackColor;
            if (root is Form) root.ForeColor = Foreground;
            foreach (Control c in root.Controls) {
                if (c is Label) {
                    c.ForeColor = Foreground;
                    c.BackColor = Color.Transparent;
                } else if (c is Button) {
                    c.BackColor = Accent;
                    c.ForeColor = AccentText;
                    ((Button)c).FlatStyle = FlatStyle.Flat;
                    ((Button)c).FlatAppearance.BorderColor = Border;
                    ((Button)c).FlatAppearance.BorderSize = 1;
                } else if (c is TextBox) {
                    c.BackColor = Input;
                    c.ForeColor = Foreground;
                    ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                } else if (c is ComboBox) {
                    c.BackColor = Input;
                    c.ForeColor = Foreground;
                    ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                } else if (c is Panel) {
                    c.BackColor = Surface;
                }
                if (c.HasChildren) Apply(c);
            }
        }
    }

    class ProfileEditor : Form
    {
        public DnsProfile Result;
        TextBox nameBox, descBox, webBox, addrBox;
        public ProfileEditor(DnsProfile initial)
        {
            Text = initial == null ? "Добавить профиль" : "Изменить профиль";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(470, 390);
            BackColor = Color.FromArgb(18,18,26); ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F);
            Label l1 = MakeLabel("Название профиля", 18, 18); Controls.Add(l1);
            nameBox = MakeText(18, 42, 430); Controls.Add(nameBox);
            Label l2 = MakeLabel("Описание / сервисы", 18, 80); Controls.Add(l2);
            descBox = MakeText(18, 104, 430); Controls.Add(descBox);
            Label l3 = MakeLabel("Сайт (необязательно)", 18, 142); Controls.Add(l3);
            webBox = MakeText(18, 166, 430); Controls.Add(webBox);
            Label l4 = MakeLabel("IPv4 DNS-адреса — по одному на строку", 18, 204); Controls.Add(l4);
            addrBox = new TextBox { Location = new Point(18,228), Size = new Size(430,90), Multiline=true, AcceptsReturn=true, ScrollBars=ScrollBars.Vertical, BackColor=Color.FromArgb(10,10,15), ForeColor=Color.White, BorderStyle=BorderStyle.FixedSingle };
            Controls.Add(addrBox);
            Button save = MakeButton("Сохранить", 250, 340, 95); save.Click += (s,e) => SaveProfile(); Controls.Add(save);
            Button cancel = MakeButton("Отмена", 353, 340, 95); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel);
            // Do not bind Enter to Save: Enter must insert a new line in the multiline IPv4 field.
            AcceptButton = null; CancelButton = cancel;
            if (initial != null) {
                nameBox.Text = initial.Name; descBox.Text = initial.Description; webBox.Text = initial.Website;
                addrBox.Text = String.Join(Environment.NewLine, initial.Addresses);
            }
            ThemeSettings.Apply(this);
        }
        Label MakeLabel(string t,int x,int y) { return new Label { Text=t, Location=new Point(x,y), AutoSize=true, ForeColor=Color.FromArgb(207,200,255) }; }
        TextBox MakeText(int x,int y,int w) { return new TextBox { Location=new Point(x,y), Size=new Size(w,24), BackColor=Color.FromArgb(10,10,15), ForeColor=Color.White, BorderStyle=BorderStyle.FixedSingle }; }
        Button MakeButton(string t,int x,int y,int w) { return new Button { Text=t, Location=new Point(x,y), Size=new Size(w,32), FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(124,92,255), ForeColor=Color.White }; }
        void SaveProfile()
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { MessageBox.Show("Укажи название профиля."); return; }
            var addresses = new List<string>();
            foreach (string raw in addrBox.Text.Split(new[] {'\r','\n',' ',',',';','\t'}, StringSplitOptions.RemoveEmptyEntries))
            {
                IPAddress ip;
                if (!IPAddress.TryParse(raw.Trim(), out ip) || ip.AddressFamily != AddressFamily.InterNetwork) {
                    MessageBox.Show("Некорректный IPv4-адрес: " + raw); return;
                }
                string normalized = ip.ToString();
                if (!addresses.Contains(normalized)) addresses.Add(normalized);
            }
            if (addresses.Count == 0) { MessageBox.Show("Добавь хотя бы один IPv4 DNS-адрес."); return; }
            Uri uri;
            string site = webBox.Text.Trim();
            if (site.Length > 0 && (!Uri.TryCreate(site, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))) {
                MessageBox.Show("Сайт должен начинаться с http:// или https://"); return;
            }
            Result = new DnsProfile { Name=name, Description=descBox.Text.Trim(), Website=site, Addresses=addresses };
            DialogResult = DialogResult.OK; Close();
        }
    }

    class MainForm : Form
    {
        const int HotkeyId = 0x4842;
        const int WmHotkey = 0x0312;
        const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly Color bg = Color.FromArgb(7,7,11), violet = Color.FromArgb(140,112,255), yellow = Color.FromArgb(255,224,102), muted = Color.FromArgb(141,141,154);
        readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HB AI Smart DNS");
        readonly string dataFile;
        readonly string settingsFile;
        AppSettings settings;
        string lastDnsSnapshot;
        bool expectedDnsChange;
        int consecutiveDnsFailures;
        bool dnsHealthUnavailable;
        bool healthCheckRunning;
        List<DnsProfile> profiles;
        ComboBox profileBox;
        Label status, addressLabel, infoLabel;
        Button toggle, add, edit, remove, site, reload, themeButton, flushButton, lookupButton;
        TabControl mainTabs;
        TabPage dnsTab, settingsTab;
        TextBox hotkeyBox;
        Button saveHotkeyButton, resetHotkeyButton;
        Label hotkeyStatusLabel;
        int pendingHotkeyModifiers, pendingHotkeyKey;
        int activeHotkeyModifiers, activeHotkeyKey;
        bool hotkeyRegistered;
        Label currentDnsLabel;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        System.Windows.Forms.Timer monitorTimer;
        bool active, busy, suppressProfileChange, allowExit, monitoring;
        int activeProfile = -1;

        public MainForm()
        {
            dataFile = Path.Combine(dataDir, "profiles.xml");
            settingsFile = Path.Combine(dataDir, "settings.json");
            settings = AppSettings.Load(settingsFile);
            Text = "HasladDNS v1.1";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(570, 710);
            MinimumSize = new Size(570, 710); MaximumSize = new Size(570, 710);
            Font = new Font("Segoe UI", 9F);
            ThemeSettings.Load(dataDir);
            BackColor = ThemeSettings.Background; ForeColor = ThemeSettings.Foreground;
            LoadProfiles();
            BuildUi();
            int detected = DnsEngine.DetectProfile(profiles);
            if (detected >= 0) { active = true; activeProfile = detected; profileBox.SelectedIndex = detected; }
            else { active = false; profileBox.SelectedIndex = 0; }
            UpdateUi();
            SetupTray();
            monitorTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            monitorTimer.Tick += (s, e) => { RefreshCurrentDns(); CheckDnsHealth(); };
            monitorTimer.Start();
            RefreshCurrentDns();
        }

        void BuildUi()
        {
            var title = new Label { Text="HasladDNS v1.1", Font=new Font("Segoe UI",18,FontStyle.Bold), ForeColor=Color.White, Location=new Point(22,18), AutoSize=true }; Controls.Add(title);
            var subtitle = new Label { Text="Переключатель DNS · IPv4 only", ForeColor=muted, Location=new Point(24,54), AutoSize=true }; Controls.Add(subtitle);
            themeButton = Btn("Тема: тёмная", 390, 20, 155, 34); themeButton.Click += (s,e) => ToggleTheme(); Controls.Add(themeButton);
            profileBox = new ComboBox { Location=new Point(24,100), Size=new Size(360,30), DropDownStyle=ComboBoxStyle.DropDownList, BackColor=Color.FromArgb(17,17,24), ForeColor=Color.White, FlatStyle=FlatStyle.Flat };
            profileBox.SelectedIndexChanged += (s,e) => {
                if (suppressProfileChange || profileBox.SelectedIndex < 0) return;
                if (active && !busy) ApplyProfile(profileBox.SelectedIndex);
                else UpdateUi();
            };
            Controls.Add(profileBox);
            add = Btn("+ Добавить профиль", 395, 98, 150); add.Click += (s,e) => AddProfile(); Controls.Add(add);
            edit = Btn("Изменить выбранный", 24, 140, 170); edit.Click += (s,e) => EditProfile(); Controls.Add(edit);
            remove = Btn("Удалить выбранный", 204, 140, 170); remove.Click += (s,e) => RemoveProfile(); Controls.Add(remove);
            reload = Btn("Обновить состояние", 384, 140, 161); reload.Click += (s,e) => RefreshState(); Controls.Add(reload);

            var panel = new Panel { Location=new Point(24,190), Size=new Size(521,112), BackColor=Color.FromArgb(17,17,24) }; Controls.Add(panel);
            status = new Label { Text="DNS выключен", Location=new Point(18,15), Size=new Size(485,30), Font=new Font("Segoe UI",15,FontStyle.Bold), ForeColor=Color.White }; panel.Controls.Add(status);
            addressLabel = new Label { Text="", Location=new Point(18,52), Size=new Size(485,24), ForeColor=yellow }; panel.Controls.Add(addressLabel);
            infoLabel = new Label { Text="", Location=new Point(18,78), Size=new Size(485,24), ForeColor=muted }; panel.Controls.Add(infoLabel);

            toggle = Btn("ВКЛ — применить профиль", 24, 322, 521, 58); toggle.Font = new Font("Segoe UI",12,FontStyle.Bold); toggle.Click += (s,e) => Toggle(); Controls.Add(toggle);
            site = Btn("Открыть сайт выбранного сервиса", 24, 393, 521, 42); site.Click += (s,e) => OpenSite(); Controls.Add(site);
            currentDnsLabel = new Label { Text="Текущие IPv4 DNS: проверка…", Location=new Point(25,448), Size=new Size(520,48), ForeColor=muted };
            Controls.Add(currentDnsLabel);
            flushButton = Btn("Очистить DNS-кэш", 24, 505, 250, 36);
            flushButton.Click += (s,e) => RunUtility(() => { DnsEngine.FlushCache(); return "DNS-кэш очищен."; }); Controls.Add(flushButton);
            lookupButton = Btn("Проверить домен (nslookup)", 285, 505, 260, 36);
            lookupButton.Click += (s,e) => RunNslookup(); Controls.Add(lookupButton);
            var note = new Label {
                Text="Сверху отображаются IPv4 DNS-серверы Windows; изменения отслеживаются каждые 5 секунд.\r\nСвернуть окно — в системный трей. Через значок можно быстро переключать профили. IPv6 не изменяется.",
                Location=new Point(25,550), Size=new Size(520,48), ForeColor=muted
            }; Controls.Add(note);
            if (!DnsEngine.IsAdmin()) {
                var warn = new Label { Text="Внимание: запусти программу от имени администратора.", Location=new Point(25,610), AutoSize=true, ForeColor=yellow };
                Controls.Add(warn);
            }
            // Move the existing interface into the first tab without changing its controls or behavior.
            var oldControls = Controls.Cast<Control>().ToArray();
            mainTabs = new TabControl { Location = new Point(10, 10), Size = new Size(550, 680), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            dnsTab = new TabPage("DNS-профили");
            settingsTab = new TabPage("Настройки");
            foreach (Control control in oldControls) dnsTab.Controls.Add(control);
            mainTabs.TabPages.Add(dnsTab);
            mainTabs.TabPages.Add(settingsTab);
            Controls.Add(mainTabs);
            BuildSettingsTab();
            RefreshProfileBox();
        }

        void BuildSettingsTab()
        {
            pendingHotkeyModifiers = settings.HotkeyModifiers;
            pendingHotkeyKey = settings.HotkeyKey;
            activeHotkeyModifiers = settings.HotkeyModifiers;
            activeHotkeyKey = settings.HotkeyKey;

            settingsTab.Padding = new Padding(12);
            var heading = new Label { Text = "Глобальная горячая клавиша", Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(18, 20), AutoSize = true };
            var description = new Label { Text = "Работает, даже когда HasladDNS свёрнут в системный трей.\r\nПереключает DNS так же, как основная кнопка приложения.", Location = new Point(20, 58), Size = new Size(480, 44), ForeColor = ThemeSettings.Muted };
            var label = new Label { Text = "Сочетание клавиш", Location = new Point(20, 120), AutoSize = true };
            hotkeyBox = new TextBox { Location = new Point(20, 146), Size = new Size(270, 30), ReadOnly = true, TabStop = true, Font = new Font("Segoe UI", 11F), Text = FormatHotkey(pendingHotkeyModifiers, pendingHotkeyKey) };
            hotkeyBox.Click += (sender, e) => { hotkeyStatusLabel.Text = "Нажми сочетание клавиш на клавиатуре."; hotkeyBox.Focus(); };
            hotkeyBox.KeyDown += (sender, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
                int mods = 0;
                if (e.Control) mods |= (int)ModControl;
                if (e.Alt) mods |= (int)ModAlt;
                if (e.Shift) mods |= (int)ModShift;
                if (mods == 0) { hotkeyStatusLabel.Text = "Сочетание должно включать Ctrl, Alt или Shift."; return; }
                pendingHotkeyModifiers = mods;
                pendingHotkeyKey = (int)e.KeyCode;
                hotkeyBox.Text = FormatHotkey(pendingHotkeyModifiers, pendingHotkeyKey);
                hotkeyStatusLabel.Text = "Нажми «Сохранить сочетание», чтобы применить.";
            };
            saveHotkeyButton = Btn("Сохранить сочетание", 20, 192, 190, 36);
            saveHotkeyButton.Click += (sender, e) => SaveHotkey();
            resetHotkeyButton = Btn("По умолчанию", 220, 192, 150, 36);
            resetHotkeyButton.Click += (sender, e) =>
            {
                pendingHotkeyModifiers = (int)(ModControl | ModAlt);
                pendingHotkeyKey = (int)Keys.D;
                hotkeyBox.Text = FormatHotkey(pendingHotkeyModifiers, pendingHotkeyKey);
                hotkeyStatusLabel.Text = "Выбрано Ctrl + Alt + D. Нажми «Сохранить сочетание».";
            };
            hotkeyStatusLabel = new Label { Text = "Текущее сочетание: " + FormatHotkey(activeHotkeyModifiers, activeHotkeyKey), Location = new Point(20, 246), Size = new Size(490, 48), ForeColor = ThemeSettings.Muted };
            var note = new Label { Text = "Если сочетание занято, HasladDNS сообщит об этом и сохранит прежнее.\r\nИзменения сохраняются для следующих запусков.", Location = new Point(20, 304), Size = new Size(490, 44), ForeColor = ThemeSettings.Muted };
            settingsTab.Controls.Add(heading);
            settingsTab.Controls.Add(description);
            settingsTab.Controls.Add(label);
            settingsTab.Controls.Add(hotkeyBox);
            settingsTab.Controls.Add(saveHotkeyButton);
            settingsTab.Controls.Add(resetHotkeyButton);
            settingsTab.Controls.Add(hotkeyStatusLabel);
            settingsTab.Controls.Add(note);
            ThemeSettings.Apply(settingsTab);
        }

        static string FormatHotkey(int modifiers, int key)
        {
            var parts = new List<string>();
            if ((modifiers & (int)ModControl) != 0) parts.Add("Ctrl");
            if ((modifiers & (int)ModAlt) != 0) parts.Add("Alt");
            if ((modifiers & (int)ModShift) != 0) parts.Add("Shift");
            parts.Add(((Keys)key).ToString());
            return String.Join(" + ", parts.ToArray());
        }

        void SaveHotkey()
        {
            if (pendingHotkeyModifiers == activeHotkeyModifiers && pendingHotkeyKey == activeHotkeyKey)
            {
                settings.HotkeyModifiers = activeHotkeyModifiers;
                settings.HotkeyKey = activeHotkeyKey;
                settings.Save(settingsFile);
                hotkeyStatusLabel.Text = "Сохранено: " + FormatHotkey(activeHotkeyModifiers, activeHotkeyKey);
                return;
            }

            if (hotkeyRegistered) { UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
            bool registered = RegisterHotKey(Handle, HotkeyId, (uint)pendingHotkeyModifiers | ModNoRepeat, (uint)pendingHotkeyKey);
            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();
                bool restored = RegisterHotKey(Handle, HotkeyId, (uint)activeHotkeyModifiers | ModNoRepeat, (uint)activeHotkeyKey);
                hotkeyRegistered = restored;
                hotkeyBox.Text = FormatHotkey(activeHotkeyModifiers, activeHotkeyKey);
                pendingHotkeyModifiers = activeHotkeyModifiers;
                pendingHotkeyKey = activeHotkeyKey;
                hotkeyStatusLabel.Text = "Не удалось применить сочетание (код " + error + ").";
                MessageBox.Show(this, "Сочетание занято или не поддерживается Windows. Прежний хоткей " + (restored ? "оставлен активным." : "не удалось восстановить; проверь настройки."), "HasladDNS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            hotkeyRegistered = true;
            activeHotkeyModifiers = pendingHotkeyModifiers;
            activeHotkeyKey = pendingHotkeyKey;
            settings.HotkeyModifiers = activeHotkeyModifiers;
            settings.HotkeyKey = activeHotkeyKey;
            settings.Save(settingsFile);
            hotkeyBox.Text = FormatHotkey(activeHotkeyModifiers, activeHotkeyKey);
            hotkeyStatusLabel.Text = "Сохранено: " + hotkeyBox.Text;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (settings != null && !hotkeyRegistered)
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, (uint)settings.HotkeyModifiers | ModNoRepeat, (uint)settings.HotkeyKey);
                if (!hotkeyRegistered && hotkeyStatusLabel != null)
                    hotkeyStatusLabel.Text = "Не удалось зарегистрировать хоткей. Возможно, он занят — выбери другое сочетание.";
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                if (!busy) Toggle();
                else NotifyTray("HasladDNS", "Операция DNS уже выполняется. Повтори хоткей позже.");
                return;
            }
            base.WndProc(ref m);
        }

        void SetupTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Открыть HasladDNS", null, (s,e) => RestoreFromTray());
            trayMenu.Items.Add(new ToolStripSeparator());
            RebuildTrayProfiles();
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Импорт профилей из JSON…", null, (s,e) => ImportProfiles());
            trayMenu.Items.Add("Экспорт профилей в JSON…", null, (s,e) => ExportProfiles());
            trayMenu.Items.Add("Недавно использованные", null, (s,e) => ShowRecentProfiles());
            trayMenu.Items.Add("Уведомлять об изменении DNS", null, (s,e) => ToggleDnsNotifications());
            trayMenu.Items.Add("Очистить DNS-кэш", null, (s,e) => RunUtility(() => { DnsEngine.FlushCache(); return "DNS-кэш очищен."; }));
            trayMenu.Items.Add("Выход", null, (s,e) => { allowExit=true; tray.Visible=false; Application.Exit(); });
            tray = new NotifyIcon();
            try { tray.Icon = Icon ?? System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "HasladDNS v1.1";
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += (s,e) => RestoreFromTray();
            if (!settings.WarnedAboutIpv6)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool hasIpv6 = false;
                    try { hasIpv6 = DnsEngine.HasIpv6Dns(); } catch { }
                    if (hasIpv6)
                    {
                        settings.WarnedAboutIpv6 = true;
                        settings.Save(settingsFile);
                        try { BeginInvoke((Action)(() => NotifyTray("Обратите внимание на IPv6", "HasladDNS меняет только IPv4 DNS. IPv6 DNS остаётся без изменений."))); } catch { }
                    }
                });
            }
        }

        void ToggleDnsNotifications()
        {
            settings.NotifyDnsChanges = !settings.NotifyDnsChanges;
            settings.Save(settingsFile);
            NotifyTray("HasladDNS", settings.NotifyDnsChanges ? "Уведомления об изменении DNS включены." : "Уведомления об изменении DNS выключены.");
        }

        void ExportProfiles()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Экспорт DNS-профилей";
                dialog.Filter = "JSON (*.json)|*.json";
                dialog.FileName = "hasladDNS-profiles.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var stream = File.Create(dialog.FileName))
                        new DataContractJsonSerializer(typeof(ProfileStore)).WriteObject(stream, new ProfileStore { Profiles = profiles });
                    MessageBox.Show(this, "Профили экспортированы.", "HasladDNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "Не удалось экспортировать профили: " + ex.Message, "HasladDNS", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void ImportProfiles()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Импорт DNS-профилей";
                dialog.Filter = "JSON (*.json)|*.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ProfileStore imported;
                    using (var stream = File.OpenRead(dialog.FileName))
                        imported = new DataContractJsonSerializer(typeof(ProfileStore)).ReadObject(stream) as ProfileStore;
                    if (imported == null || imported.Profiles == null || imported.Profiles.Count == 0)
                        throw new Exception("Файл не содержит DNS-профилей.");
                    var valid = new List<DnsProfile>();
                    foreach (var profile in imported.Profiles)
                    {
                        if (profile == null || String.IsNullOrWhiteSpace(profile.Name) || profile.Addresses == null || profile.Addresses.Count == 0)
                            continue;
                        var addresses = new List<string>();
                        foreach (string address in profile.Addresses)
                        {
                            IPAddress ip;
                            if (!IPAddress.TryParse(address ?? "", out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                                throw new Exception("Профиль «" + profile.Name + "» содержит некорректный IPv4-адрес: " + address);
                            if (!addresses.Contains(ip.ToString())) addresses.Add(ip.ToString());
                        }
                        if (addresses.Count > 0)
                        {
                            string website = (profile.Website ?? "").Trim();
                            Uri siteUri;
                            if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out siteUri) ||
                                (siteUri.Scheme != "http" && siteUri.Scheme != "https")))
                                throw new Exception("Профиль «" + profile.Name + "» содержит небезопасный или некорректный URL.");
                            valid.Add(new DnsProfile { Name=profile.Name.Trim(), Description=profile.Description ?? "", Website=website, Addresses=addresses });
                        }
                    }
                    if (valid.Count == 0) throw new Exception("В файле нет корректных DNS-профилей.");

                    DialogResult mode = MessageBox.Show(this,
                        "Да — восстановить резервную копию и заменить все текущие профили.\\r\\nНет — добавить профили из файла к существующим.\\r\\nОтмена — ничего не менять.",
                        "Импорт / восстановление профилей", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (mode == DialogResult.Cancel) return;
                    bool restoreBackup = mode == DialogResult.Yes;
                    if (restoreBackup && MessageBox.Show(this,
                        "Все текущие DNS-профили будут заменены содержимым выбранного файла. Продолжить?",
                        "Подтверждение восстановления", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;

                    int added = 0;
                    if (restoreBackup) profiles.Clear();
                    foreach (var profile in valid)
                    {
                        if (!restoreBackup && profiles.Any(p => String.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                            profile.Name = profile.Name + " (импорт)";
                        string baseName = profile.Name;
                        int suffix = 2;
                        while (profiles.Any(p => String.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                            profile.Name = baseName + " " + suffix++;
                        profiles.Add(profile);
                        added++;
                    }
                    SaveProfiles();
                    RefreshProfileBox();
                    RebuildTrayProfiles();
                    MessageBox.Show(this,
                        restoreBackup ? "Восстановлено профилей: " + added + "." : "Импортировано профилей: " + added + ".",
                        "HasladDNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "Не удалось импортировать профили: " + ex.Message, "HasladDNS", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void ShowRecentProfiles()
        {
            string message = settings.RecentProfiles.Count == 0
                ? "История пока пуста."
                : String.Join(Environment.NewLine, settings.RecentProfiles.Select((name, index) => (index + 1) + ". " + name).ToArray());
            MessageBox.Show(this, message, "Последние 3 DNS-профиля", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void RebuildTrayProfiles()
        {
            if (trayMenu == null) return;
            // Keep the static commands; replace only the profile entries between the separators.
            while (trayMenu.Items.Count > 2 && !(trayMenu.Items[2] is ToolStripSeparator)) trayMenu.Items.RemoveAt(2);
            int insertAt = 2;
            foreach (var profile in profiles)
            {
                DnsProfile captured = profile;
                trayMenu.Items.Insert(insertAt++, new ToolStripMenuItem("Применить: " + profile.Name, null, (s,e) => {
                    int index = profiles.IndexOf(captured);
                    if (index >= 0) { RestoreFromTray(); suppressProfileChange=true; profileBox.SelectedIndex=index; suppressProfileChange=false; ApplyProfile(index); }
                }));
            }
        }

        void RestoreFromTray()
        {
            Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) { Hide(); ShowInTaskbar=false; if (tray != null) tray.ShowBalloonTip(1200, "HasladDNS v1", "Программа продолжает работать в системном трее.", ToolTipIcon.Info); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel=true; Hide(); ShowInTaskbar=false;
                if (tray != null) tray.ShowBalloonTip(1200, "HasladDNS v1", "Программа свёрнута в системный трей. Для выхода выбери «Выход» в меню значка.", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }

        void RefreshCurrentDns()
        {
            if (monitoring || IsDisposed || !IsHandleCreated) return;
            monitoring=true;
            ThreadPool.QueueUserWorkItem(_ => {
                string result=null; Exception error=null;
                try { result=DnsEngine.GetCurrentDns(); } catch(Exception ex) { error=ex; }
                try { BeginInvoke((Action)(() => {
                    monitoring=false;
                    if (currentDnsLabel != null && !currentDnsLabel.IsDisposed)
                    {
                        currentDnsLabel.Text = error == null ? "Текущие IPv4 DNS: " + result : "Не удалось прочитать DNS: " + error.Message;
                        currentDnsLabel.ForeColor = error == null ? ThemeSettings.Muted : Color.IndianRed;
                        if (error == null)
                        {
                            string snapshot = NormalizeDnsSnapshot(result);
                            if (lastDnsSnapshot == null) lastDnsSnapshot = snapshot;
                            else if (!String.Equals(lastDnsSnapshot, snapshot, StringComparison.OrdinalIgnoreCase))
                            {
                                lastDnsSnapshot = snapshot;
                                if (expectedDnsChange) expectedDnsChange = false;
                                else if (!busy && settings.NotifyDnsChanges)
                                    NotifyTray("Изменение DNS", "Настройки IPv4 DNS изменились вне HasladDNS.");
                            }
                        }
                    }
                })); } catch { monitoring=false; }
            });
        }

        static string NormalizeDnsSnapshot(string value)
        {
            return String.Join("|", (value ?? "").Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        void NotifyTray(string title, string message)
        {
            try
            {
                if (tray != null && tray.Visible)
                    tray.ShowBalloonTip(4000, title, message, ToolTipIcon.Warning);
            }
            catch { }
        }

        void CheckDnsHealth()
        {
            if (healthCheckRunning || busy || IsDisposed) return;
            healthCheckRunning = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool failed = false;
                try
                {
                    var result = Dns.GetHostAddresses("example.com");
                    failed = result == null || result.Length == 0;
                }
                catch { failed = true; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        healthCheckRunning = false;
                        if (failed)
                        {
                            consecutiveDnsFailures++;
                            if (consecutiveDnsFailures >= 3 && !dnsHealthUnavailable)
                            {
                                dnsHealthUnavailable = true;
                                NotifyTray("DNS недоступен", "Три последовательные проверки DNS завершились неудачно.");
                            }
                        }
                        else
                        {
                            consecutiveDnsFailures = 0;
                            if (dnsHealthUnavailable)
                            {
                                dnsHealthUnavailable = false;
                                NotifyTray("DNS восстановлен", "Разрешение доменных имён снова работает.");
                            }
                        }
                    }));
                }
                catch { healthCheckRunning = false; }
            });
        }

        void RunUtility(Func<string> work)
        {
            if (busy) return;
            busy=true; UpdateUi();
            ThreadPool.QueueUserWorkItem(_ => {
                string result=null; Exception error=null;
                try { result=work(); } catch(Exception ex) { error=ex; }
                try { BeginInvoke((Action)(() => { busy=false; UpdateUi(); if (error != null) MessageBox.Show(this,error.Message,"HasladDNS",MessageBoxButtons.OK,MessageBoxIcon.Error); else MessageBox.Show(this,result,"HasladDNS",MessageBoxButtons.OK,MessageBoxIcon.Information); RefreshCurrentDns(); })); } catch { }
            });
        }

        void RunNslookup()
        {
            string domain;
            using (Form f = new Form { Text="Проверка DNS — nslookup", StartPosition=FormStartPosition.CenterParent, ClientSize=new Size(540,140), FormBorderStyle=FormBorderStyle.FixedDialog, MaximizeBox=false, MinimizeBox=false })
            {
                Label l = new Label { Text="Домен для проверки:", Location=new Point(14,14), AutoSize=true };
                TextBox t = new TextBox { Text="example.com", Location=new Point(14,40), Width=510 };
                Button b = new Button { Text="Выполнить nslookup", Location=new Point(14,82), Width=180, DialogResult=DialogResult.OK };
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(b); f.AcceptButton=b;
                ThemeSettings.Apply(f);
                if (f.ShowDialog(this)!=DialogResult.OK) return;
                domain=t.Text.Trim();
            }
            RunUtility(() => DnsEngine.Nslookup(domain));
        }

        void ToggleTheme()
        {
            ThemeSettings.Dark = !ThemeSettings.Dark;
            ThemeSettings.Save();
            ThemeSettings.Apply(this);
            UpdateUi();
        }

        Button Btn(string text,int x,int y,int w,int h=32) {
            return new Button { Text=text, Location=new Point(x,y), Size=new Size(w,h), FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(124,92,255), ForeColor=Color.White, Cursor=Cursors.Hand };
        }

        void LoadProfiles()
        {
            Directory.CreateDirectory(dataDir);
            try {
                if (File.Exists(dataFile)) {
                    using (var fs=File.OpenRead(dataFile)) { var store = new XmlSerializer(typeof(ProfileStore)).Deserialize(fs) as ProfileStore; profiles = store == null ? null : store.Profiles; }
                }
            } catch { profiles = null; }
            if (profiles == null || profiles.Count == 0) profiles = Defaults();
            // Ensure all stored profiles are IPv4-only and well-formed.
            profiles = profiles.Where(p => p != null && !String.IsNullOrWhiteSpace(p.Name) && p.Addresses != null && p.Addresses.Count > 0)
                .Select(p => new DnsProfile { Name=p.Name, Description=p.Description ?? "", Website=p.Website ?? "", Addresses=p.Addresses.Where(IsIPv4).Select(DnsEngine.Normalize).Distinct().ToList() })
                .Where(p => p.Addresses.Count > 0).ToList();
            if (profiles.Count == 0) profiles = Defaults();
            // Add newly shipped defaults to existing installations without overwriting user-edited profiles.
            foreach (var builtIn in Defaults())
                if (!profiles.Any(p => String.Equals(p.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase)))
                    profiles.Add(builtIn);
            SaveProfiles();
        }

        List<DnsProfile> Defaults()
        {
            return new List<DnsProfile> {
                new DnsProfile { Name="Comss.one", Description="ChatGPT, Claude, Gemini, ElevenLabs", Website="https://www.comss.ru/", Addresses=new List<string>{"83.220.169.155","212.109.195.93"} },
                new DnsProfile { Name="GeoHide", Description="Gemini, ElevenLabs; ChatGPT/Claude могут работать нестабильно", Website="https://geohide.ru/", Addresses=new List<string>{"159.194.200.33","193.233.112.67"} },
                new DnsProfile { Name="GeoHide EU", Description="ChatGPT, Claude, Gemini, ElevenLabs", Website="https://geohide.ru/", Addresses=new List<string>{"143.20.64.192","45.13.237.213"} },
                new DnsProfile { Name="Xbox DNS", Description="Xbox Live", Website="https://xbox-dns.ru/", Addresses=new List<string>{"111.88.96.54","111.88.96.55"} },
                new DnsProfile { Name="Zapret", Description="AI", Website="https://dns.zapret.moe/#connect", Addresses=new List<string>{"83.217.211.149"} },
                new DnsProfile { Name="Malw", Description="откл. геоблок по рф", Website="https://info.dns.malw.link/", Addresses=new List<string>{"179.254.166.171"} },
                new DnsProfile { Name="bezmezhau", Description="беларусский dns", Website="https://bezmezhau.com/ru/", Addresses=new List<string>{"181.215.40.49","31.58.216.201"} },
                new DnsProfile { Name="magicdns", Description="AI geoblock RU", Website="https://magicdns.ru", Addresses=new List<string>{"109.120.132.43","94.249.177.102"} }
            };
        }

        static bool IsIPv4(string s) { IPAddress ip; return IPAddress.TryParse(s, out ip) && ip.AddressFamily == AddressFamily.InterNetwork; }
        void SaveProfiles()
        {
            try {
                Directory.CreateDirectory(dataDir);
                using (var fs=File.Create(dataFile)) new XmlSerializer(typeof(ProfileStore)).Serialize(fs, new ProfileStore { Profiles=profiles });
            } catch (Exception ex) { MessageBox.Show("Не удалось сохранить профили:\r\n"+ex.Message); }
        }

        void RefreshProfileBox()
        {
            int old = profileBox.SelectedIndex;
            suppressProfileChange = true;
            profileBox.BeginUpdate();
            profileBox.Items.Clear();
            foreach (var p in profiles) profileBox.Items.Add(p.Name);
            if (profiles.Count > 0) profileBox.SelectedIndex = Math.Max(0, Math.Min(old, profiles.Count-1));
            profileBox.EndUpdate();
            suppressProfileChange = false;
            UpdateUi();
        }

        void AddProfile()
        {
            using (var d = new ProfileEditor(null)) if (d.ShowDialog(this)==DialogResult.OK) {
                if (profiles.Any(p => String.Equals(p.Name,d.Result.Name,StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Профиль с таким названием уже существует."); return; }
                profiles.Add(d.Result); SaveProfiles(); RefreshProfileBox(); profileBox.SelectedIndex=profiles.Count-1;
            }
        }

        void EditProfile()
        {
            int i=profileBox.SelectedIndex; if (i<0) return;
            using (var d=new ProfileEditor(profiles[i])) if (d.ShowDialog(this)==DialogResult.OK) {
                if (profiles.Where((p,n)=>n!=i).Any(p=>String.Equals(p.Name,d.Result.Name,StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Профиль с таким названием уже существует."); return; }
                profiles[i]=d.Result; SaveProfiles(); RefreshProfileBox(); profileBox.SelectedIndex=i;
                if (active && activeProfile==i) ApplyProfile(i);
            }
        }

        void RemoveProfile()
        {
            int i=profileBox.SelectedIndex; if (i<0) return;
            if (profiles.Count==1) { MessageBox.Show("Нельзя удалить последний профиль."); return; }
            if (active && activeProfile==i) {
                if (MessageBox.Show("Этот профиль сейчас активен. Сначала вернуть автоматический DNS и удалить профиль?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)!=DialogResult.Yes) return;
                RunAction(() => DnsEngine.Disable(), () => { active=false; activeProfile=-1; profiles.RemoveAt(i); SaveProfiles(); RefreshProfileBox(); UpdateUi(); });
            } else {
                profiles.RemoveAt(i); if (activeProfile>i) activeProfile--;
                SaveProfiles(); RefreshProfileBox(); UpdateUi();
            }
        }

        void Toggle()
        {
            if (busy) return;
            if (active) RunAction(() => DnsEngine.Disable(), () => { active=false; activeProfile=-1; RefreshState(false); });
            else { int i=profileBox.SelectedIndex; if (i>=0) ApplyProfile(i); }
        }

        void ApplyProfile(int i)
        {
            if (i<0 || i>=profiles.Count || busy) return;
            var p=profiles[i];
            RunAction(() => DnsEngine.Apply(p.Addresses), () => { active=true; activeProfile=i; settings.RecordProfile(p.Name); settings.Save(settingsFile); RefreshState(false); });
        }

        void RunAction(Action work, Action success)
        {
            busy=true; errorText=null; expectedDnsChange=true; UpdateUi();
            ThreadPool.QueueUserWorkItem(_ => {
                Exception error=null;
                try { work(); } catch(Exception ex) { error=ex; }
                BeginInvoke((Action)(() => {
                    busy=false;
                    if (error==null) success();
                    else
                    {
                        errorText=error.Message;
                        NotifyTray("Не удалось применить DNS", error.Message.Length > 180 ? error.Message.Substring(0, 177) + "..." : error.Message);
                        RefreshState(false);
                        ShowDiagnostic(error);
                    }
                    UpdateUi();
                }));
            });
        }
        void ShowDiagnostic(Exception ex)
        {
            using (Form f = new Form())
            {
                f.Text = "Ошибка DNS — подробности";
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(700, 390);
                f.MinimumSize = new Size(560, 300);
                f.BackColor = ThemeSettings.Background;
                f.ForeColor = ThemeSettings.Foreground;
                f.Font = new Font("Segoe UI", 9F);
                Label label = new Label { Text = "Не удалось применить DNS. Скопируй текст ошибки и отправь его сообщением:", AutoSize = false, Location = new Point(12,12), Size = new Size(670,36), ForeColor = ThemeSettings.Foreground };
                TextBox details = new TextBox { Location = new Point(12,52), Size = new Size(670,275), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = ThemeSettings.Input, ForeColor = ThemeSettings.Foreground, Text = ex.ToString() };
                Button copy = new Button { Text = "Копировать ошибку", Location = new Point(12,340), Size = new Size(160,34), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
                Button close = new Button { Text = "Закрыть", Location = new Point(590,340), Size = new Size(92,34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
                copy.Click += (sender, args) => { try { Clipboard.SetText(details.Text); MessageBox.Show(f, "Текст ошибки скопирован.", "DNS", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception clipEx) { MessageBox.Show(f, "Не удалось скопировать: " + clipEx.Message); } };
                f.Controls.Add(label); f.Controls.Add(details); f.Controls.Add(copy); f.Controls.Add(close); f.AcceptButton = close;
                f.ShowDialog(this);
            }
        }

        string errorText;

        void RefreshState(bool detect=true)
        {
            if (detect) {
                int d=DnsEngine.DetectProfile(profiles);
                active=d>=0; activeProfile=d;
                if (d>=0 && profileBox.SelectedIndex!=d) profileBox.SelectedIndex=d;
            }
            UpdateUi();
        }

        void UpdateUi()
        {
            if (status==null) return;
            ThemeSettings.Apply(this);
            themeButton.Text = ThemeSettings.Dark ? "Тема: тёмная" : "Тема: светлая";
            status.Text = busy ? "Применяем настройки…" : active ? "DNS включён" : "DNS выключен";
            status.ForeColor = active ? (ThemeSettings.Dark ? yellow : Color.FromArgb(25,130,75)) : ThemeSettings.Foreground;
            if (errorText!=null) status.Text = "Ошибка применения DNS";
            int i=profileBox.SelectedIndex;
            DnsProfile p = i>=0 && i<profiles.Count ? profiles[i] : null;
            addressLabel.Text = p==null ? "" : "IPv4: " + String.Join("  /  ",p.Addresses);
            infoLabel.Text = p==null ? "" : p.Description;
            toggle.Text = busy ? "ПОДОЖДИТЕ…" : active ? "ВЫКЛ — вернуть автоматический DNS" : "ВКЛ — применить профиль";
            toggle.BackColor = active ? Color.FromArgb(190,72,72) : ThemeSettings.Accent;
            addressLabel.ForeColor = ThemeSettings.Dark ? yellow : Color.FromArgb(113,78,0);
            infoLabel.ForeColor = ThemeSettings.Muted;
            themeButton.BackColor = ThemeSettings.Dark ? Color.FromArgb(55,55,70) : Color.FromArgb(225,230,242);
            themeButton.ForeColor = ThemeSettings.Dark ? Color.White : Color.FromArgb(40,45,65);
            foreach (Control c in Controls) if (c is Label && c.Text.StartsWith("Внимание:")) c.ForeColor = ThemeSettings.Dark ? yellow : Color.FromArgb(160,75,0);
            toggle.Enabled = !busy;
            add.Enabled=edit.Enabled=remove.Enabled=reload.Enabled=!busy;
            site.Enabled=!busy && p!=null && !String.IsNullOrWhiteSpace(p.Website);
            if (flushButton != null) flushButton.Enabled = !busy;
            if (lookupButton != null) lookupButton.Enabled = !busy;
            if (trayMenu != null) RebuildTrayProfiles();
            Invalidate();
        }

        void OpenSite()
        {
            int i=profileBox.SelectedIndex; if (i<0) return;
            string url=profiles[i].Website;
            if (String.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(url); } catch { try { Process.Start(new ProcessStartInfo(url){UseShellExecute=true}); } catch(Exception ex) { MessageBox.Show(ex.Message); } }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hotkeyRegistered && IsHandleCreated) { UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
            if (monitorTimer != null) monitorTimer.Stop();
            if (tray != null) { tray.Visible=false; tray.Dispose(); }
            if (trayMenu != null) trayMenu.Dispose();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
