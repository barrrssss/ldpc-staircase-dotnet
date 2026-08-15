using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LdpcStaircase;
using Xunit;

namespace LdpcStaircase.Tests
{
    /// <summary>
    /// Pins the SPEC.md section 1 profile exclusions as executable invariants.
    /// Mirrors <c>ProfileInvariantsTest</c> in <c>ldpc-staircase-java</c>.
    /// <para>
    /// <b>Why this test exists.</b> The profile is defined as much by what it does <em>not</em>
    /// do as by what it does, and those exclusions were chosen deliberately — they are what
    /// keep this codec clear of the adjacent patented techniques surveyed in the consuming
    /// product's IPR review. Absences are uniquely fragile: nothing breaks, no test goes red,
    /// and no user notices if a future optimisation quietly reintroduces one. A decoder that
    /// returned to peeling after its dense solve would still be correct, still be fast, and
    /// still pass every other test in this repository.
    /// </para>
    /// <para>
    /// So these assertions are a tripwire against our own future changes, not a correctness
    /// check. If one fails, the fix is not to relax the assertion: it is to stop, and treat the
    /// change as a profile change requiring new conformance vectors and a fresh IPR review,
    /// exactly as SPEC.md section 1 requires.
    /// </para>
    /// </summary>
    public sealed class ProfileInvariantsTests
    {
        /// <summary>
        /// SPEC.md section 6: peel to a fixed point, then <em>at most one</em> complete residual
        /// solve, and never iterate between the two.
        /// <para>
        /// The counters are incremented inside <c>Peel</c> and <c>ResidualSolve</c> themselves
        /// rather than at their call sites, so a call introduced anywhere — from inside the
        /// other stage, or from a new enclosing loop — is caught.
        /// </para>
        /// </summary>
        [Fact]
        public void DecodeNeverIteratesBetweenItsTwoStages()
        {
            int[][] shapes =
            {
                // K, R, N1, seed, symbolLength
                new[] { 16, 8, 3, 12345, 32 },
                new[] { 64, 32, 7, 42, 64 },
                new[] { 256, 128, 7, 7, 112 },
                new[] { 512, 256, 7, 99, 24 },
                new[] { 1000, 400, 7, 2026, 358 },
            };

            int checkedCases = 0;
            foreach (int[] shape in shapes)
            {
                int k = shape[0];
                int r = shape[1];
                int n = k + r;

                foreach (int lossPercent in new[] { 0, 5, 15, 25, 40, 60, 90 })
                {
                    foreach (long dataSeed in new long[] { 1L, 2L })
                    {
                        var fixture = new Fixture(k, r, shape[2], shape[3], shape[4], dataSeed);
                        // Decode fills the present-mask in place as it recovers, so record
                        // whether anything was missing before handing it over.
                        bool anythingMissing = lossPercent > 0;
                        long patternSeed = (dataSeed * 31) + lossPercent;

                        bool[] present = LossPattern(n, lossPercent, patternSeed);
                        byte[][] symbols = fixture.CopyFor(present);
                        DecodeResult result = fixture.Codec.Decode(symbols, present, DecodeOptions.Default);
                        AssertStageInvariants(result, anythingMissing, shape, lossPercent);
                        checkedCases++;

                        // The bounded-refusal path: a caller-supplied limit that the residual
                        // system exceeds must produce a clean stop, never a fallback strategy
                        // that resumes peeling or solves in pieces.
                        bool[] present2 = LossPattern(n, lossPercent, patternSeed);
                        byte[][] symbols2 = fixture.CopyFor(present2);
                        DecodeResult bounded = fixture.Codec.Decode(symbols2, present2,
                            DecodeOptions.Default.WithMaxResidualUnknowns(1));
                        AssertStageInvariants(bounded, anythingMissing, shape, lossPercent);
                        Assert.True(bounded.ResidualSolvePassCount == 0 || bounded.ResidualUnknownCount <= 1,
                            "a bounded decode must refuse rather than solve past its limit");
                        checkedCases++;

                        // Residual solve disabled entirely: peeling must still run exactly once
                        // and must not be retried to compensate.
                        bool[] present3 = LossPattern(n, lossPercent, patternSeed);
                        byte[][] symbols3 = fixture.CopyFor(present3);
                        DecodeResult peelOnly = fixture.Codec.Decode(symbols3, present3,
                            DecodeOptions.Default.WithResidualSolve(false));
                        AssertStageInvariants(peelOnly, anythingMissing, shape, lossPercent);
                        Assert.Equal(0, peelOnly.ResidualSolvePassCount);
                        checkedCases++;
                    }
                }
            }

            Assert.True(checkedCases >= 200, $"expected a broad sweep, only checked {checkedCases}");
        }

