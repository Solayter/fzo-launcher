using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FZO;

class DiskTransport : ITransport {
    readonly string root;
    public int Count;
    public DiskTransport(string value) { root = value; }
    public Task<byte[]> Read(string relative, int limit, CancellationToken token) { return Task.FromResult(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', '\\')))); }
    public Task Download(string relative, string target, long size, Action<int> progress, CancellationToken token) {
        token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Count);
        File.Copy(Path.Combine(root, relative.Replace('/', '\\')), target); progress((int)size); return Task.FromResult(0);
    }
}
class SmokeProgress : IProgress<UpdateProgress> { public void Report(UpdateProgress value) { } }
class ReleaseSmoke {
    static int Main(string[] args) { try { Run(args).GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    static async Task Run(string[] args) {
        if (args[0] == "remote") {
            using (var web = new WebTransport("https://friendzone-order.duckdns.org/")) {
                var catalog = await new Updater(web).GetCatalog(CancellationToken.None);
                foreach (var server in catalog.servers) {
                    byte[] bytes = await web.Read("launcher/manifests/" + server.manifest + ".json", 12 * 1024 * 1024, CancellationToken.None);
                    if (Updater.Hash(bytes) != server.manifest) throw new Exception("Manifest integrity failed");
                    var manifest = Json.Parse<Manifest>(bytes);
                    Updater.Validate(manifest, server.id, args[1]);
                    var file = manifest.files.First(f => f.path == "game/minecraft.jar");
                    bytes = await web.Read("launcher/objects/" + file.sha256, (int)file.size, CancellationToken.None);
                    if (Updater.Hash(bytes) != file.sha256) throw new Exception("Object integrity failed");
                    Console.WriteLine("HTTPS OK: " + server.id + " " + manifest.version + ", manifest and game JAR verified");
                }
                var executable = await web.Read("downloads/FZO-Launcher.exe", 1024 * 1024, CancellationToken.None);
                if (Updater.Hash(executable) != Updater.Hash(args[2])) throw new Exception("Published executable differs from local build");
                Console.WriteLine("HTTPS OK: published launcher matches tested build");
            }
        } else {
            var web = new DiskTransport(args[0]); var updater = new Updater(web); var catalog = await updater.GetCatalog(CancellationToken.None);
            foreach (var server in catalog.servers) {
                string client = Path.Combine(args[1], server.id);
                int count = await updater.Update(server, client, new SmokeProgress(), CancellationToken.None);
                Console.WriteLine("FULL INSTALL OK: " + server.id + ", " + count + " files");
                web.Count = 0;
                if (await updater.Update(server, client, new SmokeProgress(), CancellationToken.None) != 0 || web.Count != 0) throw new Exception("Unchanged client downloaded files");
                Console.WriteLine("NO UPDATE OK: " + server.id + ", zero downloads");
                File.WriteAllText(Path.Combine(client, "game/minecraft.jar"), "damaged");
                web.Count = 0;
                if (await updater.Update(server, client, new SmokeProgress(), CancellationToken.None) != 1 || web.Count != 1) throw new Exception("Repair downloaded more than damaged jar");
                Console.WriteLine("REPAIR OK: " + server.id + ", only damaged JAR downloaded");
            }
        }
    }
}
