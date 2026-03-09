using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace maildot.Services;

public sealed class PostgresLargeObjectStore(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;

    private readonly NpgsqlConnection _connection = connection;
    private readonly NpgsqlTransaction _transaction = transaction;

    public Task<uint> CreateAsync(CancellationToken cancellationToken) =>
        ExecuteScalarUInt32Async("select lo_create(0)", cancellationToken);

    public async Task<Stream> OpenReadAsync(uint oid, CancellationToken cancellationToken)
    {
        var fd = await OpenAsync(oid, InvRead, cancellationToken);
        return new LargeObjectStream(_connection, _transaction, fd, canRead: true, canWrite: false);
    }

    public async Task<Stream> OpenReadWriteAsync(uint oid, CancellationToken cancellationToken)
    {
        var fd = await OpenAsync(oid, InvRead | InvWrite, cancellationToken);
        return new LargeObjectStream(_connection, _transaction, fd, canRead: true, canWrite: true);
    }

    public async Task UnlinkAsync(uint oid, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("select lo_unlink(@oid)");
        command.Parameters.AddWithValue("oid", (int)oid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> OpenAsync(uint oid, int mode, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("select lo_open(@oid, @mode)");
        command.Parameters.AddWithValue("oid", (int)oid);
        command.Parameters.AddWithValue("mode", mode);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return ConvertToInt32(result, $"Failed to open PostgreSQL large object {oid}.");
    }

    private async Task<uint> ExecuteScalarUInt32Async(string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return ConvertToUInt32(result, $"Unexpected result for SQL '{sql}'.");
    }

    private NpgsqlCommand CreateCommand(string sql) => new(sql, _connection, _transaction);

    private sealed class LargeObjectStream(NpgsqlConnection connection, NpgsqlTransaction transaction, int descriptor, bool canRead, bool canWrite) : Stream
    {
        private readonly NpgsqlConnection _connection = connection;
        private readonly NpgsqlTransaction _transaction = transaction;
        private readonly int _descriptor = descriptor;
        private readonly bool _canRead = canRead;
        private readonly bool _canWrite = canWrite;
        private bool _disposed;

        public override bool CanRead => !_disposed && _canRead;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && _canWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_canRead)
            {
                throw new NotSupportedException();
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            await using var command = new NpgsqlCommand("select loread(@fd, @len)", _connection, _transaction);
            command.Parameters.AddWithValue("fd", _descriptor);
            command.Parameters.AddWithValue("len", buffer.Length);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var bytes = ConvertToBytes(result);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_canWrite)
            {
                throw new NotSupportedException();
            }

            if (buffer.Length == 0)
            {
                return;
            }

            byte[] payload;
            if (MemoryMarshal.TryGetArray(buffer, out var segment) &&
                segment.Offset == 0 &&
                segment.Count == segment.Array?.Length)
            {
                payload = segment.Array;
            }
            else
            {
                payload = buffer.ToArray();
            }

            await using var command = new NpgsqlCommand("select lowrite(@fd, @data)", _connection, _transaction);
            command.Parameters.AddWithValue("fd", _descriptor);
            command.Parameters.AddWithValue("data", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            using var command = new NpgsqlCommand("select lo_close(@fd)", _connection, _transaction);
            command.Parameters.AddWithValue("fd", _descriptor);
            command.ExecuteNonQuery();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await using var command = new NpgsqlCommand("select lo_close(@fd)", _connection, _transaction);
            command.Parameters.AddWithValue("fd", _descriptor);
            await command.ExecuteNonQueryAsync();
            await base.DisposeAsync();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static int ConvertToInt32(object? value, string message)
    {
        try
        {
            return value switch
            {
                null or DBNull => throw new InvalidOperationException(message),
                int intValue => intValue,
                _ => Convert.ToInt32(value)
            };
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(message, ex);
        }
    }

    private static uint ConvertToUInt32(object? value, string message)
    {
        try
        {
            return value switch
            {
                null or DBNull => throw new InvalidOperationException(message),
                uint uintValue => uintValue,
                int intValue when intValue >= 0 => (uint)intValue,
                long longValue when longValue >= 0 && longValue <= uint.MaxValue => (uint)longValue,
                _ => Convert.ToUInt32(value)
            };
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(message, ex);
        }
    }

    private static byte[] ConvertToBytes(object? value) =>
        value switch
        {
            null or DBNull => [],
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            ArraySegment<byte> segment => segment.ToArray(),
            _ => throw new InvalidOperationException($"Unexpected large object read result type: {value.GetType().FullName}")
        };
}
