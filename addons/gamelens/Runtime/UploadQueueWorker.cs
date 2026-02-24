using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace GameLensAnalytics.Runtime
{
    public sealed class UploadQueueWorker : IDisposable
    {
        public sealed class UploadItem
        {
            public string SessionId;
            public string CaptureId;
            public string ImagePath;
            public string JsonPath;
            public string MetaPath;
        }

        private sealed class CaptureMeta
        {
            public string session_id { get; set; }
            public string capture_id { get; set; }
            public double captured_utc_unix { get; set; }
        }

        private readonly BlockingCollection<UploadItem> _queue = new(new ConcurrentQueue<UploadItem>());
        private readonly Thread _thread;
        private volatile bool _running = true;

        // Dedup so startup scan + live CaptureSaved don't double enqueue
        private readonly HashSet<string> _seenCaptureIds = new();
        private readonly object _seenLock = new();

        private string _rootDirGlobal;

        public UploadQueueWorker()
        {
            _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "GameLens.Uploader" };
            _thread.Start();
        }

        /// Call this once on startup so the worker can scan and enqueue pending files.
        public void InitAndScan(string rootDirGlobal)
        {
            _rootDirGlobal = rootDirGlobal;
            ScanAndEnqueuePendingFromDisk();
        }

        public void Enqueue(string sessionId, string imagePath, string jsonPath, string metaPath, string captureId)
        {
            if (!_running) return;
            if (string.IsNullOrWhiteSpace(captureId)) return;

            lock (_seenLock)
            {
                if (_seenCaptureIds.Contains(captureId))
                    return;
                _seenCaptureIds.Add(captureId);
            }

            _queue.Add(new UploadItem
            {
                SessionId = sessionId,
                CaptureId = captureId,
                ImagePath = imagePath,
                JsonPath = jsonPath,
                MetaPath = metaPath
            });
        }

        private void ScanAndEnqueuePendingFromDisk(int maxToEnqueue = 20000)
        {
            try
            {
                var capturesRoot = Path.Combine(_rootDirGlobal, "captures");
                if (!Directory.Exists(capturesRoot))
                    return;

                int enqueued = 0;

                foreach (var metaPath in Directory.EnumerateFiles(capturesRoot, "*.meta.json", SearchOption.AllDirectories))
                {
                    if (enqueued >= maxToEnqueue) break;

                    CaptureMeta meta;
                    try
                    {
                        meta = JsonSerializer.Deserialize<CaptureMeta>(File.ReadAllText(metaPath));
                    }
                    catch { continue; }

                    if (meta == null || string.IsNullOrEmpty(meta.session_id) || string.IsNullOrEmpty(meta.capture_id))
                        continue;

                    var basePath = metaPath.Substring(0, metaPath.Length - ".meta.json".Length);
                    var jsonPath = basePath + ".json";

                    string imgPath = basePath + ".png";
                    if (!File.Exists(imgPath))
                    {
                        var dir = Path.GetDirectoryName(basePath);
                        var stem = Path.GetFileName(basePath);

                        // find any file like "<stem>.*" that isn't json/meta
                        string found = null;
                        foreach (var f in Directory.EnumerateFiles(dir, stem + ".*", SearchOption.TopDirectoryOnly))
                        {
                            if (f.EndsWith(".json") || f.EndsWith(".meta.json"))
                                continue;
                            found = f;
                            break;
                        }
                        imgPath = found;
                    }

                    if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath) || !File.Exists(jsonPath))
                        continue;

                    Enqueue(meta.session_id, imgPath, jsonPath, metaPath, meta.capture_id);
                    enqueued++;
                }

                GD.Print($"[GameLens] Upload scan queued {enqueued} pending captures.");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[GameLens] Upload scan error: {e.Message}");
            }
        }

        private void WorkerLoop()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (!_running) break;

                try
                {
                    // TODO:
                    // - wait for BackendReady
                    // - socket emit with session_id
                    // - on ACK: delete files
                    //
                    // For now: just pretend success and delete (to verify scan/enqueue pipeline)
                    // Remove this once real upload is implemented.
                    DeleteAfterSuccess(item);

                }
                catch { }
            }
        }

        public static void DeleteAfterSuccess(UploadItem item)
        {
            TryDelete(item.ImagePath);
            TryDelete(item.JsonPath);
            TryDelete(item.MetaPath);
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            _running = false;
            _queue.CompleteAdding();
            try { _thread.Join(500); } catch { }
            _queue.Dispose();
        }
    }
}