using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CarpetaProtegida.Crypto;

/// <summary>
/// Clave de datos de una carpeta junto con su «sobre»: la misma clave cifrada con otra
/// derivada de la contraseña (PBKDF2-SHA256 + AES-256-GCM). El sobre se guarda en la
/// cabecera de la bóveda y permite volver a bloquear con la misma contraseña sin pedirla.
/// </summary>
internal sealed class KeyMaterial : IDisposable
{
    public const int DefaultIterations = 600_000;
    public const int KeySize = 32;
    const int SaltSize = 16;
    const int NonceSize = 12;
    const int TagSize = 16;

    // iteraciones (4) | sal (16) | nonce (12) | clave cifrada (32) | etiqueta (16)
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

    /// <exception cref="WrongPasswordException">La contraseña no abre el sobre.</exception>
    public static KeyMaterial Open(ReadOnlySpan<byte> envelope, string password)
    {
        if (envelope.Length != EnvelopeSize)
            throw new InvalidDataException("La cabecera de la carpeta protegida no es válida.");

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope);
        if (iterations < 10_000 || iterations > 50_000_000)
            throw new InvalidDataException("La cabecera de la carpeta protegida no es válida.");

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

    /// <summary>Reconstruye la clave a partir de un sobre y una clave de datos ya verificados.</summary>
    public static KeyMaterial FromParts(byte[] envelope, byte[] dataKey)
    {
        if (envelope.Length != EnvelopeSize || dataKey.Length != KeySize)
            throw new InvalidDataException("La clave guardada no es válida.");
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

internal sealed class WrongPasswordException() : Exception("La contraseña no es correcta.");
