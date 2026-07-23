namespace LdpcStaircase.Tests
{
    /// <summary>
    /// The SplitMix64 generator, used only by tests to fill symbol payloads and drive
    /// deterministic loss-pattern sweeps.
    ///
    /// <para>It is deliberately <em>not</em> the codec's PRNG: <see cref="PmmsRandom"/> is
    /// normative and must never be used for anything but matrix construction. This mirrors the
    /// helper of the same name in <c>ldpc-staircase-java</c> bit for bit, so a sweep can be
    /// reproduced in either language.</para>
    /// </summary>
    internal sealed class SplitMix64
    {
        private ulong _state;

        internal SplitMix64(long seed)
        {
            _state = unchecked((ulong)seed);
        }

        internal long NextLong()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (long)(z ^ (z >> 31));
            }
        }

        /// <summary>A value in <c>[0, bound)</c>; <paramref name="bound"/> must be positive.</summary>
        internal int NextInt(int bound)
        {
            return (int)(unchecked((ulong)NextLong()) % (ulong)bound);
        }

        /// <summary>Fills <paramref name="length"/> bytes from successive big-endian 64-bit outputs.</summary>
        internal byte[] NextBytes(int length)
        {
            var output = new byte[length];
            int i = 0;
            while (i < length)
            {
                long value = NextLong();
                for (int shift = 56; shift >= 0 && i < length; shift -= 8)
                {
                    output[i++] = (byte)(value >> shift);
                }
            }

            return output;
        }
    }
}
