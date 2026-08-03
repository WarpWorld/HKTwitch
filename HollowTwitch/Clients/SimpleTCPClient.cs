using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConnectorLib.JSON;
using HollowTwitch;
using HollowTwitch.Extensions;
using Newtonsoft.Json;

namespace CrowdControl
{
    public class SimpleTCPClient : IDisposable
    {
        private TcpClient _client;
        private readonly SemaphoreSlim _client_lock = new SemaphoreSlim(1);
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _error = new ManualResetEventSlim(false);

        private static readonly JsonSerializerSettings JSON_SETTINGS = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public bool Connected { get; private set; }

        private readonly CancellationTokenSource _quitting = new CancellationTokenSource();

        public SimpleTCPClient()
        {
            Task.Factory.StartNew(ConnectLoop, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(Listen, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(KeepAlive, TaskCreationOptions.LongRunning);
        }

        ~SimpleTCPClient() => Dispose(false);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            _quitting.Cancel();
            if (disposing)
            {
                try { _client?.Close(); }
                catch { /**/ }
            }
        }

        private async void ConnectLoop()
        {
            while (!_quitting.IsCancellationRequested)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync("127.0.0.1", 58430);
                    if (!_client.Connected) { continue; }
                    Connected = true;
                    try { OnConnected?.Invoke(); }
                    catch (Exception e) { Logger.LogError(e); }
                    _ready.Set();
                    await _error.WaitHandle.WaitOneAsync(_quitting.Token);
                }
                catch (Exception e) { Logger.LogError(e); }
                finally
                {
                    Connected = false;
                    _error.Reset();
                    _ready.Reset();
                    try { _client.Close(); }
                    catch { /**/ }
                    if (!_quitting.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1)); }
                }
            }
            Connected = false;
        }

        private async void Listen()
        {
            List<byte> mBytes = new List<byte>();
            byte[] buf = new byte[4096];
            while (!_quitting.IsCancellationRequested)
            {
                try
                {
                    if (!(await _ready.WaitHandle.WaitOneAsync(_quitting.Token))) { continue; }
                    Socket socket = _client.Client;

                    int bytesRead = socket.Receive(buf);
                    //Log.Debug($"Got {bytesRead} bytes from socket.");

                    if (bytesRead <= 0)
                    {
                        // Remote end closed the connection - trigger a reconnect.
                        mBytes.Clear();
                        _error.Set();
                        continue;
                    }

                    //this is "slow" but the messages are tiny so we don't really care
                    foreach (byte b in buf.Take(bytesRead))
                    {
                        if (b != 0) { mBytes.Add(b); }
                        else
                        {
                            //Log.Debug($"Got a complete message: {mBytes.ToArray().ToHexadecimalString()}");
                            string json = Encoding.UTF8.GetString(mBytes.ToArray());
                            //Log.Debug($"Got a complete message: {json}");
                            //Request req = JsonConvert.DeserializeObject<Request>(json, JSON_SETTINGS);
                            SimpleJSONRequest req = SimpleJSONRequest.Parse(json);
                            //Log.Debug($"Got a request with ID {req.id}.");
                            try { OnRequestReceived?.Invoke(req); }
                            catch (Exception e) { Logger.LogError(e); }
                            mBytes.Clear();
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    _error.Set();

                    // Only back off on errors - delaying after every successful read
                    // throttles effect handling to one batch per second.
                    if (!_quitting.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1)); }
                }
            }
        }

        private async void KeepAlive()
        {
            while (!_quitting.IsCancellationRequested)
            {
                try
                {
                    if (Connected) { await Respond(new EffectResponse { id = 0, type = ResponseType.KeepAlive }); }
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    _error.Set();
                }
                finally { if (!_quitting.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1)); } }
            }
        }

        public event Action<SimpleJSONRequest> OnRequestReceived;
        public event Action OnConnected;

        public async Task<bool> Respond(SimpleJSONResponse response)
        {
            string json = response.Serialize();
            byte[] buffer = Encoding.UTF8.GetBytes(json + '\0');
            await _client_lock.WaitAsync();
            try
            {
                Socket socket = _client?.Client;
                if (socket == null || !Connected) { return false; }
                int bytesSent = socket.Send(buffer);
                return bytesSent > 0;
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                return false;
            }
            finally { _client_lock.Release(); }
        }
    }
}
