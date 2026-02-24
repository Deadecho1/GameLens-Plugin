using System;
using System.Threading.Tasks;
using SocketIOClient;
using SocketIO.Core;
using SocketIOClient.Transport;

namespace GameLensAnalytics.Runtime
{
    public sealed class SocketCaptureClient : IDisposable
    {
        private SocketIOClient.SocketIO _io = null;

        public bool IsConnected => _io != null && _io.Connected;

        // Events
        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<string> ResponseReceived;
        public event Action<string> ErrorReceived;

        public async Task ConnectAsync(string endpointBase)
        {
            _io ??= new SocketIOClient.SocketIO(endpointBase, new SocketIOOptions
            {
                EIO = EngineIO.V4,
                Transport = TransportProtocol.WebSocket,
                AutoUpgrade = false,      
                Reconnection = true
            });

            _io.OnConnected += (_, __) => Connected?.Invoke();
            _io.OnDisconnected += (_, reason) => Disconnected?.Invoke(reason);

            _io.On("response", resp => ResponseReceived?.Invoke(resp?.ToString() ?? "<null>"));
            _io.On("error", resp => ErrorReceived?.Invoke(resp?.ToString() ?? "<null>"));

            await _io.ConnectAsync();
        }

        public async Task EmitCaptureEventAsync(object payload)
        {
            if (_io == null || !_io.Connected)
                return;

            await _io.EmitAsync("capture_event", payload);
        }

        public void Dispose()
        {
            try { _io?.DisconnectAsync().GetAwaiter().GetResult(); }
            catch { }

            _io?.Dispose();
            _io = null;
        }
    }
}