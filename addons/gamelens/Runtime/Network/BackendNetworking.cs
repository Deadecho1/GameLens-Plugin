using Godot;
using System;
using System.Threading.Tasks;

namespace GameLensAnalytics.Runtime
{
    /// <summary>
    /// Owns backend connectivity: Socket.IO connection + session creation + retry loop.
    /// Orchestrator calls Start()/Stop() and reads SessionId/BackendReady.
    /// </summary>
    public partial class BackendNetworking : Node
    {
        public string SessionId { get; private set; } = null;
        public bool BackendReady => _backendReady;

        public SocketCaptureClient Socket => _socket;
        
        private volatile bool _backendReady = false;

        private readonly SocketCaptureClient _socket = new();
        private readonly SessionService _sessionService = new();

        private bool _loopRunning = false;
        private int _connectAttempt = 0;

        // Config (set by orchestrator)
        public string EndpointBase { get; set; } = "";
        public string GameId { get; set; } = "";

        /// <summary>
        /// Delegate used to query whether plugin is enabled (owned by orchestrator).
        /// </summary>
        public Func<bool> IsEnabledFunc { get; set; }

        public override void _Ready()
        {
            SetupSocketHandlers();
        }

        public override void _ExitTree()
        {
            try { _socket?.Dispose(); } catch { }
        }

        private void SetupSocketHandlers()
        {
            _socket.Connected += () =>
            {
                GD.Print("[GameLens] Socket connected.");
                _connectAttempt = 0;
            };

            _socket.Disconnected += reason =>
            {
                GD.Print($"[GameLens] Socket disconnected: {reason}");
                _backendReady = false;
            };

            _socket.ResponseReceived += msg =>
            {
                GD.Print($"[GameLens] socket response: {msg}");
            };

            _socket.ErrorReceived += msg =>
            {
                GD.PrintErr($"[GameLens] socket error: {msg}");
            };
        }

        public void Start()
        {
            if (_loopRunning) return;
            _loopRunning = true;
            _ = NetworkingLoopAsync();
        }

        private bool IsEnabled()
        {
            try { return IsEnabledFunc?.Invoke() ?? true; }
            catch { return true; }
        }

        private async Task NetworkingLoopAsync()
        {
            const int baseDelayMs = 500;
            const int maxDelayMs = 10_000;

            while (true)
            {
                if (!IsEnabled())
                {
                    _backendReady = false;
                    await WaitMs(500);
                    continue;
                }

                try
                {
                    // 1) Ensure socket connected
                    if (!_socket.IsConnected)
                    {
                        _connectAttempt++;
                        GD.Print($"[GameLens] Connecting socket... attempt={_connectAttempt}");

                        await _socket.ConnectAsync(EndpointBase);
                    }

                    // 2) Ensure session exists
                    if (string.IsNullOrEmpty(SessionId))
                    {
                        GD.Print("[GameLens] Creating session...");
                        SessionId = await _sessionService.CreateSessionAsync(EndpointBase, GameId);
                        GD.Print($"[GameLens] SessionId={SessionId}");
                    }

                    // 3) Mark backend ready
                    if (_socket.IsConnected && !string.IsNullOrEmpty(SessionId))
                    {
                        if (!_backendReady)
                            GD.Print("[GameLens] Backend ready. Upload worker enabled.");

                        _connectAttempt = 0;
                        _backendReady = true;
                    }

                    // 4) Stay ready until disconnected or disabled
                    while (IsEnabled() && _socket.IsConnected)
                        await WaitMs(500);

                    _backendReady = false;
                }
                catch (Exception e)
                {
                    _backendReady = false;
                    GD.PrintErr($"[GameLens] Networking loop error: {e.Message}");
                }
                // socket was disconnected but plugin is still enabled: retry with backoff.
                if (IsEnabled())
                {
                    int delay = Math.Min(maxDelayMs, baseDelayMs * (1 << Math.Min(_connectAttempt, 5)));
                    GD.Print($"[GameLens] Retry in {delay}ms...");
                    await WaitMs(delay);
                }
                else
                {
                    await WaitMs(500);
                }
            }
        }

        private async Task WaitMs(int ms)
        {
            var timer = GetTree().CreateTimer(ms / 1000.0);
            await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        }
    }
}