using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FZO {
    public class Catalog { public int schema; public long sequence, issuedAt, expiresAt; public List<ServerInfo> servers; }
    public class ServerInfo { public string id, name, version, manifest, host; public int port; internal bool FromTrustedCatalog; }
    public class FileEntry { public string path, sha256, mode; public long size; }
    public class Manifest { public int schema; public string id, version; public List<FileEntry> files; }
    public class UpdateProgress { public string Message; public long Done, Total; }
    public class MoveEntry { public string path; public bool original; }
    public class Journal { public List<MoveEntry> moves = new List<MoveEntry>(); }

    public interface ITransport {
        Task<byte[]> Read(string relative, int limit, CancellationToken token);
        Task Download(string relative, string target, long size, Action<int> progress, CancellationToken token);
    }
    public sealed class WebTransport : ITransport, IDisposable {
        readonly HttpClient client;
        public WebTransport(string origin) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.BaseAddress = new Uri(origin);
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FZOLauncher/1.0");
        }
        public async Task<byte[]> Read(string relative, int limit, CancellationToken token) {
            using (var response = await client.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, token)) {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new MemoryStream()) {
                    var buffer = new byte[65536]; int n;
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0) {
                        if (output.Length + n > limit) throw new InvalidDataException("Ответ сайта слишком большой.");
                        output.Write(buffer, 0, n);
                    }
                    return output.ToArray();
                }
            }
        }
        public async Task Download(string relative, string target, long size, Action<int> progress, CancellationToken token) {
            using (var response = await client.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, token)) {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != size)
                    throw new InvalidDataException("Размер загрузки отличается от манифеста.");
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) {
                    long count = 0; int n; var buffer = new byte[65536];
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0) {
                        count += n;
                        if (count > size) throw new InvalidDataException("Файл больше ожидаемого.");
                        await output.WriteAsync(buffer, 0, n, token); progress(n);
                    }
                    if (count != size) throw new InvalidDataException("Загрузка файла оборвалась.");
                    output.Flush(true);
                }
            }
        }
        public void Dispose() { client.Dispose(); }
    }

    public static class Json {
        public static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public static T Parse<T>(byte[] data) { return Serializer.Deserialize<T>(Encoding.UTF8.GetString(data)); }
        public static T Read<T>(string path) { return Parse<T>(File.ReadAllBytes(path)); }
        public static void Write(string path, object value) {
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }

    public sealed class Updater {
        readonly ITransport transport;
        readonly string trustedPublicKey, catalogCache;
        public static bool IsHash(string hash) { return hash != null && Regex.IsMatch(hash, "^[a-f0-9]{64}$"); }
        public static string Hash(string path) { using (var stream = File.OpenRead(path)) return Hash(stream); }
        public static string Hash(Stream stream) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        public static string Hash(byte[] bytes) { using (var stream = new MemoryStream(bytes)) return Hash(stream); }
        public Updater(ITransport value, string publicKey = null, string cachePath = null) { transport = value; trustedPublicKey = publicKey ?? UpdateTrust.PublicKey(); catalogCache = cachePath; }

        public static void CheckNoLinks(string path) {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Папка клиента не должна содержать ссылки: " + current.FullName);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Файл клиента является ссылкой: " + path);
        }
        public static string SafePath(string root, string relative) {
            if (String.IsNullOrWhiteSpace(relative) || relative.Contains("\\") || relative.Contains(":")) throw new InvalidDataException("Недопустимый путь.");
            foreach (string part in relative.Split('/')) {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ") || part.EndsWith(".") || part.StartsWith(".fzo-", StringComparison.OrdinalIgnoreCase)
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Недопустимый путь: " + relative);
            }
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь за пределами клиента.");
            CheckNoLinks(result); return result;
        }
        public static bool Personal(string path) {
            string p = "/" + path.ToLowerInvariant() + "/";
            return new[] {"/saves/", "/screenshots/", "/logs/", "/crash-reports/", "/stats/", "/texturepacks/", "/resourcepacks/"}.Any(p.Contains)
                || Regex.IsMatch(path, @"(^|/)(nickname\.txt|lastlogin|session\.lock)$", RegexOptions.IgnoreCase)
                || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }
        public static bool Seed(string path) {
            return Regex.IsMatch(path, @"(^|/)(options\.txt|optionsof\.txt|servers\.dat|servers\.dat_old|InvTweaks\.cfg|InvTweaksRules\.txt|InvTweaksTree\.txt|NEI\.cfg|NEISubset\.cfg)$", RegexOptions.IgnoreCase);
        }
        public static void Validate(Manifest manifest, string id, string root) {
            if (manifest == null || manifest.schema != 1 || manifest.id != id || String.IsNullOrEmpty(manifest.version) || manifest.files == null || manifest.files.Count == 0 || manifest.files.Count > 50000)
                throw new InvalidDataException("Неподдерживаемый манифест клиента.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in manifest.files) {
                if (file == null || !IsHash(file.sha256) || file.size < 0 || file.size > 536870912 || (file.mode != "managed" && file.mode != "seed"))
                    throw new InvalidDataException("Некорректная запись файла.");
                SafePath(root, file.path);
                if (!paths.Add(file.path) || Personal(file.path) || (Seed(file.path) && file.mode != "seed"))
                    throw new InvalidDataException("Манифест затрагивает личные файлы или содержит дубликат.");
                total += file.size;
            }
            if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Сборка слишком большая.");
        }
        public async Task<Catalog> GetCatalog(CancellationToken token) {
            byte[] bytes = await transport.Read("launcher/catalog.signed.json", 131072, token);
            var catalog = UpdateTrust.Authenticate(bytes, trustedPublicKey, catalogCache);
            if (catalog == null || catalog.schema != 1 || catalog.servers == null || catalog.servers.Count != 2 ||
                catalog.servers.Select(s => s.id).Distinct().Count() != 2 || catalog.servers.Any(s => (s.id != "beta" && s.id != "industrial") || !IsHash(s.manifest)
                    || s.host != "friendzone-order.duckdns.org" || s.port != (s.id == "beta" ? 25565 : 25566)))
                throw new InvalidDataException("Неподдерживаемый каталог серверов.");
            foreach (var server in catalog.servers) server.FromTrustedCatalog = true;
            UpdateTrust.Remember(bytes, catalogCache);
            return catalog;
        }
        static string Internal(string root, string relative) {
            if (relative == ".fzo-installed.json") return Path.Combine(root, relative);
            return SafePath(root, relative);
        }
        static void CleanWork(string root) {
            string work = Path.Combine(root, ".fzo-work");
            CheckNoLinks(work);
            if (Directory.Exists(work)) {
                // Recheck descendants before recursive removal; never follow a junction.
                CheckTree(work);
                Directory.Delete(work, true);
            }
        }
        static void CheckTree(string directory) {
            CheckNoLinks(directory);
            foreach (string file in Directory.GetFiles(directory)) CheckNoLinks(file);
            foreach (string subdir in Directory.GetDirectories(directory)) CheckTree(subdir);
        }
        public static void Recover(string root) {
            string work = Path.Combine(root, ".fzo-work"), journalPath = Path.Combine(work, "journal.json");
            CheckNoLinks(work); CheckNoLinks(journalPath);
            if (!File.Exists(journalPath)) { CleanWork(root); return; }
            var journal = Json.Read<Journal>(journalPath);
            foreach (var move in journal.moves.AsEnumerable().Reverse()) {
                string target = Internal(root, move.path), backup = Internal(Path.Combine(work, "backup"), move.path);
                string staged = Internal(Path.Combine(work, "stage"), move.path);
                CheckNoLinks(target); CheckNoLinks(backup); CheckNoLinks(staged);
                if (move.original && File.Exists(backup)) {
                    if (File.Exists(target)) File.Delete(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(backup, target);
                } else if (!move.original && !File.Exists(staged) && File.Exists(target)) File.Delete(target);
            }
            CleanWork(root);
        }
        public async Task<int> Update(ServerInfo server, string root, IProgress<UpdateProgress> progress, CancellationToken token) {
            if (server == null || !server.FromTrustedCatalog) throw new InvalidDataException("Сборка не подтверждена подписанным каталогом.");
            Directory.CreateDirectory(root); CheckNoLinks(root);
            string lockPath = Path.Combine(root, ".fzo-lock"); CheckNoLinks(lockPath);
            using (var clientLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                Recover(root);
                progress.Report(new UpdateProgress { Message = "Получаем список файлов…" });
                byte[] data = await transport.Read("launcher/manifests/" + server.manifest + ".json", 12 * 1024 * 1024, token);
                if (Hash(data) != server.manifest) throw new InvalidDataException("Контрольная сумма манифеста не совпала.");
                Manifest manifest = Json.Parse<Manifest>(data); Validate(manifest, server.id, root);
                if (manifest.version != server.version) throw new InvalidDataException("Версия манифеста отличается от подписанного каталога.");
                Manifest previous = null;
                string installed = Path.Combine(root, ".fzo-installed.json"); CheckNoLinks(installed);
                if (File.Exists(installed)) { previous = Json.Read<Manifest>(installed); Validate(previous, server.id, root); }
                var changed = new List<FileEntry>(); int checkedCount = 0;
                foreach (var file in manifest.files) {
                    token.ThrowIfCancellationRequested();
                    string path = SafePath(root, file.path);
                    if (!(file.mode == "seed" && File.Exists(path)) && (!File.Exists(path) || new FileInfo(path).Length != file.size || Hash(path) != file.sha256)) changed.Add(file);
                    progress.Report(new UpdateProgress { Message = "Проверяем файлы: " + (++checkedCount) + " / " + manifest.files.Count, Done = checkedCount, Total = manifest.files.Count });
                }
                var current = new HashSet<string>(manifest.files.Select(f => f.path), StringComparer.OrdinalIgnoreCase);
                var removed = previous == null ? new List<FileEntry>() : previous.files.Where(f => f.mode == "managed" && !Seed(f.path) && !current.Contains(f.path) && File.Exists(SafePath(root, f.path))).ToList();
                string work = Path.Combine(root, ".fzo-work"), stage = Path.Combine(work, "stage"), backupRoot = Path.Combine(work, "backup");
                Directory.CreateDirectory(stage);
                long total = changed.Sum(f => f.size), done = 0;
                try {
                    using (var failed = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (var slots = new SemaphoreSlim(4)) {
                        var jobs = changed.Select(async file => {
                            await slots.WaitAsync(failed.Token);
                            try {
                                string target = SafePath(stage, file.path); Directory.CreateDirectory(Path.GetDirectoryName(target));
                                await transport.Download("launcher/objects/" + file.sha256, target, file.size, n => {
                                    long downloaded = Interlocked.Add(ref done, n);
                                    progress.Report(new UpdateProgress { Message = "Скачиваем обновление", Done = downloaded, Total = total });
                                }, failed.Token);
                                if (Hash(target) != file.sha256) throw new InvalidDataException("Повреждён файл: " + file.path);
                            } catch { failed.Cancel(); throw; } finally { slots.Release(); }
                        }).ToArray();
                        await Task.WhenAll(jobs);
                    }
                    token.ThrowIfCancellationRequested();
                    progress.Report(new UpdateProgress { Message = "Применяем обновление…", Done = total, Total = total });
                    File.WriteAllBytes(Path.Combine(stage, ".fzo-installed.json"), data);
                    var journal = new Journal();
                    foreach (var file in changed) journal.moves.Add(new MoveEntry { path = file.path, original = File.Exists(SafePath(root, file.path)) });
                    foreach (var file in removed) journal.moves.Add(new MoveEntry { path = file.path, original = true });
                    journal.moves.Add(new MoveEntry { path = ".fzo-installed.json", original = File.Exists(installed) });
                    Json.Write(Path.Combine(work, "journal.json"), journal);
                    try {
                        foreach (var move in journal.moves) {
                            string target = Internal(root, move.path), backup = Internal(backupRoot, move.path), staged = Internal(stage, move.path);
                            CheckNoLinks(target);
                            if (move.original) { Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Move(target, backup); }
                            if (File.Exists(staged)) { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(staged, target); }
                        }
                    } catch { Recover(root); throw; }
                    // Removing the journal is the commit point. Backups become disposable afterwards.
                    File.Delete(Path.Combine(work, "journal.json"));
                    CleanWork(root);
                    return changed.Count;
                } catch {
                    if (!File.Exists(Path.Combine(work, "journal.json"))) CleanWork(root);
                    throw;
                }
            }
        }
    }
}
