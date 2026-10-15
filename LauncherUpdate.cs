using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace FZO {
    public class LauncherRelease { public string version, sha256; public long size; }
    public static class LauncherUpdate {
        public static Version Current { get { return typeof(LauncherUpdate).Assembly.GetName().Version; } }
        public static void Validate(LauncherRelease release) {
            Version version;
            if (release == null || !Version.TryParse(release.version, out version) || version.Build < 0 || version.Revision < 0
                || version.Major < 1 || !Updater.IsHash(release.sha256) || release.size < 1024 || release.size > 32 * 1024 * 1024)
                throw new InvalidDataException("Некорректное описание обновления лаунчера.");
        }
        public static bool Newer(LauncherRelease release, Version current) {
            if (release == null) return false;
            Validate(release); return new Version(release.version) > current;
        }
        public static void VerifyFile(string path, LauncherRelease release) {
            Validate(release); Updater.CheckNoLinks(path);
            if (new FileInfo(path).Length != release.size || Updater.Hash(path) != release.sha256)
                throw new InvalidDataException("Файл обновления лаунчера не прошёл проверку SHA-256.");
            if (AssemblyName.GetAssemblyName(path).Version != new Version(release.version))
                throw new InvalidDataException("Версия EXE отличается от подписанного описания.");
        }
        public static async Task<string> Prepare(ITransport web, string folder, string publicKey, string cache,
            Version current, Action<int> report, CancellationToken token) {
            byte[] bytes = await web.Read("launcher/catalog.signed.json", 131072, token);
            var catalog = UpdateTrust.Authenticate(bytes, publicKey, cache);
            if (!Newer(catalog.launcher, current)) throw new InvalidDataException("Новой версии лаунчера нет.");
            UpdateTrust.Remember(bytes, cache);
            Updater.CheckNoLinks(folder); Directory.CreateDirectory(folder);
            string stage = Path.Combine(folder, "self-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string exe = Path.Combine(stage, "FZO-Launcher.exe");
            try {
                File.WriteAllBytes(Path.Combine(stage, "catalog.signed.json"), bytes);
                await web.Download("launcher/objects/" + catalog.launcher.sha256, exe, catalog.launcher.size, report, token);
                token.ThrowIfCancellationRequested(); VerifyFile(exe, catalog.launcher);
                return exe;
            } catch {
                // Only files in this freshly created directory can be removed.
                foreach (string name in new[] { "FZO-Launcher.exe", "catalog.signed.json" }) {
                    string file = Path.Combine(stage, name); Updater.CheckNoLinks(file);
                    if (File.Exists(file)) File.Delete(file);
                }
                Directory.Delete(stage); throw;
            }
        }
        public static void StartHelper(string staged) {
            string target = Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo(staged, "--apply-update " + Process.GetCurrentProcess().Id + " "
                + Game.Quote(target) + " " + Updater.Hash(target)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(staged) });
        }
        public static string Replace(string source, string target, LauncherRelease release, string oldHash) {
            source = Path.GetFullPath(source); target = Path.GetFullPath(target);
            if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Недопустимое место установки лаунчера.");
            VerifyFile(source, release); Updater.CheckNoLinks(target);
            if (!Updater.IsHash(oldHash) || Updater.Hash(target) != oldHash)
                throw new IOException("Лаунчер на диске изменился. Обновление остановлено.");
            if (!Newer(release, AssemblyName.GetAssemblyName(target).Version))
                throw new InvalidDataException("Откат версии лаунчера запрещён.");
            string next = target + ".update-" + Guid.NewGuid().ToString("N");
            string backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            Updater.CheckNoLinks(next); Updater.CheckNoLinks(backup);
            try {
                File.Copy(source, next, false); VerifyFile(next, release);
                File.Replace(next, target, backup);
                return backup;
            } finally { if (File.Exists(next)) File.Delete(next); }
        }
        public static int Apply(string[] args) {
            try {
                if (args.Length != 4) throw new ArgumentException("Неверные параметры обновления.");
                string self = Assembly.GetExecutingAssembly().Location;
                string envelope = Path.Combine(Path.GetDirectoryName(self), "catalog.signed.json");
                Updater.CheckNoLinks(envelope);
                if (new FileInfo(envelope).Length > 131072) throw new InvalidDataException("Каталог слишком большой.");
                var catalog = UpdateTrust.Authenticate(File.ReadAllBytes(envelope), UpdateTrust.PublicKey(), Path.Combine(Settings.Folder, "trusted-catalog.json"));
                VerifyFile(self, catalog.launcher);
                int pid;
                if (!Int32.TryParse(args[1], out pid) || pid <= 0 || pid == Process.GetCurrentProcess().Id) throw new ArgumentException("Неверный процесс лаунчера.");
                try { using (var parent = Process.GetProcessById(pid)) {
                    if (!parent.HasExited && !String.Equals(parent.MainModule.FileName, Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Процесс лаунчера не совпадает с обновляемым файлом.");
                    if (!parent.WaitForExit(30000)) throw new IOException("Закройте лаунчер и повторите обновление.");
                } } catch (ArgumentException) { /* Parent has already exited. */ }
                string backup = Replace(self, args[2], catalog.launcher, args[3]);
                try {
                    Process.Start(new ProcessStartInfo(args[2]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(args[2]) });
                } catch {
                    // A failure to start restores the exact original executable.
                    File.Replace(backup, args[2], null); throw;
                }
                // Keep the backup for manual recovery; no client/settings files are touched.
                return 0;
            } catch (Exception ex) {
                System.Windows.Forms.MessageBox.Show("Не удалось обновить лаунчер: " + ex.Message
                    + "\n\nМожно скачать свежую версию с сайта. Если папка защищена, перенесите лаунчер в свою папку.",
                    "Обновление FZO", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
