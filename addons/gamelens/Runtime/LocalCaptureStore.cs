using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace GameLensAnalytics.Runtime
{
    public sealed class LocalCaptureStore : IDisposable
    {
        public int MaxFilesOnDiskPerSessionDay { get; set; } = 5000;

        // Fired AFTER files are written successfully (runs on the store worker thread)
        // (sessionId, imgPath, jsonPath, metaPath, captureId)
        public event Action<string, string, string, string, string> CaptureSaved;

        private readonly string _rootDirGlobal;
        private readonly BlockingCollection<EnqueueItem> _queue = new(new ConcurrentQueue<EnqueueItem>());
        private readonly Thread _thread;
        private volatile bool _running = true;

        private const string CapturesFolderName = "captures";

        private sealed class EnqueueItem
        {
            public string SessionId;
            public CapturePacket Packet;
        }

        private sealed class CaptureMeta
        {
            public string session_id { get; set; }
            public string capture_id { get; set; }
            public double captured_utc_unix { get; set; }
        }

        public LocalCaptureStore(string rootDirGlobal)
        {
            _rootDirGlobal = rootDirGlobal;
            Directory.CreateDirectory(_rootDirGlobal);

            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "GameLens.LocalCaptureStore"
            };
            _thread.Start();
        }

        /// <summary>
        /// Enqueue a packet to be persisted under a specific session_id.
        /// </summary>
        public void Enqueue(string sessionId, CapturePacket packet)
        {
            if (!_running) return;
            if (string.IsNullOrWhiteSpace(sessionId)) return; 

            _queue.Add(new EnqueueItem
            {
                SessionId = sessionId,
                Packet = packet
            });
        }

        private void WorkerLoop()
        {
            foreach (EnqueueItem item in _queue.GetConsumingEnumerable())
            {
                if (!_running) break;

                try
                {
                    var pkt = item.Packet;
                    var sessionId = item.SessionId;

                    var sessionDir = Path.Combine(_rootDirGlobal, CapturesFolderName, sessionId);
                    var dayDir = Path.Combine(sessionDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
                    Directory.CreateDirectory(dayDir);

                    var baseName = $"{pkt.UtcUnixSeconds:0.000}_{pkt.CaptureId}";
                    var basePath = Path.Combine(dayDir, baseName);

                    var imgPath = basePath + pkt.ImageExt;
                    var jsonPath = basePath + ".json";
                    var metaPath = basePath + ".meta.json";

                    File.WriteAllBytes(imgPath, pkt.ImageBytes);
                    File.WriteAllText(jsonPath, pkt.PayloadJson);

                    var meta = new CaptureMeta
                    {
                        session_id = sessionId,
                        capture_id = pkt.CaptureId,
                        captured_utc_unix = pkt.UtcUnixSeconds,
                    };

                    File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));

                    EnforceRetention(dayDir);

                    try { CaptureSaved?.Invoke(sessionId, imgPath, jsonPath, metaPath, pkt.CaptureId); } catch { }
                }
                catch
                {
                    // swallow or log somewhere
                }
            }
        }

        private void EnforceRetention(string dayDir)
        {
            try
            {
                // Count by meta files (1 per capture)
                var metaFiles = Directory.GetFiles(dayDir, "*.meta.json");
                if (metaFiles.Length <= MaxFilesOnDiskPerSessionDay) return;

                Array.Sort(metaFiles, StringComparer.Ordinal);
                int toDelete = metaFiles.Length - MaxFilesOnDiskPerSessionDay;

                for (int i = 0; i < toDelete; i++)
                {
                    var metaPath = metaFiles[i];
                    var basePath = metaPath.Substring(0, metaPath.Length - ".meta.json".Length);

                    TryDelete(basePath + ".png");
                    TryDelete(basePath + ".json");
                    TryDelete(metaPath);
                    TryDelete(basePath + ".uploaded");
                }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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