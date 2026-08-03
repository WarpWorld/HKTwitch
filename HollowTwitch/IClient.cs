using System;
using System.Collections.Generic;
using ConnectorLib.JSON;
using HollowTwitch.Entities;

namespace HollowTwitch
{
    public interface IClient : IDisposable
    {
        /// <summary>Raised for effect start requests. Args: user, command, duration (ms), request id.</summary>
        event Func<string, string, long?, uint?, (EffectStatus, Command)> ChatMessageReceived;

        event Action<string> ClientErrored;

        event Func<GameUpdate> GameStateRequested;
        event Func<IEnumerable<EffectResponseMetadata>> MetadataRequested;

        /// <summary>Raised for effect stop requests. Args: effect code, request id. Returns true if a running effect was found.</summary>
        event Func<string, uint, bool> EffectStopRequested;

        void StartReceive();

        /// <summary>Sends an unsolicited response/update to the client. Safe to call from any thread.</summary>
        void Send(SimpleJSONResponse response);
    }
}
