using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("FZO Launcher")]
[assembly: System.Reflection.AssemblyProduct("FZO Launcher")]
[assembly: System.Reflection.AssemblyVersion("1.2.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.3.0")]

namespace FZO {
    public class Settings {
        public string Nickname = "", Server = "industrial";
        public string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FZO", "clients");
        public int MemoryMB = 2048;
        public bool MinimizeOnLaunch = true;
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FZO", "launcher"); } }
        public static Settings Load() {
            try {
                var value = Json.Read<Settings>(Path.Combine(Folder, "settings.json")) ?? new Settings();
                if (String.IsNullOrWhiteSpace(value.InstallRoot) || !Path.IsPathRooted(value.InstallRoot)) return new Settings();
                value.MemoryMB = Math.Max(512, Math.Min(8192, value.MemoryMB)); return value;
            }
            catch { return new Settings(); }
        }
        public void Save() { Directory.CreateDirectory(Folder); Json.Write(Path.Combine(Folder, "settings.json"), this); }
    }
    public static class Game {
        public static string Quote(string value) {
            // Windows command line quoting, including a trailing backslash.
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public static ProcessStartInfo StartInfo(string id, string root, string nickname, int memory, string bootstrap = null) {
            if (id != "beta" && id != "industrial") throw new ArgumentException("Неизвестный сервер.");
            if (!Regex.IsMatch(nickname, "^[A-Za-z0-9_]{3,16}$")) throw new ArgumentException("Ник: 3–16 латинских букв, цифр или подчёркиваний.");
            if (memory < 512 || memory > 8192) throw new ArgumentException("Недопустимый объём памяти.");
            string game = id == "beta" ? root : Path.Combine(root, "game");
            string java = Path.Combine(root, id == "beta" ? @"runtime-x64\bin\javaw.exe" : @"java7\bin\javaw.exe");
            var args = new List<string> { "-Xms256M", "-Xmx" + memory + "M", "-Dfile.encoding=UTF-8" };
            string natives = Path.Combine(root, id == "beta" ? @"bin\natives" : @"game\natives");
            args.Add("-Djava.library.path=" + natives);
            args.Add("-Dorg.lwjgl.librarypath=" + natives);
            args.Add("-Dnet.java.games.input.librarypath=" + natives);
            string cp;
            if (id == "industrial") {
                args.Add("-XX:PermSize=128M"); args.Add("-XX:MaxPermSize=256M");
                args.Add("-Dminecraft.applet.TargetDirectory=" + game);
                cp = String.Join(";", new[] { @"game\minecraft-forge.jar", @"game\minecraft.jar", @"game\lib\*", @"runtime\lib\lwjgl-2.9.0.jar", @"runtime\lib\lwjgl_util-2.9.0.jar", @"runtime\lib\jinput-2.0.5.jar", @"runtime\lib\jutils-1.0.0.jar" }.Select(p => Path.Combine(root, p)));
            } else {
                args.Add("-Duser.home=" + Path.Combine(root, "data"));
                cp = String.Join(";", new[] { @"mods\beta_OptiFine_1.7.3_HD_S_G.zip", @"game\minecraft.jar", @"bin\lwjgl.jar", @"bin\lwjgl_util.jar", @"bin\jinput.jar", @"bin\jutils.jar" }.Select(p => Path.Combine(root, p)));
            }
            if (bootstrap != null) cp = bootstrap + ";" + cp;
            args.AddRange(new[] { "-cp", cp, bootstrap == null ? "net.minecraft.client.Minecraft" : "fzo.launcher.ClientBootstrap", nickname, "-" });
            var info = new ProcessStartInfo(java, String.Join(" ", args.Select(Quote))) {
                WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (id == "beta") {
                info.EnvironmentVariables["APPDATA"] = Path.Combine(root, "data");
                info.EnvironmentVariables["USERPROFILE"] = Path.Combine(root, "data");
            }
            return info;
        }
    }

    public sealed class GameExitState {
        bool windowClosed, fatal;
        public void Observe(string line) {
            if (line == null) return;
            if (line.TrimEnd().EndsWith("Someone is closing me!", StringComparison.Ordinal)) windowClosed = true;
            if (line.Contains("---- Minecraft Crash Report ----") || line.Contains("Minecraft has crashed!")
                || line.Contains("A fatal error has been detected by the Java Runtime Environment")
                || line.Contains("Exception in thread \"Minecraft main thread\"")) fatal = true;
        }
        public bool Expected(int exitCode, bool newCrashReport) {
            return !fatal && !newCrashReport && (exitCode == 0 || (exitCode == 1 && windowClosed));
        }
        public static Dictionary<string, DateTime> CrashReports(string root, string id) {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string game = Path.Combine(root, id == "industrial" ? "game" : @"data\.minecraft");
            foreach (string folder in new[] { game, root }) {
                string reports = Path.Combine(folder, "crash-reports");
                if (Directory.Exists(reports)) foreach (string file in Directory.GetFiles(reports, "*.txt")) result[file] = File.GetLastWriteTimeUtc(file);
                if (Directory.Exists(folder)) foreach (string file in Directory.GetFiles(folder, "hs_err_pid*.log")) result[file] = File.GetLastWriteTimeUtc(file);
            }
            return result;
        }
    }
    public sealed class GameProcessException : Exception { public GameProcessException(string message) : base(message) { } }

    public class LauncherForm : Form {
        public static readonly Color BackgroundColor = Color.FromArgb(12, 17, 25), PanelColor = Color.FromArgb(19, 28, 41), TextColor = Color.FromArgb(234, 241, 251), MutedColor = Color.FromArgb(146, 165, 190), AccentColor = Color.FromArgb(66, 190, 245);
        readonly Settings settings;
        readonly RadioButton beta, industrial;
        readonly TextBox nickname;
        readonly Button play, menu, cancel, launcherUpdate;
        LauncherRelease availableLauncher;
        bool checkingLauncher;
        readonly Label title, description, address, status, detail, serverState;
        readonly ProgressBar progress;
        CancellationTokenSource cancellation;
        bool busy, running;
        string Selected { get { return beta.Checked ? "beta" : "industrial"; } }
        public static Label LabelAt(Control parent, string text, int x, int y, int width, int height, float size, Color color) {
            var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), ForeColor = color, Font = new Font("Segoe UI", size), BackColor = Color.Transparent };
            parent.Controls.Add(label); return label;
        }
        public static Button ButtonAt(Control parent, string text, int x, int y, int width, int height, bool primary) {
            var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, height), BackColor = primary ? AccentColor : Color.FromArgb(30, 45, 64), ForeColor = primary ? BackgroundColor : TextColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 10), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0; parent.Controls.Add(button); return button;
        }
        public static TextBox TextBoxAt(Control parent, string text, int x, int y, int width) {
            var box = new TextBox { Text = text, Location = new Point(x, y), Width = width, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(25, 37, 53), ForeColor = TextColor, Font = new Font("Segoe UI", 13) };
            parent.Controls.Add(box); return box;
        }
        public LauncherForm(Settings value, bool preview = false) {
            settings = value;
            Text = "FZO Launcher"; ClientSize = new Size(900, 590); FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = BackgroundColor;
            ForeColor = TextColor; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            using (var stream = typeof(LauncherForm).Assembly.GetManifestResourceStream("owl.ico")) if (stream != null) Icon = new Icon(stream);
            var side = new Panel { Dock = DockStyle.Left, Width = 244, BackColor = PanelColor }; Controls.Add(side);
            var owl = new PictureBox { Location = new Point(33, 12), Size = new Size(178, 132), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
            using (var stream = typeof(LauncherForm).Assembly.GetManifestResourceStream("owl.png")) owl.Image = Image.FromStream(stream);
            side.Controls.Add(owl);
            LabelAt(side, "FRIENDZONE ORDER", 24, 145, 210, 25, 11, TextColor);
            LabelAt(side, "Выбери свой мир", 24, 173, 210, 24, 10, MutedColor);
            beta = ServerChoice(side, "BETA 1.7.3\nКлассический\nMinecraft", 228);
            industrial = ServerChoice(side, "INDUSTRIAL 1.4.7\nМоды, заводы\nи технологии", 340);
            launcherUpdate = ButtonAt(side, "Проверить лаунчер", 18, 464, 208, 54, false);
            launcherUpdate.Font = new Font("Segoe UI", 9);
            launcherUpdate.Click += async delegate {
                if (availableLauncher == null) await CheckLauncher(true); else await InstallLauncher();
            };
            LabelAt(side, "FZO LAUNCHER  /  1.2.3", 24, 539, 210, 22, 9, MutedColor);
            LabelAt(this, "ТВОЙ СЕРВЕР. ТВОЯ ИГРА.", 282, 37, 400, 22, 9, AccentColor);
            menu = ButtonAt(this, "☰  Меню", 750, 27, 116, 40, false); menu.AccessibleName = "Меню и настройки";
            title = LabelAt(this, "Industrial", 279, 100, 585, 55, 30, TextColor);
            description = LabelAt(this, "", 283, 168, 568, 54, 11, MutedColor);
            address = LabelAt(this, "", 283, 237, 568, 25, 10, AccentColor);
            serverState = LabelAt(this, preview ? "Статус обновится при открытии лаунчера" : "Проверяем статус сервера…", 283, 265, 568, 22, 9, MutedColor);
            LabelAt(this, "НИКНЕЙМ", 283, 294, 400, 22, 9, MutedColor);
            nickname = TextBoxAt(this, settings.Nickname ?? "", 283, 324, 380); nickname.MaxLength = 16; nickname.AccessibleName = "Никнейм";
            LabelAt(this, "Один ник — тот же инвентарь и прогресс на сервере.", 283, 365, 572, 24, 9, MutedColor);
            status = LabelAt(this, "Готов к игре", 283, 419, 565, 23, 10, TextColor);
            detail = LabelAt(this, "Обновления проверяются перед запуском.", 283, 446, 565, 22, 9, MutedColor);
            progress = new ProgressBar { Location = new Point(284, 481), Size = new Size(580, 6), Maximum = 1000, Style = ProgressBarStyle.Continuous }; Controls.Add(progress);
            play = ButtonAt(this, "Играть  →", 283, 511, 420, 48, true);
            cancel = ButtonAt(this, "Отмена", 719, 511, 145, 48, false); cancel.Enabled = false;
            industrial.Checked = settings.Server != "beta"; beta.Checked = settings.Server == "beta";
            beta.CheckedChanged += async delegate { if (beta.Checked) { RefreshServer(); if (!preview && Visible) await LoadStatus(); } };
            industrial.CheckedChanged += async delegate { if (industrial.Checked) { RefreshServer(); if (!preview && Visible) await LoadStatus(); } };
            if (!preview) Shown += async delegate { await Task.WhenAll(LoadStatus(), CheckLauncher(false)); };
            menu.Click += delegate { OpenSettings(); };
            play.Click += async delegate { await RunUpdate(true); };
            cancel.Click += delegate { if (cancellation != null) { cancellation.Cancel(); cancel.Enabled = false; status.Text = "Отменяем загрузку…"; } };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (running) { e.Cancel = true; WindowState = FormWindowState.Minimized; }
                else if (busy) { e.Cancel = true; if (cancellation != null) cancellation.Cancel(); }
            };
            RefreshServer(); AcceptButton = play;
        }
        static RadioButton ServerChoice(Control parent, string text, int y) {
            var button = new RadioButton { Text = text, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(18, y), Size = new Size(208, 96), Padding = new Padding(12, 8, 8, 8), FlatStyle = FlatStyle.Flat, BackColor = PanelColor, ForeColor = MutedColor, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10) };
            button.FlatAppearance.BorderSize = 1; button.FlatAppearance.BorderColor = Color.FromArgb(41, 58, 78); button.FlatAppearance.CheckedBackColor = Color.FromArgb(29, 60, 81);
            parent.Controls.Add(button); return button;
        }
        void RefreshServer() {
            bool classic = Selected == "beta";
            title.Text = classic ? "Minecraft Beta" : "Industrial";
            description.Text = classic ? "Beta 1.7.3 · Классический мир, знакомый с первых блоков.\nРусский язык и OptiFine уже в сборке." : "Minecraft 1.4.7 · От первой печи до большого производства.\nIC2, BuildCraft, Forestry, GregTech и другие моды.";
            address.Text = "friendzone-order.duckdns.org:" + (classic ? "25565" : "25566");
            beta.ForeColor = beta.Checked ? TextColor : MutedColor; industrial.ForeColor = industrial.Checked ? TextColor : MutedColor;
        }
        async Task CheckLauncher(bool manual) {
            if (busy || running || checkingLauncher) return;
            checkingLauncher = true; launcherUpdate.Enabled = false; launcherUpdate.Text = "Проверяем версию…";
            try {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var web = new WebTransport("https://friendzone-order.duckdns.org/")) {
                    var catalog = await new Updater(web, null, Path.Combine(Settings.Folder, "trusted-catalog.json")).GetCatalog(timeout.Token);
                    availableLauncher = LauncherUpdate.Newer(catalog.launcher, LauncherUpdate.Current) ? catalog.launcher : null;
                    launcherUpdate.Text = availableLauncher == null ? "Лаунчер обновлён · проверить" : "Обновить лаунчер\nдо " + new Version(availableLauncher.version).ToString(3);
                    if (manual && availableLauncher == null) MessageBox.Show(this, "Установлена свежая версия лаунчера.", "FZO Launcher");
                }
            } catch (Exception ex) {
                availableLauncher = null; launcherUpdate.Text = "Повторить проверку версии";
                if (manual) ShowError(ex);
            } finally { checkingLauncher = false; if (!IsDisposed) launcherUpdate.Enabled = !busy && !running; }
        }
        async Task InstallLauncher() {
            if (busy || running || checkingLauncher || availableLauncher == null) return;
            if (MessageBox.Show(this, "Скачать новую версию и перезапустить лаунчер?\nНастройки и игровые клиенты сохранятся.",
                "Обновление лаунчера", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            cancellation = new CancellationTokenSource(); SetBusy(true);
            status.Text = "Скачиваем обновление лаунчера"; detail.Text = "Проверяем подпись каталога и SHA-256 файла";
            progress.Value = 0;
            try {
                settings.Nickname = nickname.Text.Trim(); settings.Server = Selected; settings.Save();
                using (var web = new WebTransport("https://friendzone-order.duckdns.org/")) {
                    long done = 0;
                    IProgress<int> report = new Progress<int>(n => { done += n; detail.Text = "Скачано " + (done / 1024) + " КБ · проверка SHA-256"; });
                    string staged = await LauncherUpdate.Prepare(web, Settings.Folder, UpdateTrust.PublicKey(),
                        Path.Combine(Settings.Folder, "trusted-catalog.json"), LauncherUpdate.Current, n => report.Report(n), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    LauncherUpdate.StartHelper(staged);
                }
                SetBusy(false); Close();
            } catch (OperationCanceledException) { status.Text = "Обновление лаунчера отменено"; }
            catch (Exception ex) { status.Text = "Лаунчер не обновлён"; ShowError(ex); }
            finally { SetBusy(false); cancellation.Dispose(); cancellation = null; }
        }
        async Task LoadStatus() {
            string id = Selected; serverState.Text = "Проверяем статус сервера…";
            try {
                using (var token = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (var web = new WebTransport("https://friendzone-order.duckdns.org/")) {
                    var data = Json.Parse<Dictionary<string, object>>(await web.Read(id == "beta" ? "api/minecraft.json" : "api/industrial.json", 16384, token.Token));
                    if (!IsDisposed && id == Selected) {
                        bool online = data.ContainsKey("online") && Convert.ToBoolean(data["online"]);
                        serverState.Text = online ? "● Онлайн · " + data["players"] + " / " + data["maxPlayers"] + " игроков" : "● Сервер сейчас недоступен";
                    }
                }
            } catch { if (!IsDisposed && id == Selected) serverState.Text = "Статус сервера недоступен"; }
        }
        void SetBusy(bool value) {
            launcherUpdate.Enabled = !value && !running && !checkingLauncher;
            busy = value; play.Enabled = !value && !running; menu.Enabled = !value && !running;
            nickname.Enabled = !value && !running; beta.Enabled = !value && !running; industrial.Enabled = !value && !running;
            cancel.Enabled = value; play.Text = value ? "Проверка и обновление…" : "Играть  →";
        }
        string ClientRoot { get { return Path.Combine(settings.InstallRoot, Selected); } }
        void OpenSettings() {
            using (var dialog = new SettingsForm(settings, ClientRoot)) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    settings.Nickname = nickname.Text.Trim(); settings.Server = Selected;
                    try { settings.Save(); } catch (Exception ex) { ShowError(ex); return; }
                    detail.Text = "Память: " + settings.MemoryMB + " МБ · настройки сохранены";
                    if (dialog.Repair) { var unused = RunUpdate(false); }
                }
            }
        }
        void ShowError(Exception ex) {
            try { Directory.CreateDirectory(Settings.Folder); File.AppendAllText(Path.Combine(Settings.Folder, "launcher.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine); } catch { }
            MessageBox.Show(this, ex.Message + "\n\nСведения — в журнале лаунчера.", "FZO Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        async Task RunUpdate(bool launch) {
            if (busy || running) return;
            string nick = nickname.Text.Trim();
            if (launch && !Regex.IsMatch(nick, "^[A-Za-z0-9_]{3,16}$")) {
                MessageBox.Show(this, "Введите ник: 3–16 латинских букв, цифр или подчёркиваний.", "Никнейм", MessageBoxButtons.OK, MessageBoxIcon.Information); nickname.Focus(); return;
            }
            settings.Nickname = nick; settings.Server = Selected;
            string id = Selected, root = ClientRoot;
            cancellation = new CancellationTokenSource(); SetBusy(true);
            progress.Value = 0; status.Text = "Проверяем обновления…"; detail.Text = "Подключаемся к сайту FZO";
            var watch = Stopwatch.StartNew(); long previousDone = 0; double lastTime = 0; string phase = "";
            bool startingGame = false;
            var report = new Progress<UpdateProgress>(p => {
                status.Text = p.Message;
                progress.Value = p.Total > 0 ? (int)Math.Min(1000, p.Done * 1000 / p.Total) : 0;
                if (p.Message == "Скачиваем обновление") {
                    if (phase != p.Message) { previousDone = 0; lastTime = watch.Elapsed.TotalSeconds; phase = p.Message; }
                    double elapsed = watch.Elapsed.TotalSeconds - lastTime;
                    string rate = elapsed > 0.3 ? " · " + ((p.Done - previousDone) / 1048576.0 / elapsed).ToString("0.0") + " МБ/с" : "";
                    detail.Text = (p.Done / 1048576.0).ToString("0.0") + " / " + (p.Total / 1048576.0).ToString("0.0") + " МБ" + rate;
                    if (elapsed > 1) { previousDone = p.Done; lastTime = watch.Elapsed.TotalSeconds; }
                } else { phase = p.Message; detail.Text = "Ваши миры и личные настройки сохраняются."; }
            });
            try {
                settings.Save();
                int count; ServerInfo selected;
                using (var web = new WebTransport("https://friendzone-order.duckdns.org/")) {
                    var updater = new Updater(web, null, Path.Combine(Settings.Folder, "trusted-catalog.json"));
                    var catalog = await updater.GetCatalog(cancellation.Token);
                    selected = catalog.servers.Single(s => s.id == id);
                    count = await Task.Run(() => updater.Update(selected, root, report, cancellation.Token));
                }
                status.Text = "Клиент готов · " + selected.version; progress.Value = 1000;
                detail.Text = count == 0 ? "Все файлы актуальны. Ничего скачивать не понадобилось." : "Загружено файлов: " + count;
                cancel.Enabled = false;
                if (launch) { startingGame = true; await Launch(id, root, nick); }
            } catch (OperationCanceledException) { status.Text = "Обновление отменено"; detail.Text = "Повторите запуск, когда будете готовы."; }
            catch (GameProcessException ex) { status.Text = "Игра завершилась с ошибкой"; detail.Text = "Подробности — в журнале игры."; ShowError(ex); }
            catch (Exception ex) { status.Text = startingGame ? "Не удалось запустить игру" : "Не удалось подготовить клиент"; detail.Text = "Подробности — в журнале лаунчера."; ShowError(ex); }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        }
        async Task Launch(string id, string root, string nick) {
            var info = Game.StartInfo(id, root, nick, settings.MemoryMB, GameBootstrap.Prepare(Settings.Folder));
            if (!File.Exists(info.FileName)) throw new FileNotFoundException("Java клиента не найдена. Запустите проверку файлов.");
            string logDir = Path.Combine(root, "logs"); Directory.CreateDirectory(logDir);
            if (id == "beta") Directory.CreateDirectory(Path.Combine(root, @"data\.minecraft"));
            var exitState = new GameExitState();
            var reportsBefore = GameExitState.CrashReports(root, id);
            using (var clientLock = new FileStream(Path.Combine(root, ".fzo-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var statsLock = PlayerStats.Open(Settings.Folder, id, root))
            using (var log = new StreamWriter(Path.Combine(logDir, "launcher-game.log"), false, new UTF8Encoding(false)))
            using (var process = new Process { StartInfo = info }) {
                object gate = new object();
                DataReceivedEventHandler output = delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { log.WriteLine(e.Data); log.Flush(); exitState.Observe(e.Data); } };
                process.OutputDataReceived += output; process.ErrorDataReceived += output;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                running = true; cancel.Enabled = false; play.Text = "Игра запущена"; status.Text = "Игра запущена · " + nick;
                detail.Text = "Сервер: " + (id == "beta" ? "Beta 1.7.3" : "Industrial 1.4.7") + " · " + settings.MemoryMB + " МБ";
                if (settings.MinimizeOnLaunch) WindowState = FormWindowState.Minimized;
                try { await Task.Run(() => process.WaitForExit()); }
                finally { running = false; WindowState = FormWindowState.Normal; }
                bool newCrashReport = GameExitState.CrashReports(root, id).Any(pair => !reportsBefore.ContainsKey(pair.Key) || reportsBefore[pair.Key] != pair.Value);
                bool expected; lock (gate) expected = exitState.Expected(process.ExitCode, newCrashReport);
                if (!expected) throw new GameProcessException("Minecraft завершился с кодом " + process.ExitCode + ". Журнал: " + Path.Combine(logDir, "launcher-game.log"));
                status.Text = "До следующей игры"; detail.Text = "Клиент готов к повторному запуску.";
            }
        }
    }

    public class SettingsForm : Form {
        readonly Settings settings;
        readonly TrackBar memory;
        readonly TextBox folder;
        readonly CheckBox minimize;
        bool copying;
        public bool Repair;
        [StructLayout(LayoutKind.Sequential)] class MemoryStatus { public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)); public uint Load; public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);
        public SettingsForm(Settings value, string clientRoot) {
            settings = value; Text = "Настройки · FZO"; ClientSize = new Size(542, 480); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = LauncherForm.BackgroundColor; ForeColor = LauncherForm.TextColor; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            LauncherForm.LabelAt(this, "Настройки лаунчера", 24, 20, 490, 35, 19, ForeColor);
            var memoryInfo = new MemoryStatus(); int max = 8192;
            if (GlobalMemoryStatusEx(memoryInfo)) max = Math.Max(512, Math.Min(8192, ((int)(memoryInfo.Total / 1048576) - 2048) / 256 * 256));
            var amount = LauncherForm.LabelAt(this, "", 24, 76, 490, 25, 11, ForeColor);
            memory = new TrackBar { Location = new Point(18, 106), Size = new Size(500, 45), Minimum = 2, Maximum = Math.Max(2, max / 256), TickFrequency = 2, SmallChange = 1, LargeChange = 2, Value = Math.Max(2, Math.Min(max / 256, settings.MemoryMB / 256)) };
            Controls.Add(memory);
            Action refresh = () => { amount.Text = "Максимальная память Minecraft: " + (memory.Value * 256) + " МБ"; }; refresh(); memory.ValueChanged += delegate { refresh(); };
            LauncherForm.LabelAt(this, "Beta: от 1024 МБ · Industrial: от 2048 МБ.\nЛаунчер оставляет около 2 ГБ для Windows, когда это возможно.", 24, 153, 495, 44, 9, LauncherForm.MutedColor);
            LauncherForm.LabelAt(this, "Папка клиентов", 24, 214, 490, 24, 10, ForeColor);
            folder = LauncherForm.TextBoxAt(this, settings.InstallRoot, 24, 243, 386); folder.Font = new Font("Segoe UI", 9); folder.ReadOnly = true;
            var browse = LauncherForm.ButtonAt(this, "Выбрать", 421, 240, 96, 30, false);
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "Папка для клиентов FZO", SelectedPath = folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
            LauncherForm.LabelAt(this, "Клиенты можно скопировать в новую папку без повторной загрузки.", 24, 278, 495, 24, 9, LauncherForm.MutedColor);
            minimize = new CheckBox { Text = "Сворачивать лаунчер после запуска игры", Checked = settings.MinimizeOnLaunch, Location = new Point(24, 315), Size = new Size(495, 26) }; Controls.Add(minimize);
            var repair = LauncherForm.ButtonAt(this, "Проверить файлы", 24, 365, 157, 36, false);
            var open = LauncherForm.ButtonAt(this, "Папка игры", 191, 365, 157, 36, false);
            var logs = LauncherForm.ButtonAt(this, "Журналы", 358, 365, 159, 36, false);
            repair.Click += async delegate { if (await SaveSettings()) { Repair = true; DialogResult = DialogResult.OK; } };
            open.Click += delegate { OpenFolder(Path.Combine(folder.Text, Path.GetFileName(clientRoot))); };
            logs.Click += delegate { OpenFolder(Settings.Folder); };
            var save = LauncherForm.ButtonAt(this, "Сохранить", 358, 425, 159, 36, true);
            var close = LauncherForm.ButtonAt(this, "Отмена", 191, 425, 157, 36, false);
            save.Click += async delegate { if (await SaveSettings()) DialogResult = DialogResult.OK; };
            close.DialogResult = DialogResult.Cancel; CancelButton = close; AcceptButton = save;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (copying) e.Cancel = true; };
        }
        async Task<bool> SaveSettings() {
            try {
                string path = Path.GetFullPath(folder.Text); Updater.CheckNoLinks(path);
                if (!String.Equals(path.TrimEnd('\\'), settings.InstallRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    && new[] { "beta", "industrial" }.Any(id => Directory.Exists(Path.Combine(settings.InstallRoot, id)))) {
                    var answer = MessageBox.Show(this, "Скопировать установленные клиенты в новую папку?\n\nМиры, скриншоты и настройки тоже скопируются. Старая папка останется на месте.\n\n«Нет» — начать новую установку в выбранной папке.", "Смена папки клиентов", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return false;
                    if (answer == DialogResult.Yes) {
                        copying = true; ControlBox = false; foreach (Control control in Controls) control.Enabled = false;
                        Text = "Копируем клиентов…";
                        try { await Task.Run(() => ClientFolders.Copy(settings.InstallRoot, path)); }
                        finally { copying = false; ControlBox = true; foreach (Control control in Controls) control.Enabled = true; Text = "Настройки · FZO"; }
                    }
                }
                settings.InstallRoot = path; settings.MemoryMB = memory.Value * 256; settings.MinimizeOnLaunch = minimize.Checked; return true;
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Папка недоступна"); return false; }
        }
        void OpenFolder(string path) {
            try { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", Game.Quote(path)) { UseShellExecute = true }); else MessageBox.Show(this, "Папка появится после первой установки.", "FZO"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "FZO"); }
        }
    }
    public static class ClientFolders {
        public static void Copy(string sourceRoot, string targetRoot, string settingsFolder = null) {
            settingsFolder = settingsFolder ?? Settings.Folder;
            string source = Path.GetFullPath(sourceRoot).TrimEnd('\\') + "\\", target = Path.GetFullPath(targetRoot).TrimEnd('\\') + "\\";
            if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Выберите отдельную папку, вне текущей папки клиентов.");
            Updater.CheckNoLinks(source); Updater.CheckNoLinks(target);
            Directory.CreateDirectory(target);
            string staging = Path.Combine(target, ".fzo-migration-" + Guid.NewGuid().ToString("N"));
            var locks = new List<FileStream>(); var moved = new List<string>();
            var ids = new[] { "beta", "industrial" }.Where(id => Directory.Exists(Path.Combine(source, id))).ToArray();
            try {
                foreach (string id in ids) {
                    if (Directory.Exists(Path.Combine(target, id)) || File.Exists(Path.Combine(target, id)))
                        throw new IOException("В новой папке уже есть " + id + ". Выберите пустую папку для копирования.");
                    string client = Path.Combine(source, id); Updater.CheckNoLinks(client);
                    string lockPath = Path.Combine(client, ".fzo-lock"); Updater.CheckNoLinks(lockPath);
                    locks.Add(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                    Updater.Recover(client);
                }
                foreach (string id in ids) CopyTree(Path.Combine(source, id), Path.Combine(staging, id), Path.Combine(source, id, PlayerStats.Relative(id)), id, settingsFolder);
                foreach (string id in ids) { Directory.Move(Path.Combine(staging, id), Path.Combine(target, id)); moved.Add(id); }
            } catch {
                // Only our newly created directories are moved back. Existing targets are never touched.
                foreach (string id in moved.AsEnumerable().Reverse()) Directory.Move(Path.Combine(target, id), Path.Combine(staging, id));
                throw;
            } finally {
                foreach (var fileLock in locks) fileLock.Dispose();
                if (Directory.Exists(staging)) { CheckTree(staging); Directory.Delete(staging, true); }
            }
        }
        static void CheckTree(string path) {
            Updater.CheckNoLinks(path);
            foreach (string file in Directory.GetFiles(path)) Updater.CheckNoLinks(file);
            foreach (string dir in Directory.GetDirectories(path)) CheckTree(dir);
        }
        static void CopyTree(string source, string target, string statsPath, string id, string settingsFolder) {
            Updater.CheckNoLinks(source); Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) {
                Updater.CheckNoLinks(file);
                if (Path.GetFileName(file) == ".fzo-lock") continue;
                string destination = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, destination, false);
                if (Updater.Hash(file) != Updater.Hash(destination)) throw new IOException("Ошибка проверки копии: " + file);
            }
            foreach (string directory in Directory.GetDirectories(source)) {
                if (Path.GetFileName(directory) == ".fzo-work") continue;
                if (String.Equals(directory, statsPath, StringComparison.OrdinalIgnoreCase) && PlayerStats.IsLinked(directory, settingsFolder, id)) continue;
                CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)), statsPath, id, settingsFolder);
            }
        }
    }
    static class Program {
        [STAThread] static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--apply-update") return LauncherUpdate.Apply(args);
            if (args.Length == 2 && args[0] == "--preview") {
                using (var form = new LauncherForm(new Settings { Nickname = "Player" }, true)) {
                    Render(form, args[1]);
                    using (var options = new SettingsForm(new Settings(), @"C:\FZO\industrial")) Render(options, Path.Combine(Path.GetDirectoryName(args[1]), "settings-preview.png"));
                }
                return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, @"Local\FZO-Launcher-v1", out created)) {
                if (!created) { MessageBox.Show("FZO Launcher уже запущен.", "FZO"); return 0; }
                try { Application.Run(new LauncherForm(Settings.Load())); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "FZO Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            return 0;
        }
        static void Render(Form form, string path) {
            form.Show(); Application.DoEvents();
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); }
            form.Hide();
        }
    }
}
