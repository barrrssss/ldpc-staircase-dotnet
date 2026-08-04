using System;
using System.Diagnostics;
using System.Globalization;
using LdpcStaircase;
using Xunit;
using Xunit.Abstractions;

namespace LdpcStaircase.Tests
{
    /// <summary>
    /// Times matrix construction, encoding, and decoding up to a large stress case
    /// (<c>K = 10,000</c>, <c>R = 5,000</c>, <c>S = 818</c>). Gated off by default; enable with:
    ///
    /// <code>LDPC_BENCH=true dotnet test --filter FullyQualifiedName~DecoderBenchmark</code>
    ///
    /// <para>When the flag is absent the test is a no-op pass, mirroring the Java
    /// <c>@EnabledIfSystemProperty</c> gate. The numbers are directly comparable with
    /// <c>ldpc-staircase-java</c>'s <c>DecoderBenchmark</c>, which is the point: the two
    /// implementations share a contract, so a large gap is worth explaining.</para>
    /// </summary>
    public sealed class DecoderBenchmark
    {
        private const int SymbolLength = 818; // representative medium payload

        private readonly ITestOutputHelper _output;

        public DecoderBenchmark(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool Enabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LDPC_BENCH"));

        [Fact]
        public void BenchmarkAcrossSizes()
        {
            if (!Enabled)
            {
                return;
            }

            _output.WriteLine($"LDPC-Staircase, N1=7, S={SymbolLength}, R=K/2");
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,8} {1,8} {2,10} {3,10} {4,12} {5,12} {6,12}",
                "K", "R", "build ms", "encode ms", "peel ms", "residual ms", "residual u"));

            foreach (int k in new[] { 1_000, 2_500, 5_000, 10_000 })
            {
                Run(k, k / 2);
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine("peel ms: decode with R/2 random losses (expected to peel cleanly).");
            _output.WriteLine("residual ms: decode at the full R-symbol loss budget.");
        }

        private void Run(int k, int r)
        {
            int n = k + r;

            var watch = Stopwatch.StartNew();
            LdpcStaircaseCodec codec = LdpcStaircaseCodec.Create(k, r, 20260723, 7);
            long buildMs = watch.ElapsedMilliseconds;

            var encoded = new byte[n][];
            var rng = new SplitMix64(k);
            for (int i = 0; i < k; i++)
            {
                encoded[i] = rng.NextBytes(SymbolLength);
            }

            watch.Restart();
            codec.Encode(encoded);
            long encodeMs = watch.ElapsedMilliseconds;

            (DecodeResult light, long lightMs) = Decode(codec, encoded, n, r / 2, 1);
            (DecodeResult heavy, long heavyMs) = Decode(codec, encoded, n, r, 2);

            _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,8} {1,8} {2,10} {3,10} {4,12} {5,12} {6,12}",
                k, r, buildMs, encodeMs, lightMs, heavyMs, heavy.ResidualUnknownCount));

            if (!light.IsComplete)
            {
                _output.WriteLine("  note: the R/2 loss decode did not complete: " + light);
            }

            if (!heavy.IsComplete)
            {
                _output.WriteLine("  note: the full-budget decode did not complete (expected near the "
                    + "threshold): " + heavy);
            }
        }

        private static (DecodeResult Result, long Millis) Decode(LdpcStaircaseCodec codec,
            byte[][] encoded, int n, int lossCount, long lossSeed)
        {
            var present = new bool[n];
            for (int i = 0; i < n; i++)
            {
                present[i] = true;
            }

            var rng = new SplitMix64(lossSeed);
            int removed = 0;
            while (removed < lossCount)
            {
                int index = rng.NextInt(n);
                if (present[index])
                {
                    present[index] = false;
                    removed++;
                }
            }

            var symbols = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                if (present[i])
                {
                    symbols[i] = (byte[])encoded[i].Clone();
                }
            }

            var watch = Stopwatch.StartNew();
            DecodeResult result = codec.Decode(symbols, present);
            return (result, watch.ElapsedMilliseconds);
        }
    }
}
