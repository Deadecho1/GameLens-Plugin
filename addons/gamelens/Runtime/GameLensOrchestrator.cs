using Godot;
using System;
using System.Collections.Generic;
using System.IO;


namespace GameLensAnalytics.Runtime
{
    // -------------------------
    // Snap reason enum
    // -------------------------
    public enum SnapReason
    {
        AutoTimer,
        Manual,
    }
    
    /// GameLens main runtime entry point. This singleton gets loaded immediately by the GameLensApi autoload on game start.
    /// Owns config + state, exposes a small public API, and coordinates capture workers.
    public partial class GameLensOrchestrator : Node
    {
        // -------------------------
        // Singleton access
        // -------------------------
        public static GameLensOrchestrator Instance { get; private set; }

        // -------------------------
        // Public API surface 
        // -------------------------
        [Export] public bool Enabled { get; private set; } = true;

        public void Enable() => Enabled = true;
        public void Disable() => Enabled = false;

        // -------------------------
        // Managed instances
        // -------------------------
        private GameLensCapturer _capturer;
        private LocalCaptureStore _store;
        private UploadQueueWorker _uploader;

        // -------------------------
        // Networking
        // -------------------------
        private BackendNetworking _net;

        public string SessionId => _net?.SessionId;
        public bool BackendReady => _net?.BackendReady ?? false; // flag for upload worker to know when it can start uploading (after session is created)

        // -------------------------
        // Sub viewport related
        // -------------------------
        private SubViewport _capVp;
        private TextureRect _capRect;

        // -------------------------
        // Coalesced snap state (max 1 snap per frame)
        // -------------------------
        private bool _snapPending = false;
        private bool _snapFinalized = false;
        private ulong _snapPendingFrame = 0;

        private Godot.Collections.Array<SnapReason> _pendingReasons = new();
        private double _pendingUtcUnixSeconds = 0.0;

        // -------------------------
        // Events (for debugging)
        // -------------------------
        [Signal] public delegate void SnapQueuedEventHandler();
        [Signal] public delegate void SnapCapturedEventHandler(int snapId);
        [Signal] public delegate void SnapDroppedEventHandler(string why);


        public override void _EnterTree()
        {
            if (Instance != null && Instance != this)
            {
                QueueFree();
                return;
            }
            Instance = this;
            Name = "Orchestrator";
            ProcessMode = ProcessModeEnum.Always;
        }

        public override void _ExitTree()
        {
            if (Instance == this) Instance = null;
            
            _net?.QueueFree();
            _store?.Dispose();
            _uploader?.Dispose();
        }
        
        public override void _Ready()
        {
            _capturer = new GameLensCapturer();
            AddChild(_capturer);

            // Globalize user:// once on main thread
            var rootGlobal = ProjectSettings.GlobalizePath("user://gamelens");
            Directory.CreateDirectory(rootGlobal);

            _uploader = new UploadQueueWorker();
            _uploader.InitAndScan(rootGlobal);

            _store = new LocalCaptureStore(rootGlobal);

            // When storage finishes writing a capture -> enqueue for upload
            _store.CaptureSaved += (sessionId, imgPath, jsonPath, metaPath, captureId) =>
            {
                _uploader.Enqueue(sessionId, imgPath, jsonPath, metaPath, captureId);
            };

            _net = new BackendNetworking
            {
                EndpointBase = GameLensConfig.EndpointBase,
                GameId = GameLensConfig.GameId,
                IsEnabledFunc = () => Enabled
            };
            AddChild(_net);
            _net.Start();
        }

        /// <summary>
        /// If snap is called multiple times in a single frame, it coalesces multiple requests into ONE snap per frame, merging reasons.
        /// </summary>
        public void Snap(SnapReason reason)
        {
            GD.Print($"Snap requested: reason='{reason}'");

            if (!Enabled)
            {
                EmitSignal(SignalName.SnapDropped, "disabled");
                return;
            }

            ulong frame = Engine.GetProcessFrames();

            // Starting a new coalesced snap for this frame?
            if (!_snapPending || frame != _snapPendingFrame)
            {
                _snapPending = true;
                _snapFinalized = false;
                _snapPendingFrame = frame;

                _pendingReasons.Clear();
                _pendingUtcUnixSeconds = Time.GetUnixTimeFromSystem();

                // Allow multiple Snap() calls this frame before finalizing
                CallDeferred(nameof(FinalizePendingSnap));
            }

            // Merge reasons
            _pendingReasons.Add(reason);

            EmitSignal(SignalName.SnapQueued);
        }

        private void FinalizePendingSnap()
        {
            if (!_snapPending || _snapFinalized)
                return;

            _snapFinalized = true;
            var pkt = _capturer.Capture(_pendingReasons, _pendingUtcUnixSeconds);
            _store.Enqueue(SessionId, pkt);
            // reset snap state for next frame
            ConsumeSnap();
        }

        /// <summary>
        /// Drain the single coalesced snap (max 1).
        /// </summary>
        private void ConsumeSnap()
        {
            _snapPending = false;
            _snapFinalized = false;

            _pendingReasons.Clear();
            _pendingUtcUnixSeconds = 0;
        }
    }
}
