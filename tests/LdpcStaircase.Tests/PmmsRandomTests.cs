using LdpcStaircase;
using Xunit;

namespace LdpcStaircase.Tests
{
    /// <summary>Covers <see cref="PmmsRandom"/> against RFC 5170 section 5.7 / SPEC.md section 3.</summary>
    public sealed class PmmsRandomTests
    {
        /// <summary>
        /// The RFC's own validation criterion, and the single most important assertion in this
        /// repository: if this holds, an independent implementation of the same recurrence walks
        /// the identical stream, and therefore builds the identical graph.
        /// </summary>
        [Fact]
        public void Rfc5170ValidationCriterion()
        {
            var rng = new PmmsRandom(1);
            int value = 0;
            for (int i = 0; i < 10_000; i++)
            {
                value = rng.Next();
            }

            Assert.Equal(1043618065, value);
        }

        [Fact]
        public void FirstValuesFollowTheRecurrence()
        {
            var rng = new PmmsRandom(1);
            Assert.Equal(16807, rng.Next());
            Assert.Equal(282475249, rng.Next());
            Assert.Equal(1622650073, rng.Next());
        }

        [Fact]
        public void StateStaysInRange()
        {
            var rng = new PmmsRandom(PmmsRandom.MaxSeed);
            for (int i = 0; i < 100_000; i++)
            {
                int value = rng.Next();
                Assert.InRange(value, 1, PmmsRandom.MaxSeed);
            }
        }

        [Fact]
        public void BoundedDrawsStayInRange()
        {
            var rng = new PmmsRandom(12345);
            for (int i = 0; i < 100_000; i++)
            {
                int maxv = 1 + (i % 1000);
                Assert.InRange(rng.NextBounded(maxv), 0, maxv - 1);
            }
        }

        [Fact]
        public void BoundOfOneIsAlwaysZero()
        {
            var rng = new PmmsRandom(7);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(0, rng.NextBounded(1));
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void RejectsOutOfRangeSeeds(int seed)
        {
            Assert.Throws<LdpcStaircaseException>(() => new PmmsRandom(seed));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void RejectsNonPositiveBounds(int maxv)
        {
            var rng = new PmmsRandom(1);
            Assert.Throws<LdpcStaircaseException>(() => rng.NextBounded(maxv));
        }

        [Fact]
        public void SameSeedGivesTheSameStream()
        {
            var a = new PmmsRandom(999);
            var b = new PmmsRandom(999);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(a.Next(), b.Next());
            }
        }
    }
}
