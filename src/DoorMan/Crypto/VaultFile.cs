using System.Security.Cryptography;

namespace DoorMan.Crypto;

/// <summary>
/// Vault header: <c>signature (8) | key envelope | random id (16)</c>.
/// The content is encrypted with a key derived (HKDF) from the data key and the id, so every new lock with the same
/// password uses a different content key.
/// </summary>
internal static class VaultFile
{
    static ReadOnlySpan<byte> Magic => "DOORMAN1"u8;
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
            // Part of the file format: changing it would make every existing vault unreadable.
            header.AsSpan(HeaderSize - FileIdSize), "DoorMan v1 contenido"u8);
        return contentKey;
    }

    static InvalidDataException NotAVault() => new(Strings.VaultInvalid);
}
