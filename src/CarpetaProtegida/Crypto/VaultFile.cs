using System.Security.Cryptography;

namespace CarpetaProtegida.Crypto;

/// <summary>
/// Cabecera de la bóveda: <c>firma (8) | sobre de la clave | id aleatorio (16)</c>.
/// El contenido se cifra con una clave derivada (HKDF) de la clave de datos y del id,
/// de modo que cada nuevo bloqueo con la misma contraseña usa una clave de contenido distinta.
/// </summary>
internal static class VaultFile
{
    static ReadOnlySpan<byte> Magic => "CPBLOQ01"u8;
    const int FileIdSize = 16;
    public const int HeaderSize = 8 + KeyMaterial.EnvelopeSize + FileIdSize;

    public static EncryptingStream OpenWrite(Stream output, KeyMaterial key)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        key.Envelope.CopyTo(header, Magic.Length);
        RandomNumberGenerator.Fill(header.AsSpan(HeaderSize - FileIdSize));
        output.Write(header);
        return new EncryptingStream(output, DeriveContentKey(key, header), header);
    }

    public static byte[] ReadHeader(Stream input)
    {
        var header = new byte[HeaderSize];
        try
        {
            input.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw NotAVault();
        }
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw NotAVault();
        return header;
    }

    public static ReadOnlySpan<byte> GetEnvelope(byte[] header) => header.AsSpan(Magic.Length, KeyMaterial.EnvelopeSize);

    public static DecryptingStream OpenRead(Stream input, byte[] header, KeyMaterial key) =>
        new(input, DeriveContentKey(key, header), header);

    static byte[] DeriveContentKey(KeyMaterial key, byte[] header)
    {
        var contentKey = new byte[KeyMaterial.KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.DataKey, contentKey,
            header.AsSpan(HeaderSize - FileIdSize), "CarpetaProtegida v1 contenido"u8);
        return contentKey;
    }

    static InvalidDataException NotAVault() =>
        new("El archivo de la carpeta protegida no es válido o está dañado.");
}
