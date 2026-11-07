using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DoorMan.Crypto;

/// <summary>
/// The data key of a folder together with its “envelope”: the same key encrypted with another one derived from the
/// password (PBKDF2-SHA256 + AES-256-GCM). The envelope is stored in the vault header and lets the folder be locked
/// again with the same password without asking for it.
/// </summary>
internal sealed class KeyMaterial : IDisposable
{
    public const int DefaultIterations = 600_000;
    public const int KeySize = 32;
    const int SaltSize = 16;
    const int NonceSize = 12;
    const int TagSize = 16;

    // iterations (4) | salt (16) | nonce (12) | encrypted key (32) | tag (16)
    public const int EnvelopeSize = 4 + SaltSize + NonceSize + KeySize + TagSize;
    const int AadSize = 4 + SaltSize;

    public byte[] Envelope { get; }
    public byte[] DataKey { get; }

    KeyMaterial(byte[] envelope, byte[] dataKey)
    {
        Envelope = envelope;
        DataKey = dataKey;
    }

    public static KeyMaterial Create(string password, int iterations = DefaultIterations)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        var envelope = new byte[EnvelopeSize];
        BinaryPrimitives.WriteInt32LittleEndian(envelope, iterations);
        RandomNumberGenerator.Fill(envelope.AsSpan(4, SaltSize));
        RandomNumberGenerator.Fill(envelope.AsSpan(AadSize, NonceSize));

        var kek = DeriveKek(password, envelope.AsSpan(4, SaltSize), iterations);
        try
        {
            using var gcm = new AesGcm(kek, TagSize);
            gcm.Encrypt(
                envelope.AsSpan(AadSize, NonceSize),
                dataKey,
                envelope.AsSpan(AadSize + NonceSize, KeySize),
                envelope.AsSpan(AadSize + NonceSize + KeySize, TagSize),
                envelope.AsSpan(0, AadSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
        return new KeyMaterial(envelope, dataKey);
    }

    /// <exception cref="WrongPasswordException">The password does not open the envelope.</exception>
    public static KeyMaterial Open(ReadOnlySpan<byte> envelope, string password)
    {
        if (envelope.Length != EnvelopeSize)
            throw new InvalidDataException(Strings.VaultInvalid);

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope);
        if (iterations < 10_000 || iterations > 50_000_000)
            throw new InvalidDataException(Strings.VaultInvalid);

        var kek = DeriveKek(password, envelope.Slice(4, SaltSize), iterations);
        var dataKey = new byte[KeySize];
        try
        {
            using var gcm = new AesGcm(kek, TagSize);
            gcm.Decrypt(
                envelope.Slice(AadSize, NonceSize),
                envelope.Slice(AadSize + NonceSize, KeySize),
                envelope.Slice(AadSize + NonceSize + KeySize, TagSize),
                dataKey,
                envelope[..AadSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongPasswordException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
        return new KeyMaterial(envelope.ToArray(), dataKey);
    }

    /// <summary>Rebuilds the key from an envelope and a data key that were already verified.</summary>
    public static KeyMaterial FromParts(byte[] envelope, byte[] dataKey)
    {
        if (envelope.Length != EnvelopeSize || dataKey.Length != KeySize)
            throw new InvalidDataException("The stored key is not valid.");
        return new KeyMaterial(envelope, dataKey);
    }

    static byte[] DeriveKek(string password, ReadOnlySpan<byte> salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(DataKey);
}

internal sealed class WrongPasswordException() : Exception(Strings.WrongPassword);
