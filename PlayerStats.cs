using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FZO {
    // A junction lets Minecraft write to durable storage even if the launcher exits.
    public static class PlayerStats {
        const uint MountPoint = 0xA0000003, GetReparse = 0x000900A8, SetReparse = 0x000900A4;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        public static string Relative(string id) {
            if (id == "industrial") return @"game\stats";
            if (id == "beta") return @"data\.minecraft\stats";
            throw new ArgumentException("Неизвестный сервер.");
        }
        public static string Storage(string folder, string id) {
            Relative(id);
            return Path.Combine(Path.GetFullPath(folder), "player-data", id, "stats");
        }
        static SafeFileHandle Handle(string path, bool write) {
            var handle = CreateFile(path, write ? 0x40000000u : 0u, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return handle;
        }
        static string JunctionTarget(string path) {
            byte[] data = new byte[16384]; int returned;
            using (var handle = Handle(path, false)) {
                if (!DeviceIoControl(handle, GetReparse, null, 0, data, data.Length, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (returned < 16 || BitConverter.ToUInt32(data, 0) != MountPoint) throw new IOException("Неизвестная ссылка статистики.");
            int offset = BitConverter.ToUInt16(data, 8), length = BitConverter.ToUInt16(data, 10);
            if (16 + offset + length > returned) throw new IOException("Повреждённая ссылка статистики.");
            string target = Encoding.Unicode.GetString(data, 16 + offset, length);
            if (!target.StartsWith(@"\??\", StringComparison.Ordinal)) throw new IOException("Неизвестный путь статистики.");
            return target.Substring(4);
        }
        public static bool IsLinked(string path, string folder, string id) {
            return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                && String.Equals(JunctionTarget(path).TrimEnd('\\'), Storage(folder, id).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        static void Link(string path, string target) {
            if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Для статистики требуется локальная папка Windows.");
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target), display = Encoding.Unicode.GetBytes(target);
            byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
            Array.Copy(BitConverter.GetBytes(MountPoint), 0, data, 0, 4);
            Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
            Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
            Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
            Array.Copy(BitConverter.GetBytes((ushort)display.Length), 0, data, 14, 2);
            Array.Copy(substitute, 0, data, 16, substitute.Length);
            Array.Copy(display, 0, data, 18 + substitute.Length, display.Length);
            Directory.CreateDirectory(path);
            try {
                int returned;
                using (var handle = Handle(path, true)) {
                    if (!DeviceIoControl(handle, SetReparse, data, data.Length, null, 0, out returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            } catch { Directory.Delete(path, false); throw; }
        }
        static void Import(string source, string target) {
            Updater.CheckNoLinks(source); Updater.CheckNoLinks(target);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) {
                Updater.CheckNoLinks(file);
                string destination = Path.Combine(target, Path.GetFileName(file)); Updater.CheckNoLinks(destination);
                // Durable data wins over a stale client copy. The original remains in its backup.
                if (File.Exists(destination)) continue;
                string temporary = Path.Combine(target, ".import-" + Guid.NewGuid().ToString("N"));
                try {
                    File.Copy(file, temporary);
                    if (Updater.Hash(file) != Updater.Hash(temporary)) throw new IOException("Ошибка переноса статистики.");
                    File.Move(temporary, destination);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (string directory in Directory.GetDirectories(source)) Import(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        public static FileStream Open(string folder, string id, string root) {
            string storage = Storage(folder, id), path = Path.Combine(Path.GetFullPath(root), Relative(id));
            Updater.CheckNoLinks(storage); Directory.CreateDirectory(storage);
            string lockPath = Path.Combine(Path.GetDirectoryName(storage), ".stats-lock"); Updater.CheckNoLinks(lockPath);
            var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try {
                Updater.CheckNoLinks(Path.GetDirectoryName(path));
                if (IsLinked(path, folder, id)) return lease;
                Updater.CheckNoLinks(path);
                string backup = null;
                if (Directory.Exists(path)) {
                    Import(path, storage);
                    backup = path + ".before-fzo-" + Guid.NewGuid().ToString("N");
                    Directory.Move(path, backup);
                }
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    Link(path, storage);
                    if (!IsLinked(path, folder, id)) throw new IOException("Не удалось подключить постоянную статистику.");
                } catch {
                    if (backup != null && !Directory.Exists(path) && !File.Exists(path)) Directory.Move(backup, path);
                    throw;
                }
                return lease;
            } catch { lease.Dispose(); throw; }
        }
    }
}
