using System;
using System.Collections.Generic;
using System.Linq;
using LdpcStaircase;
using Xunit;

namespace LdpcStaircase.Tests
{
    /// <summary>
    /// Encode/decode behaviour of <see cref="LdpcStaircaseCodec"/> (SPEC.md sections 5 and 6).
    /// Mirrors <c>LdpcStaircaseTest</c> in <c>ldpc-staircase-java</c>; the byte-exact agreement
    /// between the two is enforced separately by <see cref="ConformanceTests"/>.
    /// </summary>
    public sealed class LdpcStaircaseTests
    {
        // ------------------------------------------------------------------
        // Encoding
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(16, 8, 3, 32)]
        [InlineData(64, 32, 7, 17)]
        [InlineData(100, 50, 7, 1)]
        [InlineData(250, 25, 7, 818)]
        [InlineData(1, 1, 1, 8)]
        public void EncodedSymbolsSatisfyEveryEquation(int k, int r, int n1, int symbolLength)
        {
            var fixture = new Fixture(k, r, n1, 12345, symbolLength, 1);
            Assert.True(fixture.Codec.Verify(fixture.Encoded));
        }

        [Fact]
        public void EncodingIsDeterministic()
        {
            var a = new Fixture(64, 32, 7, 42, 25, 7);
            var b = new Fixture(64, 32, 7, 42, 25, 7);
            for (int i = 0; i < a.Codec.EncodingSymbolCount; i++)
            {
                Assert.Equal(a.Encoded[i], b.Encoded[i]);
            }
        }

        /// <summary>
        /// SPEC.md section 5: repair symbol <c>p</c> is its equation's source symbols XORed with
        /// repair symbol <c>p-1</c>. Recomputing that by hand must reproduce the encoder's output.
        /// </summary>
        [Fact]
        public void RepairSymbolsFollowTheRunningXorDefinition()
        {
            const int k = 32;
            const int r = 16;
            var fixture = new Fixture(k, r, 5, 77, 24, 3);
            ParityCheckMatrix h = fixture.Codec.ParityCheckMatrix;

            byte[] previous = null;
            for (int p = 0; p < r; p++)
            {
                var expected = new byte[24];
                foreach (int c in h.RowColumns(p))
                {
                    if (c < k)
                    {
                        Xor(expected, fixture.Encoded[c]);
                    }
                }

                if (previous != null)
                {
                    Xor(expected, previous);
                }

                Assert.Equal(expected, fixture.Encoded[k + p]);
                previous = expected;
            }
        }

        [Fact]
        public void VerifyRejectsACorruptedSymbol()
        {
            var fixture = new Fixture(32, 16, 5, 5, 16, 11);
            Assert.True(fixture.Codec.Verify(fixture.Encoded));
            fixture.Encoded[3][0] ^= 0x01;
            Assert.False(fixture.Codec.Verify(fixture.Encoded));
        }

        [Fact]
        public void NoRepairCodeEncodesNothing()
        {
            var fixture = new Fixture(8, 0, 7, 1, 16, 2);
            Assert.Equal(8, fixture.Codec.EncodingSymbolCount);
            Assert.True(fixture.Codec.Verify(fixture.Encoded));
        }

        // ------------------------------------------------------------------
        // Decoding
        // ------------------------------------------------------------------

        [Fact]
        public void NothingMissingIsANoOp()
        {
            var fixture = new Fixture(32, 16, 5, 3, 16, 4);
            Session session = fixture.Session(AllPresent(fixture));
            DecodeResult result = session.Decode();

            Assert.True(result.IsComplete);
            Assert.Equal(DecodeStage.None, result.Stage);
            Assert.Equal(0, result.RecoveredSymbolCount);
            session.AssertMatchesOriginal();
        }

        /// <summary>With every source symbol in hand the staircase rebuilds the repair symbols.</summary>
        [Fact]
        public void MissingRepairSymbolsAreRebuilt()
        {
            var fixture = new Fixture(32, 16, 5, 3, 16, 4);
            bool[] present = AllPresent(fixture);
            for (int p = 0; p < 16; p++)
            {
                present[32 + p] = false;
            }

            Session session = fixture.Session(present);
            DecodeResult result = session.Decode();

            Assert.True(result.IsComplete);
            Assert.Equal(DecodeStage.Peeling, result.Stage);
            Assert.Equal(16, result.RecoveredSymbolCount);
            Assert.Equal(0, result.RecoveredSourceCount);
            session.AssertMatchesOriginal();
        }

