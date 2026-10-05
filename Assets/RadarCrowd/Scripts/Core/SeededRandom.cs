using System;

namespace RadarCrowd
{
    /// <summary>
    /// Deterministic random stream. Every component owns its own stream, seeded from the run seed and a component
    /// name, so adding or removing one component never shifts the random numbers drawn by another.
    /// </summary>
    public sealed class SeededRandom
    {
        readonly Random rng;

        public SeededRandom(int seed)
        {
            rng = new Random(seed);
        }

        public static SeededRandom For(int runSeed, string component)
        {
            return new SeededRandom(Derive(runSeed, component));
        }

        /// <summary>Stable seed derivation: FNV-1a hash of the component name mixed with the run seed (SplitMix64).</summary>
        public static int Derive(int runSeed, string component)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in component)
                {
                    h ^= c;
                    h *= 16777619;
                }
                ulong z = ((ulong)(uint)runSeed << 32) | h;
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (int)(z & 0x7FFFFFFF);
            }
        }

        public double Uniform() => rng.NextDouble();

        public float Range(float min, float max) => min + (float)(rng.NextDouble() * (max - min));

        public int Index(int count) => rng.Next(count);

        public bool Bernoulli(double p) => rng.NextDouble() < p;

        public double Normal(double mean, double sd)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        public double TruncatedNormal(double mean, double sd, double min, double max)
        {
            for (int i = 0; i < 100; i++)
            {
                double x = Normal(mean, sd);
                if (x >= min && x <= max)
                    return x;
            }
            return Math.Min(max, Math.Max(min, mean));
        }

        public double Exponential(double mean) => -mean * Math.Log(1.0 - rng.NextDouble());

        /// <summary>Poisson draw (Knuth's method; adequate for the small means used per radar frame).</summary>
        public int Poisson(double mean)
        {
            if (mean <= 0.0)
                return 0;
            double limit = Math.Exp(-mean);
            double p = 1.0;
            int k = 0;
            do
            {
                k++;
                p *= rng.NextDouble();
            } while (p > limit);
            return k - 1;
        }
    }
}
