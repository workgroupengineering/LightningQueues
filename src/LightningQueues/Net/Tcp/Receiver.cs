using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LightningQueues.Net.Tcp;

public class Receiver : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IReceivingProtocol _protocol;
    private readonly ILogger _logger;
    private bool _disposed;
    private readonly Uri _localUri;
    private readonly object _lockObject;
        
    public Receiver(IPEndPoint endpoint, IReceivingProtocol protocol, ILogger logger)
    {
        Endpoint = endpoint;
        _localUri = new Uri($"lq://localhost:{Endpoint.Port}");
        _protocol = protocol;
        _logger = logger;
        _listener = new TcpListener(Endpoint);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _lockObject = new object();
    }

    public IPEndPoint Endpoint { get; }

    public async ValueTask StartReceivingAsync(ChannelWriter<Message> receivedChannel, CancellationToken cancellationToken = default)
    {
        StartListener();
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            var socket = await _listener.AcceptSocketAsync(cancellationToken);
            await using var stream = new NetworkStream(socket, false);
            var messages = _protocol.ReceiveMessagesAsync(socket.RemoteEndPoint, stream, cancellationToken);
            var messageEnumerator = messages.GetAsyncEnumerator(cancellationToken);
            var hasResult = true;
            while (hasResult)
            {
                try
                {
                    hasResult = await messageEnumerator.MoveNextAsync();
                    var msg = hasResult ? messageEnumerator.Current : null;
                    if (msg != null)
                        await receivedChannel.WriteAsync(msg, cancellationToken);
                }
                catch (Exception ex)
                {
                    if(_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError("Error reading messages", ex);
                }
            }
        }
    }

    private void StartListener()
    {
        lock (_lockObject)
        {
            _listener.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        
        if(_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Disposing TcpListener at {Port}", Endpoint.Port);
        _disposed = true;
        _listener.Stop();
        GC.SuppressFinalize(this);
    }
}