using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Game.Application.Session;
using UnityEngine;

namespace Game.Presentation
{
    public interface ISinglePlayerSaveStorage
    {
        Task<string> ReadAsync(string key);
        Task WriteAsync(string key, string value);
    }

    public sealed class SinglePlayerFileStorage : ISinglePlayerSaveStorage
    {
        private readonly string _directory;
        public SinglePlayerFileStorage(string directory) { _directory = directory; }
        private string PathFor(string key)
        {
            if (key != "manual" && key != "auto" &&
                key != "manual.backup" && key != "auto.backup")
                throw new ArgumentException("잘못된 저장 슬롯입니다.");
            return Path.Combine(_directory, key + ".json");
        }
        public Task<string> ReadAsync(string key)
        {
            string path = PathFor(key);
            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        public Task WriteAsync(string key, string value)
        {
            Directory.CreateDirectory(_directory);
            string path = PathFor(key);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(value);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return Task.CompletedTask;
        }
    }

    public sealed class SinglePlayerSaveStore
    {
        public const int MaximumJsonLength = 16 * 1024 * 1024;
        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public string checksum;
            public string payload;
        }
        private readonly ISinglePlayerSaveStorage _storage;
        public SinglePlayerSaveStore(ISinglePlayerSaveStorage storage) { _storage = storage; }
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
        public static string Encode(SinglePlayerSave save)
        {
            save.Validate();
            string payload = JsonUtility.ToJson(save);
            string json = JsonUtility.ToJson(new Envelope { payload = payload, checksum = Hash(payload) });
            if (json.Length > MaximumJsonLength)
                throw new InvalidOperationException("저장 기록이 너무 큽니다. 기존 저장은 유지됩니다.");
            return json;
        }
        public static SinglePlayerSave Decode(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaximumJsonLength)
                throw new InvalidDataException("저장 파일이 비어 있거나 너무 큽니다.");
            Envelope envelope = JsonUtility.FromJson<Envelope>(json);
            if (envelope == null || envelope.version != 1 || envelope.payload == null ||
                envelope.checksum != Hash(envelope.payload))
                throw new InvalidDataException("저장 파일이 손상되었습니다.");
            var save = JsonUtility.FromJson<SinglePlayerSave>(envelope.payload);
            if (save == null) throw new InvalidDataException("저장을 읽을 수 없습니다.");
            save.Validate();
            return save;
        }

        // Read both copies. A damaged primary never destroys the last good save.
        public async Task<SinglePlayerSave> LoadAsync(string slot)
        {
            Exception primaryError = null;
            try
            {
                string primary = await _storage.ReadAsync(slot);
                if (primary != null) return Decode(primary);
            }
            catch (Exception error) { primaryError = error; }
            string backup = await _storage.ReadAsync(slot + ".backup");
            if (backup != null) return Decode(backup);
            if (primaryError != null) throw primaryError;
            return null;
        }

        public Task SaveAsync(string slot, SinglePlayerSave save) => SaveEncodedAsync(slot, Encode(save));

        public async Task SaveEncodedAsync(string slot, string next)
        {
            // Freeze the payload before the first await while the live game may
            // continue appending records. Never hold a live list across an await.
            Decode(next);
            SinglePlayerSave previous = null;
            try { previous = await LoadAsync(slot); }
            catch (InvalidDataException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (previous != null)
                await _storage.WriteAsync(slot + ".backup", Encode(previous));
            await _storage.WriteAsync(slot, next);
        }
    }

}
