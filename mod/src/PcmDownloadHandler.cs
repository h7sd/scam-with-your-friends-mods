using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace ScamWYF.ElevenLabsAgents
{
    /// <summary>Streaming, bounded little-endian PCM16 buffer. Handles odd HTTP chunk boundaries.</summary>
    internal sealed class PcmDownloadHandler : DownloadHandlerScript
    {
        private const int MaxBufferedBytes = 16 * 1024 * 1024;
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private readonly object sync = new object();
        private int headOffset;
        private int bufferedBytes;
        internal bool Overflow { get; private set; }
        internal long TotalBytes { get; private set; }

        internal PcmDownloadHandler() : base(new byte[16384]) { }
        internal int AvailableSamples { get { lock (sync) { return bufferedBytes / 2; } } }
        internal int RemainingBytes { get { lock (sync) { return bufferedBytes; } } }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            lock (sync)
            {
                if (dataLength > MaxBufferedBytes - bufferedBytes)
                {
                    Overflow = true;
                    return false;
                }
                var copy = new byte[dataLength];
                Buffer.BlockCopy(data, 0, copy, 0, dataLength);
                chunks.Enqueue(copy);
                bufferedBytes += dataLength;
                TotalBytes += dataLength;
                return true;
            }
        }

        internal int ReadSamples(float[] destination)
        {
            lock (sync)
            {
                int count = Math.Min(destination.Length, bufferedBytes / 2);
                for (int i = 0; i < count; i++)
                {
                    int low = ReadByte();
                    int high = ReadByte();
                    destination[i] = (short)(low | (high << 8)) / 32768f;
                }
                if (count < destination.Length) Array.Clear(destination, count, destination.Length - count);
                return count;
            }
        }

        private byte ReadByte()
        {
            var first = chunks.Peek();
            byte result = first[headOffset++];
            bufferedBytes--;
            if (headOffset == first.Length)
            {
                chunks.Dequeue();
                headOffset = 0;
            }
            return result;
        }
    }
}
