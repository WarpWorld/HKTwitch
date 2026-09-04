using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ConnectorLib.JSON;
using CrowdControl;
using HollowTwitch.Entities;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace HollowTwitch.Clients
{
    /// <summary>
    /// Client for the Crowd Control desktop app (local TCP connection).
    /// Network messages arrive on a background thread and are queued; all game work
    /// (and all Unity API access) happens on the main thread in <see cref="CrowdControlUpdater"/>.
    /// </summary>
    public class CrowdControlClient : IClient
    {
        public event Func<string, string, long?, uint?, uint, (EffectStatus, Command)> ChatMessageReceived;
        public event Action<string> ClientErrored;
        public event Func<GameUpdate> GameStateRequested;
        public event Func<IEnumerable<DataResponse>> MetadataRequested;
        public event Func<string, uint, bool> EffectStopRequested;

        private SimpleTCPClient _client;

        private readonly ConcurrentQueue<SimpleJSONRequest> _requests = new();

        public CrowdControlClient(Config config)
        {
            // Created on the main thread (mod init) so Unity object creation is safe here.
            var go = new GameObject("HollowTwitch.CrowdControlUpdater");
            UObject.DontDestroyOnLoad(go);
            go.AddComponent<CrowdControlUpdater>().Client = this;
        }

        public void Dispose()
        {
            try { _client?.Dispose(); }
            catch {/**/}
        }

        public void StartReceive()
        {
            Connect("127.0.0.1", 58430);
        }

        private void Connect(string host, int port)
        {
            try { _client?.Dispose(); }
            catch { /**/ }
            _client = new SimpleTCPClient();

            // Just queue it - requests are handled on the main thread in Update().
            _client.OnRequestReceived += r => _requests.Enqueue(r);
            _client.OnConnected += () => _forceStateReport = true;

            Logger.Log("Connecting...");
        }

        public void Send(SimpleJSONResponse response)
        {
            if (response == null) return;
            _client?.Respond(response);
        }

        private GameState? _lastReportedState;
        private volatile bool _forceStateReport;

        /// <summary>
        /// Runs on the main thread: drains queued client messages and pushes game state
        /// changes so the Crowd Control app releases queued effects as soon as the game
        /// becomes ready again, instead of waiting to poll us.
        /// </summary>
        private class CrowdControlUpdater : MonoBehaviour
        {
            internal CrowdControlClient Client;

            private void Update()
            {
                if (Client == null) return;

                while (Client._requests.TryDequeue(out SimpleJSONRequest request))
                {
                    try { Client.HandleRequest(request); }
                    catch (Exception e) { Logger.LogError(e); }
                }

                Client.ReportGameState();
            }
        }

        private void ReportGameState()
        {
            try
            {
                GameUpdate update = GameStateRequested?.Invoke();

                if (update == null) return;

                if (_forceStateReport || update.state != _lastReportedState)
                {
                    _forceStateReport = false;
                    _lastReportedState = update.state;
                    _client?.Respond(update);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }

        private Dictionary<string, DataResponse> TryGetMetadata()
        {
            try
            {
                return MetadataRequested?.Invoke()?.ToDictionary(m => m.key);
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                return null;
            }
        }

        /// <summary>Handles a single client message. Runs on the main thread. Always answers effect requests.</summary>
        private void HandleRequest(SimpleJSONRequest request)
        {
            if (request == null)
            {
                Logger.LogError("HandleRequest got a null request.");
                return;
            }

            switch (request)
            {
                case EffectRequest req when request.type == RequestType.EffectStart:
                    HandleEffectStart(req);
                    return;

                case EffectRequest req when request.type == RequestType.EffectTest:
                {
                    // Respond as if the effect would start, but don't actually run it.
                    bool ready = CrowdControl.Instance?.Processor?.IsGameReady() ?? false;

                    _client?.Respond(new EffectResponse
                    {
                        id = req.id,
                        status = ready ? EffectStatus.Success : EffectStatus.Retry
                    });
                    return;
                }

                case EffectRequest req when request.type == RequestType.EffectStop:
                {
                    // If a running effect is found, it reports Finished (with its original id)
                    // once it has unwound and restored state; nothing else to send here.
                    bool found = EffectStopRequested?.Invoke(req.code, req.id) ?? false;

                    if (!found)
                    {
                        _client?.Respond(new EffectResponse
                        {
                            id = req.id,
                            status = EffectStatus.Failure
                        });
                    }
                    return;
                }

                case not null when request.type == RequestType.GameUpdate:
                {
                    try
                    {
                        GameUpdate result = GameStateRequested?.Invoke();
                        if (result != null) _client?.Respond(result);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                        _client?.Respond(new GameUpdate(GameState.Error));
                    }
                    return;
                }

                case not null when request.type == RequestType.Version:
                    _client?.Respond(new VersionResponse(request.id, new VersionNumber(HollowTwitch.CrowdControl.ModVersion)));
                    return;

                case not null when request.type == RequestType.KeepAlive:
                case not null when request.type == RequestType.PlayerInfo:
                case not null when request.type == RequestType.Login:
                    return;

                default:
                    // Anything new the client starts sending is ignored rather than treated as an error.
                    Logger.Log($"Ignoring client message of type {request.type}.");
                    return;
            }
        }

        private void HandleEffectStart(EffectRequest req)
        {
            EffectResponse response;

            try
            {
                string command = string.Join(" ",
                    (req.parameters?.Select(p => p.ToString()) ?? Array.Empty<string>())
                    .Prepend('!' + req.code.Replace('_', ' ')).ToArray());

                // "3x"/"5x" purchases arrive as a quantity on a single request.
                uint quantity = Math.Min(Math.Max(req.quantity ?? 1, 1), 100);

                (EffectStatus status, Command cmd) =
                    ChatMessageReceived?.Invoke(req.viewer, command, req.duration, req.id, quantity)
                    ?? (EffectStatus.Retry, null);

                // timeRemaining on Success means "this timed effect is now running for X ms".
                // Instant effects must report 0 - reporting the cooldown here made the client
                // treat them as long-running effects and stall repeat purchases in its queue.
                response = new EffectResponse
                {
                    id = req.id,
                    status = status,
                    timeRemaining = req.duration ?? 0L,
                    metadata = TryGetMetadata()
                };
            }
            catch (Exception e)
            {
                Logger.LogError(e);

                // Never leave a request unanswered - an unanswered effect sits in the
                // client's queue forever.
                response = new EffectResponse
                {
                    id = req.id,
                    status = EffectStatus.Retry
                };
            }

            _client?.Respond(response);
        }
    }
}