        [Theory]
        [InlineData(64, 1)]
        [InlineData(64, 5)]
        [InlineData(64, 16)]
        [InlineData(64, 30)]
        public void RandomSourceLossIsRecovered(int k, int lost)
        {
            var fixture = new Fixture(k, k / 2, 7, 4242, 32, 9);
            bool[] present = AllPresent(fixture);
            var rng = new SplitMix64(lost);
            int removed = 0;
            while (removed < lost)
            {
                int index = rng.NextInt(k);
                if (present[index])
                {
                    present[index] = false;
                    removed++;
                }
            }

            Session session = fixture.Session(present);
            DecodeResult result = session.Decode();

            Assert.True(result.IsComplete, "decode failed: " + result);
            Assert.Equal(lost, result.RecoveredSourceCount);
            session.AssertMatchesOriginal();
        }

        /// <summary>
        /// A whole lost sheet is a contiguous run of symbol indices — the correlated damage
        /// pattern that matters for printed backups.
        /// </summary>
        [Fact]
        public void ContiguousBandLossIsRecovered()
        {
            var fixture = new Fixture(200, 100, 7, 8181, 40, 21);
            bool[] present = AllPresent(fixture);
            for (int i = 60; i < 140; i++)
            {
                present[i] = false;
            }

            Session session = fixture.Session(present);
            DecodeResult result = session.Decode();

            Assert.True(result.IsComplete, "decode failed: " + result);
            session.AssertMatchesOriginal();
        }

        [Fact]
        public void MixedSourceAndRepairLossIsRecovered()
        {
            var fixture = new Fixture(200, 100, 7, 616, 40, 22);
            bool[] present = AllPresent(fixture);
            for (int i = 0; i < 300; i += 6)
            {
                present[i] = false;
            }

            Session session = fixture.Session(present);
            DecodeResult result = session.Decode();

            Assert.True(result.IsComplete, "decode failed: " + result);
            session.AssertMatchesOriginal();
        }

        /// <summary>
        /// The property that matters most: whenever a decode reports completion, every recovered
        /// source symbol must equal the original byte-for-byte.
        /// </summary>
        [Fact]
        public void CompletedDecodesAlwaysReproduceTheSource()
        {
            const int k = 120;
            const int r = 60;
            var fixture = new Fixture(k, r, 7, 31337, 24, 55);
            var rng = new SplitMix64(0xC0FFEE);

            int complete = 0;
            for (int trial = 0; trial < 300; trial++)
            {
                bool[] present = AllPresent(fixture);
                int lost = 1 + rng.NextInt(r);
                for (int i = 0; i < lost; i++)
                {
                    present[rng.NextInt(k + r)] = false;
                }

                Session session = fixture.Session(present);
                DecodeResult result = session.Decode();
                if (result.IsComplete)
                {
                    complete++;
                    session.AssertMatchesOriginal();
                }
                else
                {
                    int[] missing = result.MissingSourceIndices();
                    Assert.All(missing, index => Assert.InRange(index, 0, k - 1));
                    Assert.Equal(missing.Length, missing.Distinct().Count());
                }
            }

            Assert.True(complete > 200, $"expected most sampled losses to be recoverable; got {complete}");
        }

        /// <summary>
        /// The residual solve is not optional: sparse peeling stalls on patterns the received set
        /// still determines.
        /// </summary>
        [Fact]
        public void ResidualSolveRecoversWherePeelingStalls()
        {
            var fixture = new Fixture(120, 60, 7, 2468, 24, 66);
            var rng = new SplitMix64(0xBEEF);

            int stalled = 0;
            int rescued = 0;
            for (int trial = 0; trial < 400; trial++)
            {
                bool[] present = AllPresent(fixture);
                int lost = 45 + rng.NextInt(15);
                for (int i = 0; i < lost; i++)
                {
                    present[rng.NextInt(180)] = false;
                }

                Session peelingOnly = fixture.Session((bool[])present.Clone());
                if (peelingOnly.Decode(DecodeOptions.Default.WithResidualSolve(false)).IsComplete)
                {
                    continue;
                }

                stalled++;
                Session full = fixture.Session((bool[])present.Clone());
                DecodeResult result = full.Decode();
                Assert.Equal(DecodeStage.ResidualSolve, result.Stage);
                Assert.True(result.ResidualUnknownCount > 0);
                if (result.IsComplete)
                {
                    rescued++;
                    full.AssertMatchesOriginal();
                }
            }

            Assert.True(stalled > 0, "the sweep never stalled peeling; it proves nothing");
            Assert.True(rescued > 0, "the residual solve never rescued a stalled decode");
        }

