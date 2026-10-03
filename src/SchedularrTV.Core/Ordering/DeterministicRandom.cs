using System.Numerics;
using System.Text;

namespace SchedularrTV.Core.Ordering;

/// <summary>
/// xoshiro256** seeded via splitmix64. Implemented in-repo rather than using <see cref="System.Random"/>
/// because schedule reproducibility must survive runtime upgrades and process boundaries.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public DeterministicRandom(ulong seed)
    {
        _s0 = SplitMix64(ref seed);
        _s1 = SplitMix64(ref seed);
        _s2 = SplitMix64(ref seed);
        _s3 = SplitMix64(ref seed);
    }

    private DeterministicRandom(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    public static DeterministicRandom FromSeedText(params ReadOnlySpan<string> parts)
    {
        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            sb.Append(part).Append('\u001f');
        }

        return new DeterministicRandom(Hash(sb.ToString()));
    }

    /// <summary>FNV-1a 64. Stable by definition, unlike <see cref="string.GetHashCode()"/>.</summary>
    public static ulong Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Uniform value in [0, exclusiveMax).</summary>
    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 1)
        {
            return 0;
        }

        // Lemire's debiased bounded generation.
        ulong bound = (ulong)exclusiveMax;
        ulong threshold = (0UL - bound) % bound;
        while (true)
        {
            ulong r = NextUInt64();
            if (r >= threshold)
            {
                return (int)(r % bound);
            }
        }
    }

    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public string Serialize() => $"{_s0:x16}{_s1:x16}{_s2:x16}{_s3:x16}";

    public static DeterministicRandom Deserialize(string? state, ulong fallbackSeed)
    {
        if (state is not { Length: 64 })
        {
            return new DeterministicRandom(fallbackSeed);
        }

        try
        {
            return new DeterministicRandom(
                Convert.ToUInt64(state[..16], 16),
                Convert.ToUInt64(state[16..32], 16),
                Convert.ToUInt64(state[32..48], 16),
                Convert.ToUInt64(state[48..], 16));
        }
        catch (FormatException)
        {
            return new DeterministicRandom(fallbackSeed);
        }
    }
}
