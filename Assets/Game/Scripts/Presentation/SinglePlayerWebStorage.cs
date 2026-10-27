using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.Presentation
{
    // Browser saves live in IndexedDB, not the 1 MB WebGL PlayerPrefs store.
    // Acknowledgement is sent only when the IndexedDB transaction commits.
    public sealed class SinglePlayerWebStorage : MonoBehaviour, ISinglePlayerSaveStorage
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SE_SaveRead(string receiver, int requestId, string key);
        [DllImport("__Internal")]
        private static extern void SE_SaveWrite(string receiver, int requestId, string key, string value);
#endif
        [Serializable]
        private sealed class Response
        {
            public int id;
            public string value;
            public string error;
        }
        private int _nextId;
        private readonly Dictionary<int, TaskCompletionSource<string>> _pending =
            new Dictionary<int, TaskCompletionSource<string>>();
        public Task<string> ReadAsync(string key) => Request(key, null);
        public async Task WriteAsync(string key, string value) { await Request(key, value); }
        private async Task<string> Request(string key, string value)
        {
            int id = ++_nextId;
            var completion = new TaskCompletionSource<string>();
            _pending.Add(id, completion);
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (value == null) SE_SaveRead(gameObject.name, id, key);
                else SE_SaveWrite(gameObject.name, id, key, value);
#else
                completion.TrySetException(new PlatformNotSupportedException());
#endif
                if (await Task.WhenAny(completion.Task, Task.Delay(15000)) != completion.Task)
                    throw new IOException("브라우저 저장 응답이 없습니다. 저장 공간 권한을 확인하세요.");
                return await completion.Task;
            }
            finally { _pending.Remove(id); }
        }
        // Called by the WebGL plug-in through SendMessage. Preserve for IL2CPP.
        [UnityEngine.Scripting.Preserve]
        public void OnStorageResponse(string json)
        {
            Response response = JsonUtility.FromJson<Response>(json);
            if (response == null || !_pending.TryGetValue(response.id, out var completion)) return;
            if (!string.IsNullOrEmpty(response.error))
                completion.TrySetException(new IOException(response.error));
            else completion.TrySetResult(response.value);
        }
        private void OnDestroy()
        {
            var pendingRequests = new List<TaskCompletionSource<string>>(_pending.Values);
            _pending.Clear();
            // Completing a task can synchronously run Request's finally block.
            foreach (var pending in pendingRequests)
                pending.TrySetException(new IOException("저장 연결이 종료되었습니다."));
        }
    }
}
