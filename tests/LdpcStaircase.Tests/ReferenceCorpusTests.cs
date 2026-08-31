using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LdpcStaircase;
using Xunit;

namespace LdpcStaircase.Tests
{
    /// <summary>
    /// The independent-oracle gate of specification decision 0002.
    ///
    /// <para><see cref="ConformanceTests"/> proves this implementation reproduces the committed
    /// vectors, which <c>ldpc-staircase-java</c> also generated. That leaves one failure mode
    /// open: the generator and both implementations could share a single misreading of
    /// <c>SPEC.md</c>. The specification repository therefore carries a dependency-free Python
    /// reference checker, derived from the specification rather than from either implementation,
    /// plus a committed differential corpus at <c>reference/corpus.json</c>.</para>
    ///
    /// <para>This suite runs that corpus through the .NET codec and compares every field with the
    /// checker's own answer for the same case. The checker is invoked as an ordinary subprocess
    /// and writes its results under the test output directory; nothing here references it, and
    /// the corpus is not a conformance vector set.</para>
    ///
    /// <para>When no Python 3.11+ interpreter is on the machine the corpus check reports success
    /// with a skip notice, so an ordinary developer build still runs. Set the environment
    /// variable <c>LDPC_REFERENCE_CORPUS_REQUIRED=1</c> to turn a missing interpreter into a
    /// failure; the pre-release checklist uses the required form.</para>
    /// </summary>
    public sealed class ReferenceCorpusTests
    {
        private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

        private static readonly Lazy<CorpusRun> Corpus = new Lazy<CorpusRun>(RunReferenceChecker);

        /// <summary>
        /// Every corpus case, compared field by field against the independent checker.
        /// xUnit theories cannot enumerate data that only exists after the checker has run, so
        /// the whole corpus is walked here and every disagreement is collected before failing.
        /// </summary>
        [Fact]
        public void DifferentialCorpusAgreesWithTheReferenceChecker()
        {
            CorpusRun run = Corpus.Value;
            if (run.UnavailableReason != null)
            {
                Assert.False(CorpusIsRequired(),
                    "The independent reference corpus was required but could not run. "
                        + run.UnavailableReason);
                return;
            }

            var failures = new List<string>();
            foreach (JsonElement expected in run.Cases)
            {
                try
                {
                    CheckCase(expected, run.LossSets);
                }
                catch (Exception e)
                {
                    failures.Add(e.Message);
                }
            }

            Assert.True(failures.Count == 0,
                failures.Count + " of " + run.Cases.Count + " differential corpus case(s) "
                    + "disagreed with the independent reference checker:"
                    + Environment.NewLine + "  - "
                    + string.Join(Environment.NewLine + "  - ", failures.Take(20)));
        }

        [Fact]
        public void CorpusCoversTheRequiredShapes()
        {
            CorpusRun run = Corpus.Value;
            if (run.UnavailableReason != null)
            {
                Assert.False(CorpusIsRequired(), run.UnavailableReason);
                return;
            }

            Assert.Contains(run.Cases, c => c.GetProperty("r").GetInt32() == 0);
            Assert.Contains(run.Cases, c => c.GetProperty("k").GetInt32() == 1);
            Assert.Contains(run.Cases, c => c.GetProperty("r").GetInt32() == 1);
            Assert.Contains(run.Cases, c => c.GetProperty("n1").GetInt32() == 7);
            Assert.Contains(run.Cases, c => c.GetProperty("symbolLen").GetInt32() % 2 == 1);
            Assert.Contains(run.Cases, c => !c.GetProperty("complete").GetBoolean());
            Assert.Contains(run.Cases,
                c => c.GetProperty("complete").GetBoolean()
                    && !c.GetProperty("peelingSuffices").GetBoolean());
        }

