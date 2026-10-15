using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FZO {
    public class CatalogEnvelope { public int schema; public string algorithm, payload, signature; }
    public static class UpdateTrust {
        internal static readonly object CacheGate = new object();
        public static long UnixTime() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
        public static string PublicKey() {
            using (var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("update-public-key.xml")) {
                if (stream == null) throw new InvalidDataException("В лаунчере отсутствует ключ проверки обновлений.");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        public static Catalog Verify(byte[] envelopeBytes, string publicKey, bool checkTime) {
            var envelope = Json.Parse<CatalogEnvelope>(envelopeBytes);
            if (envelope == null || envelope.schema != 1 || envelope.algorithm != "RSA-SHA256" || envelope.payload == null || envelope.signature == null)
                throw new InvalidDataException("Каталог обновлений не подписан или имеет неизвестный формат.");
            byte[] payload, signature;
            try { payload = Convert.FromBase64String(envelope.payload); signature = Convert.FromBase64String(envelope.signature); }
            catch (FormatException) { throw new InvalidDataException("Повреждена подпись каталога обновлений."); }
            if (payload.Length > 65536 || signature.Length != 384) throw new InvalidDataException("Некорректный размер подписанного каталога.");
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (rsa.KeySize != 3072 || !rsa.PublicOnly || !rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidDataException("Подпись обновления недействительна. Загрузка остановлена.");
            }
            var catalog = Json.Parse<Catalog>(payload);
            if (catalog == null || catalog.sequence < 1 || catalog.issuedAt < 1 || catalog.expiresAt <= catalog.issuedAt
                || catalog.expiresAt - catalog.issuedAt > 180L * 86400)
                throw new InvalidDataException("Некорректные сроки действия каталога.");
            if (checkTime && (catalog.issuedAt > UnixTime() + 600 || catalog.expiresAt <= UnixTime()))
                throw new InvalidDataException("Срок действия каталога истёк или неверно время компьютера. Обратитесь к администратору FZO.");
            return catalog;
        }
        public static Catalog Authenticate(byte[] bytes, string publicKey, string cachePath) {
            var catalog = Verify(bytes, publicKey, true);
            if (cachePath != null) {
                Updater.CheckNoLinks(cachePath);
                if (File.Exists(cachePath)) {
                    byte[] previousBytes = File.ReadAllBytes(cachePath);
                    if (previousBytes.Length > 131072) throw new InvalidDataException("Повреждён локальный каталог доверия.");
                    var previous = Verify(previousBytes, publicKey, false);
                    if (catalog.sequence < previous.sequence || (catalog.sequence == previous.sequence && Updater.Hash(bytes) != Updater.Hash(previousBytes)))
                        throw new InvalidDataException("Сайт предлагает устаревший каталог. Откат обновлений заблокирован.");
                }
            }
            return catalog;
        }
        public static void Remember(byte[] bytes, string cachePath) {
            if (cachePath == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)); Updater.CheckNoLinks(cachePath);
            string temp = cachePath + ".tmp"; Updater.CheckNoLinks(temp);
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(cachePath)) File.Replace(temp, cachePath, null); else File.Move(temp, cachePath);
        }
    }
}