        private static void AssertStageInvariants(DecodeResult result, bool anythingMissing, int[] shape,
            int lossPercent)
        {
            string where = string.Format(CultureInfo.InvariantCulture,
                "K={0} R={1} N1={2} seed={3} S={4} loss={5}%",
                shape[0], shape[1], shape[2], shape[3], shape[4], lossPercent);

            Assert.True(result.PeelPassCount <= 1, $"peeling must run at most once: {where}");
            Assert.True(result.ResidualSolvePassCount <= 1,
                $"the residual solve must run at most once: {where}");
            Assert.True(result.PeelPassCount == (anythingMissing ? 1 : 0),
                $"peeling must run exactly once when anything is missing, and never otherwise: {where}");

            if (result.ResidualSolvePassCount == 1)
            {
                Assert.True(result.PeelPassCount == 1,
                    $"a residual solve must be preceded by exactly one peel: {where}");
                Assert.Equal(DecodeStage.ResidualSolve, result.Stage);
                Assert.False(result.ResidualSolveSkipped,
                    $"a decode that solved must not also report a skip: {where}");
            }
            else
            {
                Assert.True(result.Stage != DecodeStage.ResidualSolve,
                    $"only a real solve may report the residual stage: {where}");
            }
        }

        /// <summary>
        /// SPEC.md section 2: <c>K</c> and <c>R</c> are fixed when the codec is created and every
        /// repair symbol exists before anything is transmitted. The profile excludes rateless and
        /// post-factum repair generation, so there must be no way to obtain an additional symbol
        /// after encoding, and re-encoding must reproduce the same set rather than extend it.
        /// </summary>
        [Fact]
        public void RepairSymbolsAreFixedAtCreationAndCannotBeGeneratedLater()
        {
            const int k = 128;
            const int r = 64;
            var fixture = new Fixture(k, r, 7, 555, 96, 3L);

            Assert.Equal(k, fixture.Codec.SourceSymbolCount);
            Assert.Equal(r, fixture.Codec.RepairSymbolCount);
            Assert.Equal(k + r, fixture.Codec.EncodingSymbolCount);

            var again = new byte[k + r][];
            for (int i = 0; i < k; i++)
            {
                again[i] = (byte[])fixture.Encoded[i].Clone();
            }

            fixture.Codec.Encode(again);
            Assert.Equal(k + r, again.Length);
            for (int i = 0; i < k + r; i++)
            {
                Assert.Equal(fixture.Encoded[i], again[i]);
            }
        }

        /// <summary>
        /// A named tripwire for the profile's excluded techniques. It is deliberately name-based
        /// and therefore weak on its own, but it is free, and it catches the realistic case: an
        /// optimisation added in good faith, under its ordinary name, by someone who never read
        /// SPEC.md section 1.
        /// </summary>
        [Fact]
        public void ThePublicApiExposesNoneOfTheExcludedTechniques()
        {
            string[] excluded =
            {
                "inactiv",      // permanent / predetermined / on-the-fly inactivation
                "defer",        // deferred-variable scheduling
                "precode",      // Raptor-style precode layer
                "intermediate", // L >= K intermediate symbols
                "rateless",     // unbounded output symbol generation
                "triangle",     // LDPC-Triangle
                "grouping",     // G > 1, multiple symbols per transport unit
            };

            var offenders = new List<string>();
            foreach (Type type in new[]
                     {
                         typeof(LdpcStaircaseCodec), typeof(DecodeOptions), typeof(DecodeResult),
                         typeof(ParityCheckMatrix),
                     })
            {
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
                             | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string name = member.Name.ToLowerInvariant();
                    foreach (string term in excluded)
                    {
                        if (name.Contains(term, StringComparison.Ordinal))
                        {
                            offenders.Add($"{type.Name}.{member.Name} (matched '{term}')");
                        }
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "SPEC.md section 1 excludes these techniques; adding one is a profile change "
                + "requiring new vectors and a fresh IPR review, not a passing test: "
                + string.Join(", ", offenders));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>A present-mask with roughly <paramref name="lossPercent"/> of the symbols erased.</summary>
        private static bool[] LossPattern(int n, int lossPercent, long seed)
        {
            var present = new bool[n];
            for (int i = 0; i < n; i++)
            {
                present[i] = true;
            }

            if (lossPercent == 0)
            {
                return present;
            }

            var rng = new SplitMix64(seed);
            int target = (n * lossPercent) / 100;
            int erased = 0;
            while (erased < target)
            {
                int index = rng.NextInt(n);
                if (present[index])
                {
                    present[index] = false;
                    erased++;
                }
            }

            return present;
        }

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

            /// <summary>A fresh symbol array holding only what <paramref name="present"/> marks.</summary>
            internal byte[][] CopyFor(bool[] present)
            {
                var symbols = new byte[Encoded.Length][];
                for (int i = 0; i < Encoded.Length; i++)
                {
                    symbols[i] = present[i] ? (byte[])Encoded[i].Clone() : null;
                }

                return symbols;
            }
        }
    }
}
