namespace Rocket.Chat.Net.Driver
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    using Rocket.Chat.Net.Interfaces;
    using Rocket.Chat.Net.Models;

    public class DdpClient : IDdpClient
    {
        private readonly ILogger _logger;
        private readonly ClientWebSocket _socket;
        private readonly ConcurrentDictionary<string, JObject> _messages = new ConcurrentDictionary<string, JObject>();

        Thread _waitForMessageThread;

        public string Url { get; }
        public string SessionId { get; private set; }
        public bool IsDisposed { get; private set; }

        public event DataReceived DataReceivedRaw;
        public event DdpReconnect DdpReconnect;

        public DdpClient(string baseUrl, bool useSsl, ILogger logger)
        {
            _logger = logger;

            var protocol = useSsl ? "wss" : "ws";
            Url = $"{protocol}://{baseUrl}/websocket";

            _socket = new ClientWebSocket();
            _waitForMessageThread = new Thread(WaitForMessage);
            _waitForMessageThread.Start();
        }

        public DdpClient(ClientWebSocket socket, ILogger logger)
        {
            _logger = logger;

            _socket = socket;
        }

        private async void WaitForMessage()
        {
            var recvBytes = new byte[4096];
            var recvBuffer = new ArraySegment<byte>(recvBytes);

            while (true)
            {
                if (_socket.State != WebSocketState.Open)
                    continue;

                WebSocketReceiveResult recvResult = await _socket.ReceiveAsync(recvBytes, new CancellationToken());
                byte[] msgBytes = recvBuffer.Skip(recvBuffer.Offset).Take(recvResult.Count).ToArray();
                string recvMessage = Encoding.UTF8.GetString(msgBytes);
                SocketOnMessage(recvMessage);
            }
        }

        private void SocketOnOpened(object sender, EventArgs eventArgs)
        {
            _logger.Debug("OPEN");

            _logger.Debug("Sending connection request");
            const string ddpVersion = "1";
            var request = new
            {
                msg = "connect",
                version = ddpVersion,
                session = SessionId, // Although, it doesn't appear that RC handles resuming sessions
                support = new[]
                {
                    ddpVersion
                }
            };

            SendObjectAsync(request, CancellationToken.None).Wait();
        }

        private void SocketOnMessage(string message)
        {
            var json = message;
            var data = JObject.Parse(json);
            _logger.Debug($"RECIEVED: {JsonConvert.SerializeObject(data, Formatting.Indented)}");

            var isRocketMessage = data?["msg"] != null;
            if (isRocketMessage)
            {
                var type = data["msg"].ToObject<string>();
                InternalHandle(type, data);
                OnDataReceived(type, data);
            }
        }

        private void InternalHandle(string type, JObject data)
        {
            if (data["id"] != null)
            {
                var id = data["id"].ToObject<string>();
                _messages.TryAdd(id, data);
            }

            switch (type)
            {
                case "ping": // Required by spec
                    PongAsync(data).Wait();
                    break;
                case "connected":

                    if (SessionId != null)
                    {
                        OnDdpReconnect();
                    }
                    SessionId = data["session"].ToObject<string>();

                    _logger.Debug($"Connected via session {SessionId}.");
                    break;
                case "ready":
                    var subs = data["subs"];
                    var ids = subs?.ToObject<List<string>>();
                    var id = ids?.FirstOrDefault(); // TODO Handle collection?
                    if (id != null)
                    {
                        _messages.TryAdd(id, data);
                    }
                    break;
            }
        }

        public async Task PingAsync(CancellationToken token)
        {
            var id = CreateId();
            var request = new
            {
                msg = "ping",
                id
            };

            await SendObjectAsync(request, token).ConfigureAwait(false);
            await WaitForIdOrReadyAsync(id, token).ConfigureAwait(false);
        }

        private async Task PongAsync(JObject data)
        {
            var request = new
            {
                msg = "pong",
                id = data["id"]?.ToObject<string>()
            };

            await SendObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task ConnectAsync(CancellationToken token)
        {
            await _socket.ConnectAsync(new Uri(Url), token);
            SocketOnOpened(this, null);

            await WaitForConnectAsync(token).ConfigureAwait(false);
        }

        public async Task<string> SubscribeAsync(string name, CancellationToken token, params object[] args)
        {
            var id = CreateId();
            var request = new
            {
                msg = "sub",
                @params = args,
                name,
                id
            };

            await SendObjectAsync(request, token).ConfigureAwait(false);
            return id;
        }

        public async Task<string> SubscribeAndWaitAsync(string name, CancellationToken token, params object[] args)
        {
            var id = CreateId();
            var request = new
            {
                msg = "sub",
                @params = args,
                name,
                id
            };

            await SendObjectAsync(request, token).ConfigureAwait(false);
            await WaitForIdOrReadyAsync(id, token).ConfigureAwait(false);
            return id;
        }

        public async Task UnsubscribeAsync(string id, CancellationToken token)
        {
            var request = new
            {
                msg = "unsub",
                id
            };

            await SendObjectAsync(request, token).ConfigureAwait(false);
            await WaitForIdOrReadyAsync(id, token).ConfigureAwait(false);
        }

        public async Task<JObject> CallAsync(string method, CancellationToken token, params object[] args)
        {
            var id = CreateId();
            var request = new
            {
                msg = "method",
                @params = args,
                method,
                id
            };

            await SendObjectAsync(request, token).ConfigureAwait(false);
            var result = await WaitForIdOrReadyAsync(id, token).ConfigureAwait(false);

            return result;
        }

        private void OnDataReceived(string type, JObject data)
        {
            DataReceivedRaw?.Invoke(type, data);
        }

        public void Dispose()
        {
            IsDisposed = true;
            SessionId = null;
            _socket.CloseAsync(WebSocketCloseStatus.Empty, "", new CancellationToken());
        }

        private async Task SendObjectAsync(object payload, CancellationToken token)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            _logger.Debug($"SEND: {json}");
            byte[] sendbytes = Encoding.UTF8.GetBytes(json);
            var sendbuffer = new ArraySegment<byte>(sendbytes);
            await _socket.SendAsync(sendbuffer, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        private async Task<JObject> WaitForIdOrReadyAsync(string id, CancellationToken token)
        {
            JObject data;
            while (!_messages.TryRemove(id, out data))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            return data;
        }

        private async Task WaitForConnectAsync(CancellationToken token)
        {
            while (SessionId == null)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }

        private static string CreateId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void OnDdpReconnect()
        {
            DdpReconnect?.Invoke();
        }
    }
}