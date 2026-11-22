using System;
using System.Collections.Generic;

namespace GraveyardOrbit.Coop
{
    public enum Delivery
    {
        Reliable,
        Unreliable
    }

    public readonly struct PeerId : IEquatable<PeerId>
    {
        public readonly ulong Value;

        public PeerId(ulong value) { Value = value; }

        public bool Equals(PeerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PeerId p && Equals(p);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(PeerId a, PeerId b) => a.Equals(b);
        public static bool operator !=(PeerId a, PeerId b) => !a.Equals(b);
    }

    /// <summary>
    /// Transport abstraction. Steam P2P is the shipping path; TCP exists for
    /// single-machine dev testing. Gameplay code never talks to either directly.
    /// </summary>
    public abstract class NetTransport : IDisposable
    {
        public event Action<PeerId> PeerJoined;
        public event Action<PeerId> PeerLeft;
        public event Action<PeerId, byte[]> PayloadReceived;

        public abstract bool IsHost { get; }
        public abstract PeerId LocalId { get; }
        public abstract IReadOnlyCollection<PeerId> Peers { get; }
        public abstract string Name { get; }

        public abstract void Send(PeerId to, byte[] data, Delivery delivery);
        public abstract void Poll();
        public abstract void Dispose();

        public void Broadcast(byte[] data, Delivery delivery)
        {
            foreach (PeerId peer in Peers)
            {
                if (peer != LocalId)
                {
                    Send(peer, data, delivery);
                }
            }
        }

        protected void RaiseJoined(PeerId peer) => PeerJoined?.Invoke(peer);
        protected void RaiseLeft(PeerId peer) => PeerLeft?.Invoke(peer);
        protected void RaisePayload(PeerId from, byte[] data) => PayloadReceived?.Invoke(from, data);
    }
}
