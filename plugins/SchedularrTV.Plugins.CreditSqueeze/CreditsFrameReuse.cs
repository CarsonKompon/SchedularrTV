using System.Security.Cryptography;
using SkiaSharp;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Reuse static scans without overlooking a changed name. Near matches compare every decoded pixel, not a thumbnail.</summary>
public sealed class CreditsFrameReuse
{
    private readonly Dictionary<string, int> _hashes = new(StringComparer.Ordinal);
    private byte[]? _representative;
    private int _width, _height, _previous = -1;
    public List<string> Unique { get; } = [];
    public List<bool> Blank { get; } = [];

    public async Task<int> AddAsync(string path, CancellationToken cancellationToken)
    {
        byte[] encoded = await File.ReadAllBytesAsync(path, cancellationToken);
        string hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
        if (_hashes.TryGetValue(hash, out int existing))
        {
            if (_previous != existing) Remember(existing);
            return existing;
        }
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is not null)
        {
            byte[] pixels = bitmap.GetPixelSpan().ToArray();
            if (_representative is not null && bitmap.Width == _width && bitmap.Height == _height && NearIdentical(_representative, pixels))
            {
                _hashes[hash] = _previous;
                return _previous; // keep the original representative: fades must not chain into a different card
            }
            _representative = pixels; _width = bitmap.Width; _height = bitmap.Height;
        }
        else _representative = null;
        _previous = Unique.Count;
        Unique.Add(path);
        Blank.Add(bitmap is not null && _representative is not null && Uniform(_representative, bitmap.Info.BytesPerPixel));
        _hashes[hash] = _previous;
        return _previous;
    }

    private void Remember(int index)
    {
        using var bitmap = SKBitmap.Decode(Unique[index]);
        _representative = bitmap is null ? null : bitmap.GetPixelSpan().ToArray();
        _width = bitmap?.Width ?? 0; _height = bitmap?.Height ?? 0; _previous = index;
    }

    public static bool NearIdentical(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (Math.Abs(left[index] - right[index]) > 4) return false;
        return true;
    }

    private static bool Uniform(ReadOnlySpan<byte> pixels, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0 || pixels.Length < bytesPerPixel) return false;
        for (int index = bytesPerPixel; index < pixels.Length; index++)
            if (Math.Abs(pixels[index] - pixels[index % bytesPerPixel]) > 3) return false;
        return true;
    }
}