        private static void CheckCase(JsonElement expected, IReadOnlyDictionary<string, int[]> lossSets)
        {
            string name = expected.GetProperty("name").GetString();
            int k = expected.GetProperty("k").GetInt32();
            int r = expected.GetProperty("r").GetInt32();
            int n1 = expected.GetProperty("n1").GetInt32();
            int seed = expected.GetProperty("seed").GetInt32();
            int symbolLength = expected.GetProperty("symbolLen").GetInt32();
            ulong payloadSeed = expected.GetProperty("payloadSeed").GetUInt64();
            int n = k + r;

            LdpcStaircaseCodec codec = LdpcStaircaseCodec.Create(k, r, seed, n1);
            Expect(name, "matrix entry count",
                expected.GetProperty("matrixEntryCount").GetInt32(), codec.ParityCheckMatrix.EntryCount);
            Expect(name, "canonical matrix serialization",
                expected.GetProperty("matrixSha256").GetString(), Sha256(codec.ParityCheckMatrix.Serialize()));

            var codeword = new byte[n][];
            for (int esi = 0; esi < k; esi++)
            {
                codeword[esi] = CorpusSymbol(payloadSeed, esi, symbolLength);
            }
            for (int esi = k; esi < n; esi++)
            {
                codeword[esi] = new byte[symbolLength];
            }
            codec.Encode(codeword);

            Expect(name, "source symbols",
                expected.GetProperty("sourceSha256").GetString(), Sha256(Concat(codeword, 0, k)));
            Expect(name, "repair symbols",
                expected.GetProperty("repairSha256").GetString(), Sha256(Concat(codeword, k, n)));

            var present = new bool[n];
            for (int i = 0; i < n; i++)
            {
                present[i] = true;
            }
            foreach (int index in lossSets[name])
            {
                present[index] = false;
            }

            var symbols = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                if (present[i])
                {
                    symbols[i] = (byte[])codeword[i].Clone();
                }
            }

            // Decode mutates symbolPresent, marking everything it recovers, so the original
            // loss set is snapshotted first.
            var wasMissing = new bool[n];
            for (int i = 0; i < n; i++)
            {
                wasMissing[i] = !present[i];
            }

            DecodeResult result = codec.Decode(symbols, present);

            Expect(name, "completion flag",
                expected.GetProperty("complete").GetBoolean(), result.IsComplete);
            Expect(name, "recovered symbol count",
                expected.GetProperty("recoveredCount").GetInt32(), result.RecoveredSymbolCount);
            Expect(name, "source indices still missing",
                Describe(IntArray(expected.GetProperty("missingSourceAfter"))),
                Describe(result.MissingSourceIndices()));

            // The checker has no notion of stages, but "did peeling alone finish?" follows from
            // the mathematics, and the profile only reaches ResidualSolve when it did not.
            bool peelingSufficed = result.Stage != DecodeStage.ResidualSolve;
            Expect(name, "whether degree-one peeling alone completed",
                expected.GetProperty("peelingSuffices").GetBoolean(), peelingSufficed);

            var recoveredSource = new List<int>();
            var recoveredRepair = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!wasMissing[i] || symbols[i] == null)
                {
                    continue;
                }

