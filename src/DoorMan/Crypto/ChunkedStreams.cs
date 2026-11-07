using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DoorMan.Crypto;

/// <summary>
/// Chunked stream format: each chunk is <c>final flag (1) | length (4) | ciphertext | GCM tag (16)</c>.
/// The nonce holds the chunk number and the final flag, so reordering, removing or truncating chunks makes
/// authentication fail.
/// </summary>
internal static class ChunkFormat
{
    public const int ChunkSize = 1 << 20;
    public const int TagSize = 16;
    public const int HeaderSize = 5;

    public static void BuildNonce(Span<byte> nonce, long counter, bool final)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce.Slice(3, 8), counter);
        nonce[11] = final ? (byte)1 : (byte)0;
    }
}

/// <summary>Write-only stream that encrypts in chunks. <see cref="Complete"/> must be called at the end.</summary>
internal sealed class EncryptingStream : Stream
{
    readonly Stream _inner;
    readonly AesGcm _gcm;
    readonly byte[] _aad;
    readonly byte[] _buffer = new byte[ChunkFormat.ChunkSize];
    readonly byte[] _cipher = new byte[ChunkFormat.ChunkSize];
    readonly byte[] _tag = new byte[ChunkFormat.TagSize];
    readonly byte[] _nonce = new byte[12];
    readonly byte[] _header = new byte[ChunkFormat.HeaderSize];
    int _count;
    long _counter;
    bool _completed;

    public EncryptingStream(Stream inner, byte[] key, byte[] aad)
    {
        _inner = inner;
        _gcm = new AesGcm(key, ChunkFormat.TagSize);
        _aad = aad;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        while (buffer.Length > 0)
        {
            // A full chunk is only written when more data arrives: that way the last one always carries the final flag.
            if (_count == _buffer.Length)
                WriteChunk(final: false);
            int n = Math.Min(_buffer.Length - _count, buffer.Length);
            buffer[..n].CopyTo(_buffer.AsSpan(_count));
            _count += n;
            buffer = buffer[n..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value) => Write([value]);

    public void Complete()
    {
        if (_completed)
            return;
        WriteChunk(final: true);
        _completed = true;
        _inner.Flush();
    }

    void WriteChunk(bool final)
    {
        ChunkFormat.BuildNonce(_nonce, _counter, final);
        _header[0] = final ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(1), _count);
        _gcm.Encrypt(_nonce, _buffer.AsSpan(0, _count), _cipher.AsSpan(0, _count), _tag, _aad);
        _inner.Write(_header);
        _inner.Write(_cipher, 0, _count);
        _inner.Write(_tag);
        _counter++;
        _count = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gcm.Dispose();
            CryptographicOperations.ZeroMemory(_buffer);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_completed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Read-only stream that decrypts and authenticates each chunk before handing it over.</summary>
internal sealed class DecryptingStream : Stream
{
    readonly Stream _inner;
    readonly AesGcm _gcm;
    readonly byte[] _aad;
    readonly byte[] _plain = new byte[ChunkFormat.ChunkSize];
    readonly byte[] _cipher = new byte[ChunkFormat.ChunkSize];
    readonly byte[] _tag = new byte[ChunkFormat.TagSize];
    readonly byte[] _nonce = new byte[12];
    readonly byte[] _header = new byte[ChunkFormat.HeaderSize];
    int _pos;
    int _len;
    long _counter;
    bool _finished;

    public DecryptingStream(Stream inner, byte[] key, byte[] aad)
    {
        _inner = inner;
        _gcm = new AesGcm(key, ChunkFormat.TagSize);
        _aad = aad;
    }

    public override int Read(Span<byte> buffer)
    {
        while (_pos == _len)
        {
            if (_finished)
                return 0;
            ReadChunk();
        }
        int n = Math.Min(buffer.Length, _len - _pos);
        _plain.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    void ReadChunk()
    {
        try
        {
            _inner.ReadExactly(_header);
            byte flag = _header[0];
            int len = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(1));
            bool final = flag == 1;
            if (flag > 1 || len < 0 || len > ChunkFormat.ChunkSize || (!final && len != ChunkFormat.ChunkSize))
                throw Corrupt();

            _inner.ReadExactly(_cipher, 0, len);
            _inner.ReadExactly(_tag);
            ChunkFormat.BuildNonce(_nonce, _counter, final);
            _gcm.Decrypt(_nonce, _cipher.AsSpan(0, len), _tag, _plain.AsSpan(0, len), _aad);

            _counter++;
            _pos = 0;
            _len = len;
            _finished = final;
            if (final && _inner.ReadByte() != -1)
                throw Corrupt();
        }
        catch (EndOfStreamException)
        {
            throw Corrupt();
        }
        catch (AuthenticationTagMismatchException)
        {
            throw Corrupt();
        }
    }

    static InvalidDataException Corrupt() => new(Strings.VaultDamaged);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gcm.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
