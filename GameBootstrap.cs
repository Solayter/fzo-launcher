using System;
using System.IO;

namespace FZO {
    public static class GameBootstrap {
        public static string Prepare(string folder) {
            byte[] bytes;
            using (var stream = typeof(GameBootstrap).Assembly.GetManifestResourceStream("game-bootstrap.jar")) {
                if (stream == null) throw new InvalidDataException("В лаунчере отсутствует модуль значка игры.");
                using (var buffer = new MemoryStream()) { stream.CopyTo(buffer); bytes = buffer.ToArray(); }
            }
            string hash = Updater.Hash(bytes);
            string directory = Path.Combine(folder, "bootstrap");
            Updater.CheckNoLinks(directory); Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, hash + ".jar"); Updater.CheckNoLinks(target);
            if (File.Exists(target) && Updater.Hash(target) == hash) return target;
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                return target;
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