                (i < k ? recoveredSource : recoveredRepair).Add(i);
                Expect(name, "bytes of recovered symbol " + i,
                    Convert.ToHexString(codeword[i]), Convert.ToHexString(symbols[i]));
            }

            Expect(name, "recovered source indices",
                Describe(IntArray(expected.GetProperty("recoveredSource"))), Describe(recoveredSource));
            Expect(name, "recovered repair indices",
                Describe(IntArray(expected.GetProperty("recoveredRepair"))), Describe(recoveredRepair));

            // Unrecovered source symbols contribute symbolLen zero bytes, so a decode that
            // silently left a hole cannot produce the same digest as one that filled it.
            var recoveredSourceBytes = new byte[k * symbolLength];
            for (int i = 0; i < k; i++)
            {
                if (symbols[i] != null)
                {
                    Array.Copy(symbols[i], 0, recoveredSourceBytes, i * symbolLength, symbolLength);
                }
            }
            Expect(name, "recovered source bytes",
                expected.GetProperty("recoveredSourceSha256").GetString(), Sha256(recoveredSourceBytes));
        }

        /// <summary>
        /// The corpus payload rule, documented in the specification repository's
        /// <c>reference/README.md</c>: one independent SplitMix64 stream per symbol, keyed by the
        /// case payload seed and the symbol's ESI, emitted as little-endian 64-bit words and
        /// truncated to <c>S</c> bytes. Corpus test data only; not part of the normative codec.
        /// </summary>
        private static byte[] CorpusSymbol(ulong payloadSeed, int esi, int symbolLength)
        {
            ulong state = payloadSeed ^ ((ulong)(esi + 1) * GoldenGamma);
            var output = new byte[symbolLength];
            int offset = 0;
            while (offset < symbolLength)
            {
                state += GoldenGamma;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                for (int byteIndex = 0; byteIndex < 8 && offset < symbolLength; byteIndex++, offset++)
                {
                    output[offset] = (byte)(z >> (8 * byteIndex));
                }
            }

            return output;
        }

        private static CorpusRun RunReferenceChecker()
        {
            string specDir = LocateSpecDir();
            if (specDir == null)
            {
                return CorpusRun.Unavailable(
                    "The external/ldpc-staircase-spec submodule was not found.");
            }

            string checker = Path.Combine(specDir, "reference", "ldpc_staircase_reference.py");
            string corpus = Path.Combine(specDir, "reference", "corpus.json");
            if (!File.Exists(checker) || !File.Exists(corpus))
            {
                return CorpusRun.Unavailable(
                    "The reference checker is not present at " + checker
                        + ". Update the external/ldpc-staircase-spec submodule to a revision that"
                        + " contains reference/ (specification decision 0002).");
            }

            string interpreter = FindPython();
            if (interpreter == null)
            {
                return CorpusRun.Unavailable("No Python 3.11+ interpreter was found on PATH.");
            }

            string results = Path.Combine(AppContext.BaseDirectory, "reference-corpus",
                "corpus-results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(results));

            (int exitCode, string output) = Run(interpreter, checker, "emit-corpus", corpus,
                "--out", results);
            Assert.True(exitCode == 0,
                "The reference checker failed with exit code " + exitCode + ": " + output);

            using var document = JsonDocument.Parse(File.ReadAllText(results));
            JsonElement root = document.RootElement.Clone();
            Assert.True(root.GetProperty("resultVersion").GetInt32() == 1, "unexpected resultVersion");

            var cases = root.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToList();
            Assert.True(cases.Count > 0, "the differential corpus is empty");

            using var definition = JsonDocument.Parse(File.ReadAllText(corpus));
            var lossSets = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (JsonElement testCase in definition.RootElement.GetProperty("cases").EnumerateArray())
            {
                lossSets[testCase.GetProperty("name").GetString()] =
                    IntArray(testCase.GetProperty("missing"));
            }

            return new CorpusRun(cases, lossSets, null);
        }

        /// <summary>Walks up from the test assembly looking for the spec submodule.</summary>
        private static string LocateSpecDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "external", "ldpc-staircase-spec");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static string FindPython()
        {
            string configured = Environment.GetEnvironmentVariable("LDPC_PYTHON");
            if (!string.IsNullOrEmpty(configured) && IsSupportedPython(configured))
            {
                return configured;
            }

            foreach (string candidate in new[] { "python3", "python", "py" })
            {
                if (IsSupportedPython(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Accepts an interpreter only if it really is Python 3.11 or newer.</summary>
        private static bool IsSupportedPython(string executable)
        {
            try
            {
                (int exitCode, string output) = Run(executable, "-c",
                    "import sys; print(sys.version_info[0], sys.version_info[1])");
                if (exitCode != 0)
                {
                    return false;
                }

                string[] parts = output.Trim().Split(
                    (char[])null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 2
                    && int.Parse(parts[0], CultureInfo.InvariantCulture) == 3
                    && int.Parse(parts[1], CultureInfo.InvariantCulture) >= 11;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static (int ExitCode, string Output) Run(string executable, params string[] arguments)
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info);
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                process.Kill(true);
                throw new TimeoutException("The reference checker did not finish within ten minutes");
            }

            return (process.ExitCode, output);
        }

        private static bool CorpusIsRequired()
        {
            string value = Environment.GetEnvironmentVariable("LDPC_REFERENCE_CORPUS_REQUIRED");
            return !string.IsNullOrEmpty(value) && value != "0";
        }

        private static void Expect<T>(string caseName, string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Xunit.Sdk.XunitException(
                    caseName + ": " + what + ": expected " + expected + ", got " + actual);
            }
        }

        private static string Describe(IEnumerable<int> values) =>
            "[" + string.Join(",", values) + "]";

        private static int[] IntArray(JsonElement array) =>
            array.EnumerateArray().Select(e => e.GetInt32()).ToArray();

        private static byte[] Concat(byte[][] symbols, int from, int to)
        {
            var output = new List<byte>();
            for (int i = from; i < to; i++)
            {
                output.AddRange(symbols[i]);
            }

            return output.ToArray();
        }

        private static string Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder(64);
            foreach (byte value in sha.ComputeHash(data))
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>One run of the reference checker, or the reason it could not run.</summary>
        private sealed class CorpusRun
        {
            internal CorpusRun(IReadOnlyList<JsonElement> cases,
                IReadOnlyDictionary<string, int[]> lossSets, string unavailableReason)
            {
                Cases = cases;
                LossSets = lossSets;
                UnavailableReason = unavailableReason;
            }

            internal IReadOnlyList<JsonElement> Cases { get; }

            internal IReadOnlyDictionary<string, int[]> LossSets { get; }

            internal string UnavailableReason { get; }

            internal static CorpusRun Unavailable(string reason) =>
                new CorpusRun(Array.Empty<JsonElement>(),
                    new Dictionary<string, int[]>(StringComparer.Ordinal), reason);
        }
    }
}