        /// <summary>
        /// LDPC-Staircase is not MDS. This deterministic sweep holds exactly <c>K</c> symbols —
        /// always enough for Reed-Solomon — and shows that some patterns still fail.
        /// </summary>
        [Fact]
        public void ExactlyKReceivedSymbolsIsNotAGuarantee()
        {
            const int k = 120;
            const int r = 60;
            var fixture = new Fixture(k, r, 7, 31337, 24, 77);
            var rng = new SplitMix64(0xD15EA5E);

            int failures = 0;
            for (int trial = 0; trial < 200; trial++)
            {
                bool[] present = AllPresent(fixture);
                int removed = 0;
                while (removed < r)
                {
                    int index = rng.NextInt(k + r);
                    if (present[index])
                    {
                        present[index] = false;
                        removed++;
                    }
                }

                if (!fixture.Session(present).Decode().IsComplete)
                {
                    failures++;
                }
            }

            Assert.True(failures > 0, "expected the maximum-loss sweep to expose the non-MDS margin");
        }

        [Fact]
        public void UnrecoverableSessionReportsWhatIsMissing()
        {
            const int k = 64;
            const int r = 32;
            var fixture = new Fixture(k, r, 7, 12, 16, 88);

            bool[] present = AllPresent(fixture);
            for (int i = 0; i < k + r; i++)
            {
                present[i] = i % 4 == 0;
            }

            Session session = fixture.Session(present);
            DecodeResult result = session.Decode();

            Assert.False(result.IsComplete);
            Assert.NotEmpty(result.MissingSourceIndices());
            Assert.All(result.MissingSourceIndices(), index => Assert.False(session.Present[index]));
        }

        [Fact]
        public void ResidualSolveCanBeBoundedForMemory()
        {
            const int k = 120;
            const int r = 60;
            var fixture = new Fixture(k, r, 7, 31337, 24, 99);
            bool[] present = AllPresent(fixture);
            for (int i = 0; i < 58; i++)
            {
                present[i * 3 % (k + r)] = false;
            }

            Session unbounded = fixture.Session((bool[])present.Clone());
            DecodeResult reference = unbounded.Decode();
            if (reference.Stage != DecodeStage.ResidualSolve)
            {
                return; // This pattern peels cleanly; nothing to bound.
            }

            Session bounded = fixture.Session((bool[])present.Clone());
            DecodeResult result = bounded.Decode(
                DecodeOptions.Default.WithMaxResidualUnknowns(reference.ResidualUnknownCount - 1));

            Assert.False(result.IsComplete);
            Assert.True(result.ResidualSolveSkipped);
            Assert.Equal(DecodeStage.Peeling, result.Stage);
            Assert.Equal(reference.ResidualUnknownCount, result.ResidualUnknownCount);
        }

        [Fact]
        public void NoRepairCodeDecodesOnlyWhenNothingIsMissing()
        {
            var fixture = new Fixture(8, 0, 7, 1, 16, 100);

            bool[] present = AllPresent(fixture);
            Assert.True(fixture.Session(present).Decode().IsComplete);

            present[3] = false;
            DecodeResult result = fixture.Session(present).Decode();
            Assert.False(result.IsComplete);
            Assert.Equal(new[] { 3 }, result.MissingSourceIndices());
        }

        // ------------------------------------------------------------------
        // Progress reporting
        // ------------------------------------------------------------------

