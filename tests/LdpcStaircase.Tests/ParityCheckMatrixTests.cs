using System;
using System.Collections.Generic;
using System.Linq;
using LdpcStaircase;
using Xunit;

namespace LdpcStaircase.Tests
{
    /// <summary>
    /// Structural and determinism properties of <see cref="ParityCheckMatrix"/>
    /// (SPEC.md section 4).
    /// </summary>
    public sealed class ParityCheckMatrixTests
    {
        [Theory]
        [InlineData(16, 8, 3, 1)]
        [InlineData(64, 32, 7, 42)]
        [InlineData(100, 50, 7, 2147483646)]
        [InlineData(37, 11, 5, 999)]
        [InlineData(250, 25, 7, 123456)]
        public void StructureFollowsTheSpec(int k, int r, int n1, int seed)
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(k, r, n1, seed);

            Assert.Equal(k, h.SourceSymbolCount);
            Assert.Equal(r, h.RepairSymbolCount);
            Assert.Equal(k + r, h.EncodingSymbolCount);
            Assert.Equal(n1, h.LeftDegree);
            Assert.Equal(seed, h.Seed);

            // Section 4.2: every source column carries at least N1 ones (section 4.3 may add
            // more when it repairs a low-degree row).
            for (int j = 0; j < k; j++)
            {
                Assert.True(h.ColumnDegree(j) >= n1,
                    $"source column {j} has degree {h.ColumnDegree(j)} < N1={n1}");
            }

            // Section 4.4: repair column K+p appears in equations p and p+1, so every repair
            // column has degree 2 except the last, which has only its own equation.
            for (int p = 0; p < r; p++)
            {
                Assert.Equal(p == r - 1 ? 1 : 2, h.ColumnDegree(k + p));
            }

            // Section 4.3 plus the staircase: no equation may be trivially weak.
            for (int i = 0; i < r; i++)
            {
                Assert.True(h.RowDegree(i) >= 3,
                    $"row {i} has degree {h.RowDegree(i)}; expected >= 2 left + staircase");
            }

            for (int i = 0; i < r; i++)
            {
                AssertAscendingAndUnique(h.RowColumns(i), $"row {i}");
            }

            for (int c = 0; c < k + r; c++)
            {
                AssertAscendingAndUnique(h.ColumnRows(c), $"column {c}");
            }

            // The row view and the column view describe the same set of ones.
            Assert.Equal(EntriesByRow(h), EntriesByColumn(h));
            Assert.Equal(EntriesByRow(h).Count, h.EntryCount);
        }

        /// <summary>
        /// The staircase itself: row 0 holds only repair symbol 0, and row i additionally holds
        /// repair symbol i-1. This is what makes encoding a running XOR.
        /// </summary>
        [Fact]
        public void StaircaseIsExact()
        {
            const int k = 20;
            const int r = 10;
            ParityCheckMatrix h = ParityCheckMatrix.Build(k, r, 3, 5);

            Assert.Equal(new[] { k }, RepairColumnsOf(h, 0));
            for (int i = 1; i < r; i++)
            {
                Assert.Equal(new[] { k + i - 1, k + i }, RepairColumnsOf(h, i));
            }
        }

        [Fact]
        public void SameParametersGiveTheSameMatrix()
        {
            Assert.Equal(
                ParityCheckMatrix.Build(64, 32, 7, 42).Serialize(),
                ParityCheckMatrix.Build(64, 32, 7, 42).Serialize());
        }

        [Fact]
        public void DifferentSeedsGiveDifferentMatrices()
        {
            Assert.NotEqual(
                ParityCheckMatrix.Build(64, 32, 7, 42).Serialize(),
                ParityCheckMatrix.Build(64, 32, 7, 43).Serialize());
        }

        [Fact]
        public void NoRepairYieldsAnEmptyMatrix()
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(8, 0, 7, 1);
            Assert.Equal(0, h.RepairSymbolCount);
            Assert.Equal(0, h.EntryCount);
            Assert.Equal(8, h.EncodingSymbolCount);
            for (int c = 0; c < 8; c++)
            {
                Assert.Equal(0, h.ColumnDegree(c));
            }
        }

        /// <summary>
        /// The degenerate <c>K = 1</c> rule of SPEC.md section 4.3: with only one source column,
        /// rows can be raised to degree 1 and no further.
        /// </summary>
        [Fact]
        public void SingleSourceColumnIsHandled()
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(1, 1, 1, 7);
            Assert.Equal(1, h.ColumnDegree(0));
            Assert.Equal(2, h.RowDegree(0)); // the source symbol plus repair symbol 0
        }

        [Fact]
        public void SerializationMatchesTheDeclaredLayout()
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(16, 8, 3, 1);
            byte[] bytes = h.Serialize();

            Assert.Equal(4 * (2 + h.RepairSymbolCount + h.EntryCount), bytes.Length);
            Assert.Equal(h.RepairSymbolCount, ReadU32(bytes, 0));
            Assert.Equal(h.EncodingSymbolCount, ReadU32(bytes, 4));

            int offset = 8;
            for (int i = 0; i < h.RepairSymbolCount; i++)
            {
                int degree = ReadU32(bytes, offset);
                offset += 4;
                Assert.Equal(h.RowDegree(i), degree);
                int[] columns = h.RowColumns(i);
                for (int idx = 0; idx < degree; idx++)
                {
                    Assert.Equal(columns[idx], ReadU32(bytes, offset));
                    offset += 4;
                }
            }

            Assert.Equal(bytes.Length, offset);
        }

        [Fact]
        public void RejectsInvalidParameters()
        {
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(0, 8, 3, 1));
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(16, -1, 3, 1));
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(16, 8, 0, 1));
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(16, 8, 9, 1));
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(16, 8, 3, 0));
            Assert.Throws<LdpcStaircaseException>(() => ParityCheckMatrix.Build(16, 8, 3, int.MaxValue));
        }

        /// <summary>Accessors must hand out copies so a caller cannot corrupt a shared matrix.</summary>
        [Fact]
        public void AccessorsReturnCopies()
        {
            ParityCheckMatrix h = ParityCheckMatrix.Build(16, 8, 3, 1);

            int[] row = h.RowColumns(0);
            row[0] = -12345;
            Assert.NotEqual(-12345, h.RowColumns(0)[0]);

            int[] column = h.ColumnRows(0);
            column[0] = -12345;
            Assert.NotEqual(-12345, h.ColumnRows(0)[0]);
        }

        private static void AssertAscendingAndUnique(int[] values, string label)
        {
            for (int i = 1; i < values.Length; i++)
            {
                Assert.True(values[i] > values[i - 1], $"{label} is not strictly ascending at index {i}");
            }
        }

        /// <summary>The repair-part columns of <paramref name="row"/>, i.e. its slice of the staircase.</summary>
        private static int[] RepairColumnsOf(ParityCheckMatrix h, int row)
        {
            int k = h.SourceSymbolCount;
            return h.RowColumns(row).Where(c => c >= k).ToArray();
        }

        private static List<string> EntriesByRow(ParityCheckMatrix h)
        {
            var entries = new List<string>();
            for (int i = 0; i < h.RepairSymbolCount; i++)
            {
                foreach (int c in h.RowColumns(i))
                {
                    entries.Add(i + ":" + c);
                }
            }

            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        private static List<string> EntriesByColumn(ParityCheckMatrix h)
        {
            var entries = new List<string>();
            for (int c = 0; c < h.EncodingSymbolCount; c++)
            {
                foreach (int i in h.ColumnRows(c))
                {
                    entries.Add(i + ":" + c);
                }
            }

            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        private static int ReadU32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16)
                | (data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
