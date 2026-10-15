using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using FZO;

class FakeTransport : ITransport {
    public readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
    public int Downloads;
    public bool Corrupt, Cancel;
    public Task<byte[]> Read(string relative, int limit, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Data[relative]); }
    public Task Download(string relative, string target, long size, Action<int> progress, CancellationToken token) {
        token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Downloads);
        File.WriteAllBytes(target, Corrupt ? Encoding.UTF8.GetBytes("CORRUPTED") : Data[relative]);
        if (Cancel) throw new OperationCanceledException();
        progress((int)size); return Task.FromResult(0);
    }
}
class IgnoreProgress : IProgress<UpdateProgress> { public void Report(UpdateProgress p) { } }
class Tests {
    static readonly RSACryptoServiceProvider SigningKey = CreateKey();
    static RSACryptoServiceProvider CreateKey() { var rsa = new RSACryptoServiceProvider(3072); rsa.PersistKeyInCsp = false; return rsa; }
    static long sequence;
    static byte[] Signed(Catalog catalog, RSACryptoServiceProvider key) {
        byte[] payload = Bytes(Json.Serializer.Serialize(catalog));
        return Bytes(Json.Serializer.Serialize(new CatalogEnvelope { schema = 1, algorithm = "RSA-SHA256", payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256"))) }));
    }
    static Catalog TestCatalog(string hash, string version) {
        return new Catalog { schema = 1, sequence = ++sequence, issuedAt = UpdateTrust.UnixTime() - 1, expiresAt = UpdateTrust.UnixTime() + 86400,
            servers = new List<ServerInfo> {
                new ServerInfo { id = "industrial", version = version, manifest = hash, host = "friendzone-order.duckdns.org", port = 25566 },
                new ServerInfo { id = "beta", version = "beta", manifest = hash, host = "friendzone-order.duckdns.org", port = 25565 }
            } };
    }
    static int assertions;
    static void Assert(bool test, string name) { assertions++; if (!test) throw new Exception("FAIL: " + name); Console.WriteLine("OK " + name); }
    static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
    static FileEntry Entry(string path, string content, string mode) { return new FileEntry { path = path, size = Bytes(content).Length, sha256 = Updater.Hash(Bytes(content)), mode = mode }; }
    static ServerInfo Release(FakeTransport web, string version, params FileEntry[] entries) {
        var manifest = new Manifest { schema = 1, id = "industrial", version = version, files = entries.ToList() };
        var data = Bytes(Json.Serializer.Serialize(manifest)); string hash = Updater.Hash(data);
        web.Data["launcher/manifests/" + hash + ".json"] = data;
        web.Data["launcher/catalog.signed.json"] = Signed(TestCatalog(hash, version), SigningKey);
        return new Updater(web, SigningKey.ToXmlString(false)).GetCatalog(CancellationToken.None).GetAwaiter().GetResult().servers.Single(s => s.id == "industrial");
    }
    static void Object(FakeTransport web, string content) { web.Data["launcher/objects/" + Updater.Hash(Bytes(content))] = Bytes(content); }
    static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    static int Main(string[] args) {
        string baseRoot = Path.Combine(Path.GetFullPath(args[0]), "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(baseRoot);
        try { Run(baseRoot).GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            // Delete only the unique test directory verified inside the named test root.
            if (baseRoot.StartsWith(Path.GetFullPath(args[0]).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) Directory.Delete(baseRoot, true);
        }
    }
    static async Task Run(string root) {
        var web = new FakeTransport(); var updater = new Updater(web, SigningKey.ToXmlString(false)); var progress = new IgnoreProgress();
        foreach (string bad in new[] { "../escape", "/absolute", "C:/outside", "mods/../../x", "mods\\evil", "mods/a:ads", "mods/CON.jar", "mods/trailing.", ".fzo-installed.json", "mods//file" })
            Assert(Throws(() => Updater.SafePath(root, bad)), "reject unsafe path " + bad);
        Assert(Updater.SafePath(root, "game/mods/a.jar").StartsWith(root), "accept regular client path");
        foreach (string value in new[] { "OLD", "NEW", "LIB", "DEFAULT", "MOD2" }) Object(web, value);
        var first = Release(web, "r1", Entry("game/mods/a.jar", "OLD", "managed"), Entry("game/lib/library.jar", "LIB", "managed"), Entry("game/options.txt", "DEFAULT", "seed"));
        Assert(await updater.Update(first, root, progress, CancellationToken.None) == 3, "first install downloads all missing files");
        Assert(File.ReadAllText(Path.Combine(root, "game/mods/a.jar")) == "OLD", "first install commits verified content");
        File.WriteAllText(Path.Combine(root, "game/options.txt"), "MY OPTIONS");
        string player = Path.Combine(root, "game/saves/world"); Directory.CreateDirectory(player); File.WriteAllText(Path.Combine(player, "level.dat"), "PLAYER WORLD");
        File.WriteAllText(Path.Combine(root, "game/mods/personal.jar"), "PERSONAL MOD");
        web.Downloads = 0;
        Assert(await updater.Update(first, root, progress, CancellationToken.None) == 0 && web.Downloads == 0, "unchanged client makes no file downloads");
        var second = Release(web, "r2", Entry("game/mods/a.jar", "NEW", "managed"), Entry("game/lib/library.jar", "LIB", "managed"), Entry("game/options.txt", "DEFAULT", "seed"));
        web.Downloads = 0;
        Assert(await updater.Update(second, root, progress, CancellationToken.None) == 1 && web.Downloads == 1, "incremental update downloads only changed jar");
        Assert(File.ReadAllText(Path.Combine(root, "game/options.txt")) == "MY OPTIONS", "player options are preserved");
        Assert(File.ReadAllText(Path.Combine(player, "level.dat")) == "PLAYER WORLD", "player world is preserved");
        Assert(File.Exists(Path.Combine(root, "game/mods/personal.jar")), "unmanaged mod is preserved");
        File.WriteAllText(Path.Combine(root, "game/lib/library.jar"), "BAD");
        Assert(await updater.Update(second, root, progress, CancellationToken.None) == 1, "repair restores damaged managed file");
        var third = Release(web, "r3", Entry("game/mods/b.jar", "MOD2", "managed"), Entry("game/lib/library.jar", "LIB", "managed"));
        await updater.Update(third, root, progress, CancellationToken.None);
        Assert(!File.Exists(Path.Combine(root, "game/mods/a.jar")) && File.Exists(Path.Combine(root, "game/mods/b.jar")), "obsolete managed mod is removed");
        Assert(File.ReadAllText(Path.Combine(root, "game/options.txt")) == "MY OPTIONS", "obsolete seed is retained");
        var failed = Release(web, "r4", Entry("game/mods/b.jar", "NEW", "managed"), Entry("game/lib/library.jar", "LIB", "managed"));
        web.Corrupt = true; bool caught = false;
        try { await updater.Update(failed, root, progress, CancellationToken.None); } catch (InvalidDataException) { caught = true; }
        Assert(caught, "corrupted download is rejected by SHA256");
        Assert(File.ReadAllText(Path.Combine(root, "game/mods/b.jar")) == "MOD2", "failed download leaves working client intact");
        Assert(Json.Read<Manifest>(Path.Combine(root, ".fzo-installed.json")).version == "r3", "failed download retains installed version");
        web.Corrupt = false; web.Cancel = true; caught = false;
        try { await updater.Update(failed, root, progress, CancellationToken.None); } catch (OperationCanceledException) { caught = true; }
        Assert(caught && File.ReadAllText(Path.Combine(root, "game/mods/b.jar")) == "MOD2", "cancelled download leaves client intact");
        web.Cancel = false;
        // Simulate a sharing violation during commit, after the earlier file was already replaced.
        var commitFailure = Release(web, "r5", Entry("game/mods/b.jar", "NEW", "managed"), Entry("game/lib/library.jar", "NEW", "managed"));
        caught = false;
        using (var locked = new FileStream(Path.Combine(root, "game/lib/library.jar"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try { await updater.Update(commitFailure, root, progress, CancellationToken.None); } catch (IOException) { caught = true; }
        }
        Assert(caught && File.ReadAllText(Path.Combine(root, "game/mods/b.jar")) == "MOD2", "commit failure rolls back earlier replacements");
        Assert(File.ReadAllText(Path.Combine(root, "game/lib/library.jar")) == "LIB", "locked original is preserved");
        // Simulate interruption immediately after backup + replacement, before commit marker removal.
        string work = Path.Combine(root, ".fzo-work"); string backup = Path.Combine(work, "backup/game/mods");
        Directory.CreateDirectory(backup); Directory.CreateDirectory(Path.Combine(work, "stage"));
        File.Move(Path.Combine(root, "game/mods/b.jar"), Path.Combine(backup, "b.jar"));
        File.WriteAllText(Path.Combine(root, "game/mods/b.jar"), "HALF UPDATE");
        Json.Write(Path.Combine(work, "journal.json"), new Journal { moves = new List<MoveEntry> { new MoveEntry { path = "game/mods/b.jar", original = true } } });
        Updater.Recover(root);
        Assert(File.ReadAllText(Path.Combine(root, "game/mods/b.jar")) == "MOD2" && !Directory.Exists(work), "interrupted commit is recovered on next run");
        var malicious = Release(web, "bad", Entry("game/saves/world/level.dat", "NEW", "managed")); caught = false;
        try { await updater.Update(malicious, root, progress, CancellationToken.None); } catch (InvalidDataException) { caught = true; }
        Assert(caught, "manifest cannot overwrite saved worlds");
        var settingsOverwrite = Release(web, "bad", Entry("game/options.txt", "NEW", "managed")); caught = false;
        try { await updater.Update(settingsOverwrite, root, progress, CancellationToken.None); } catch (InvalidDataException) { caught = true; }
        Assert(caught, "manifest cannot manage player options");
        var configRelease = Release(web, "config", Entry("game/config/IC2.cfg", "NEW", "managed"));
        Assert(await updater.Update(configRelease, root, progress, CancellationToken.None) == 1, "required mod configuration is managed for server compatibility");
        web.Data["launcher/manifests/" + failed.manifest + ".json"] = Bytes("{}"); caught = false;
        try { await updater.Update(failed, root, progress, CancellationToken.None); } catch (InvalidDataException) { caught = true; }
        Assert(caught, "manifest hash mismatch is rejected");
        var launch = Game.StartInfo("industrial", Path.Combine(root, "folder with spaces"), "Test_123", 3072);
        Assert(launch.Arguments.Contains("-Xmx3072M") && launch.Arguments.Contains("minecraft-forge.jar") && launch.Arguments.Contains("Test_123"), "industrial launch respects memory, classpath and nickname");
        launch = Game.StartInfo("beta", root, "Player", 1024);
        Assert(launch.FileName.Contains("runtime-x64") && launch.Arguments.Contains("beta_OptiFine") && launch.EnvironmentVariables["APPDATA"] == Path.Combine(root, "data"), "beta launch uses its own Java and isolated data");
        Assert(Throws(() => Game.StartInfo("beta", root, "bad nick", 1024)), "invalid nickname rejected");
        Assert(Game.Quote(@"C:\folder with spaces\") == "\"C:\\folder with spaces\\\\\"", "Windows quoting handles trailing slash");
        string migrationSource = Path.Combine(root, "migration-source"), migrationTarget = Path.Combine(root, "migration-target");
        Directory.CreateDirectory(Path.Combine(migrationSource, "beta/data/.minecraft/saves/world"));
        File.WriteAllText(Path.Combine(migrationSource, "beta/data/.minecraft/saves/world/level.dat"), "SAVE");
        File.WriteAllText(Path.Combine(migrationSource, "beta/data/.minecraft/options.txt"), "KEYBINDS");
        ClientFolders.Copy(migrationSource, migrationTarget);
        Assert(File.ReadAllText(Path.Combine(migrationTarget, "beta/data/.minecraft/saves/world/level.dat")) == "SAVE", "folder change copies worlds");
        Assert(File.ReadAllText(Path.Combine(migrationTarget, "beta/data/.minecraft/options.txt")) == "KEYBINDS", "folder change copies personal settings");
        Assert(File.Exists(Path.Combine(migrationSource, "beta/data/.minecraft/options.txt")), "folder change retains original client");
        Assert(Throws(() => ClientFolders.Copy(migrationSource, migrationTarget)), "migration refuses existing target client");
        Assert(Throws(() => ClientFolders.Copy(migrationSource, Path.Combine(migrationSource, "nested"))), "migration refuses nested destination");
        var normalExit = new GameExitState();
        Assert(normalExit.Expected(0, false), "exit zero is normal");
        Assert(!normalExit.Expected(1, false), "unknown exit one remains an error");
        normalExit.Observe("Someone is closing me!");
        Assert(normalExit.Expected(1, false), "legacy Minecraft window-close exit one is normal");
        Assert(!normalExit.Expected(2, false), "window-close marker does not hide other exit codes");
        Assert(!normalExit.Expected(1, true), "new crash report prevents treating exit as normal");
        normalExit.Observe("---- Minecraft Crash Report ----");
        Assert(!normalExit.Expected(1, false), "crash output takes priority over window-close marker");
        Assert(!normalExit.Expected(0, false), "crash output is retained even if exit code is zero");
        var forgeExit = new GameExitState();
        forgeExit.Observe("2026-10-15 15:00:00 [INFO] [STDERR] Someone is closing me!");
        Assert(forgeExit.Expected(1, false), "Forge-prefixed window-close marker is recognized");
        string publicKey = SigningKey.ToXmlString(false);
        var trustedCatalog = TestCatalog(new string('a', 64), "trusted");
        byte[] envelope = Signed(trustedCatalog, SigningKey);
        Assert(UpdateTrust.Verify(envelope, publicKey, true).sequence == trustedCatalog.sequence, "valid signed catalog is accepted");
        var altered = Json.Parse<CatalogEnvelope>(envelope);
        altered.payload = Convert.ToBase64String(Bytes(Json.Serializer.Serialize(TestCatalog(new string('b', 64), "attacker"))));
        Assert(Throws(() => UpdateTrust.Verify(Bytes(Json.Serializer.Serialize(altered)), publicKey, true)), "catalog payload tampering is rejected");
        using (var otherKey = CreateKey()) Assert(Throws(() => UpdateTrust.Verify(Signed(trustedCatalog, otherKey), publicKey, true)), "catalog signed by another key is rejected");
        Assert(Throws(() => UpdateTrust.Verify(Bytes(Json.Serializer.Serialize(trustedCatalog)), publicKey, true)), "unsigned legacy catalog is rejected");
        var expired = TestCatalog(new string('a', 64), "expired"); expired.issuedAt = UpdateTrust.UnixTime() - 100; expired.expiresAt = UpdateTrust.UnixTime() - 1;
        Assert(Throws(() => UpdateTrust.Verify(Signed(expired, SigningKey), publicKey, true)), "expired signed catalog is rejected");
        string cache = Path.Combine(root, "trusted-catalog.json"); UpdateTrust.Remember(envelope, cache);
        var newer = TestCatalog(new string('a', 64), "newer"); byte[] newerBytes = Signed(newer, SigningKey);
        UpdateTrust.Authenticate(newerBytes, publicKey, cache); UpdateTrust.Remember(newerBytes, cache);
        Assert(Throws(() => UpdateTrust.Authenticate(envelope, publicKey, cache)), "replay of previously signed older catalog is rejected");
        var sameSequence = TestCatalog(new string('c', 64), "different"); sameSequence.sequence = newer.sequence;
        Assert(Throws(() => UpdateTrust.Authenticate(Signed(sameSequence, SigningKey), publicKey, cache)), "different catalog cannot reuse a cached sequence");
        Assert(UpdateTrust.Authenticate(newerBytes, publicKey, cache).sequence == newer.sequence, "unchanged signed catalog remains valid");
        bool unauthorized = false;
        try { await updater.Update(new ServerInfo { id = "industrial", manifest = new string('a',64) }, root, progress, CancellationToken.None); } catch (InvalidDataException) { unauthorized = true; }
        Assert(unauthorized, "update cannot bypass catalog authentication");
    }
}