        [Fact]
        public void ProgressIsMonotoneAndEndsExactlyAtTheTotal()
        {
            var fixture = new Fixture(200, 100, 7, 555, 32, 111);
            bool[] present = AllPresent(fixture);
            for (int i = 0; i < 90; i++)
            {
                present[i * 3 % 300] = false;
            }

            var ticks = new List<(long Done, long Total)>();
            DecodeResult result = fixture.Session(present).Decode(
                DecodeOptions.Default.WithProgressListener((done, total) => ticks.Add((done, total))));

            Assert.True(result.IsComplete, "decode failed: " + result);
            Assert.NotEmpty(ticks);

            long expectedTotal = ticks[0].Total;
            Assert.True(expectedTotal > 0);
            long previous = -1;
            foreach ((long done, long total) in ticks)
            {
                Assert.Equal(expectedTotal, total);
                Assert.True(done >= previous, "progress went backwards");
                Assert.True(done <= total, "progress exceeded the total");
                previous = done;
            }

            Assert.Equal(expectedTotal, ticks[ticks.Count - 1].Done);
        }

        [Fact]
        public void ProgressListenerCanCancelADecode()
        {
            var fixture = new Fixture(200, 100, 7, 555, 32, 112);
            bool[] present = AllPresent(fixture);
            for (int i = 0; i < 90; i++)
            {
                present[i * 3 % 300] = false;
            }

            Assert.Throws<InvalidOperationException>(() => fixture.Session(present).Decode(
                DecodeOptions.Default.WithProgressListener(
                    (done, total) => throw new InvalidOperationException("cancelled"))));
        }

        // ------------------------------------------------------------------
        // Split / Join
        // ------------------------------------------------------------------

        [Fact]
        public void SplitAndJoinRoundTrip()
        {
            byte[] data = new SplitMix64(4).NextBytes(1000);
            byte[][] symbols = LdpcStaircaseCodec.Split(data, 40, 32);

            Assert.Equal(40, symbols.Length);
            Assert.All(symbols, symbol => Assert.Equal(32, symbol.Length));
            Assert.Equal(data, LdpcStaircaseCodec.Join(symbols, data.Length));
        }

        [Fact]
        public void SplitZeroPadsTheTail()
        {
            byte[][] symbols = LdpcStaircaseCodec.Split(new byte[] { 1, 2, 3 }, 2, 4);
            Assert.Equal(new byte[] { 1, 2, 3, 0 }, symbols[0]);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, symbols[1]);
        }

        /// <summary>Unlike erasure16's GF(2^16) words, GF(2) symbols have no even-length rule.</summary>
        [Fact]
        public void OddSymbolLengthsAreLegal()
        {
            byte[][] symbols = LdpcStaircaseCodec.Split(new SplitMix64(5).NextBytes(21), 3, 7);
            Assert.Equal(7, symbols[0].Length);
        }

        [Fact]
        public void SplitAndJoinRejectBadArguments()
        {
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Split(null, 4, 8));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Split(new byte[8], 0, 8));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Split(new byte[8], 4, 0));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Split(new byte[100], 4, 8));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Join(null, 0));
            Assert.Throws<LdpcStaircaseException>(
                () => LdpcStaircaseCodec.Join(new[] { new byte[4] }, -1));
            Assert.Throws<LdpcStaircaseException>(
                () => LdpcStaircaseCodec.Join(new[] { new byte[4] }, 5));
        }

        // ------------------------------------------------------------------
        // Argument validation
        // ------------------------------------------------------------------

        [Fact]
        public void ConstructionRejectsBadParameters()
        {
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Create(0, 8, 1));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Create(16, -1, 1));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Create(16, 8, 0));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Create(16, 8, 1, 0));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Create(16, 8, 1, 9));
            Assert.Throws<LdpcStaircaseException>(() => LdpcStaircaseCodec.Of(null));
        }

        [Fact]
        public void EncodeRejectsMalformedSymbolArrays()
        {
            LdpcStaircaseCodec codec = LdpcStaircaseCodec.Create(4, 2, 2, 1);

            Assert.Throws<LdpcStaircaseException>(() => codec.Encode(null));
            Assert.Throws<LdpcStaircaseException>(() => codec.Encode(NewSymbols(3, 8)));
            Assert.Throws<LdpcStaircaseException>(() => codec.Encode(NewSymbols(6, 0)));

            byte[][] withNull = NewSymbols(6, 8);
            withNull[1] = null;
            Assert.Throws<LdpcStaircaseException>(() => codec.Encode(withNull));

            byte[][] ragged = NewSymbols(6, 8);
            ragged[2] = new byte[9];
            Assert.Throws<LdpcStaircaseException>(() => codec.Encode(ragged));
        }

        [Fact]
        public void DecodeRejectsMalformedSymbolArrays()
        {
            LdpcStaircaseCodec codec = LdpcStaircaseCodec.Create(4, 2, 2, 1);
            byte[][] symbols = NewSymbols(6, 8);

            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(symbols, null));
            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(symbols, new bool[5]));
            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(symbols, new bool[6]));
            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(symbols, AllTrue(6), null));

            byte[][] missingBacking = NewSymbols(6, 8);
            missingBacking[0] = null;
            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(missingBacking, AllTrue(6)));

            byte[][] ragged = NewSymbols(6, 8);
            ragged[1] = new byte[9];
            Assert.Throws<LdpcStaircaseException>(() => codec.Decode(ragged, AllTrue(6)));
        }

        [Fact]
        public void OptionsRejectNegativeBounds()
        {
            Assert.Throws<LdpcStaircaseException>(
                () => DecodeOptions.Default.WithMaxResidualUnknowns(-1));
        }

        [Fact]
        public void APrebuiltMatrixCanBeReused()
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(32, 16, 5, 4242);
            LdpcStaircaseCodec codec = LdpcStaircaseCodec.Of(h);
            Assert.Equal(32, codec.SourceSymbolCount);
            Assert.Equal(16, codec.RepairSymbolCount);
            Assert.Equal(5, codec.LeftDegree);
            Assert.Equal(4242, codec.Seed);
            Assert.NotNull(codec.ToString());
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static void Xor(byte[] dst, byte[] src)
        {
            for (int i = 0; i < dst.Length; i++)
            {
                dst[i] ^= src[i];
            }
        }

        private static byte[][] NewSymbols(int count, int length)
        {
            var symbols = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                symbols[i] = new byte[length];
            }

            return symbols;
        }

        private static bool[] AllPresent(Fixture fixture) => AllTrue(fixture.Codec.EncodingSymbolCount);

        private static bool[] AllTrue(int length)
        {
            var present = new bool[length];
            for (int i = 0; i < length; i++)
            {
                present[i] = true;
            }

            return present;
        }

        /// <summary>An encoded symbol set plus the code that produced it.</summary>
        private sealed class Fixture
        {
            internal readonly LdpcStaircaseCodec Codec;
            internal readonly byte[][] Encoded;

            internal Fixture(int k, int r, int n1, int seed, int symbolLength, long dataSeed)
            {
                Codec = LdpcStaircaseCodec.Create(k, r, seed, n1);
                Encoded = new byte[k + r][];

                var rng = new SplitMix64(dataSeed);
                for (int i = 0; i < k; i++)
                {
                    Encoded[i] = rng.NextBytes(symbolLength);
                }

                Codec.Encode(Encoded);
            }

            /// <summary>A decode session holding only the symbols <paramref name="present"/> marks.</summary>
            internal Session Session(bool[] present) => new Session(this, present);
        }

        /// <summary>One decode attempt over a copy of the fixture's symbols.</summary>
        private sealed class Session
        {
            private readonly Fixture _fixture;

            internal readonly byte[][] SymbolArray;
            internal readonly bool[] Present;

            internal Session(Fixture fixture, bool[] present)
            {
                _fixture = fixture;
                Present = present;
                SymbolArray = new byte[fixture.Encoded.Length][];
                for (int i = 0; i < SymbolArray.Length; i++)
                {
                    if (present[i])
                    {
                        SymbolArray[i] = (byte[])fixture.Encoded[i].Clone();
                    }
                }
            }

            internal DecodeResult Decode() => _fixture.Codec.Decode(SymbolArray, Present);

            internal DecodeResult Decode(DecodeOptions options) =>
                _fixture.Codec.Decode(SymbolArray, Present, options);

            internal void AssertMatchesOriginal()
            {
                for (int i = 0; i < _fixture.Codec.SourceSymbolCount; i++)
                {
                    Assert.True(Present[i], $"source symbol {i} was not recovered");
                    Assert.Equal(_fixture.Encoded[i], SymbolArray[i]);
                }

                for (int i = _fixture.Codec.SourceSymbolCount; i < SymbolArray.Length; i++)
                {
                    if (Present[i])
                    {
                        Assert.Equal(_fixture.Encoded[i], SymbolArray[i]);
                    }
                }
            }
        }
    }
}
