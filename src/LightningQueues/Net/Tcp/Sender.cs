﻿using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LightningQueues.Storage;

namespace LightningQueues.Net.Tcp;

public class Sender : IDisposable
{
    private readonly ISendingProtocol _protocol;
    private readonly Channel<OutgoingMessageFailure> _failedToSend;
    private readonly ILogger _logger;
    private readonly TimeSpan _sendTimeout;

    public Sender(ISendingProtocol protocol, ILogger logger, TimeSpan sendTimeout)
    {
        _protocol = protocol;
        _logger = logger;
        _sendTimeout = sendTimeout;
        _failedToSend = Channel.CreateUnbounded<OutgoingMessageFailure>();
    }

    public Channel<OutgoingMessageFailure> FailedToSend() => _failedToSend;

    public async ValueTask StartSendingAsync(ChannelReader<Message> outgoing, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batch = await outgoing.ReadBatchAsync(50, TimeSpan.FromMilliseconds(200), cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    break;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(_sendTimeout);
                foreach (var messageGroup in batch.GroupBy(x => x.Destination))
                {
                    var uri = messageGroup.Key;
                    var messages = messageGroup.ToList();
                    try
                    {
                        using var client = new TcpClient();
                        if (uri.IsLoopback || Dns.GetHostName() == uri.Host)
                        {
                            await client.ConnectAsync(IPAddress.Loopback, uri.Port, linked.Token)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await client.ConnectAsync(uri.Host, uri.Port, linked.Token).ConfigureAwait(false);
                        }

                        await _protocol.SendAsync(uri, client.GetStream(), messages, linked.Token)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
                    {
                        _logger.SenderSendingError(uri, ex);
                        var failed = new OutgoingMessageFailure
                        {
                            Messages = messages,
                            ShouldRetry = false
                        };
                        await _failedToSend.Writer.WriteAsync(failed, cancellationToken).ConfigureAwait(false);
                    }
                    catch (QueueDoesNotExistException ex)
                    {
                        _logger.SenderQueueDoesNotExistError(uri, ex);
                    }
                    catch (Exception ex)
                    {
                        _logger.SenderSendingError(uri, ex);
                        var failed = new OutgoingMessageFailure
                        {
                            Messages = messages,
                            ShouldRetry = true
                        };
                        await _failedToSend.Writer.WriteAsync(failed, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.SenderSendingLoopError(ex);
            }
        }
    }

    public void Dispose()
    {
        _logger.SenderDisposing();
        
        try
        {
            // Complete the channel to prevent further sends
            _failedToSend.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Just log and continue with disposal
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Error during sender disposal");
        }

        GC.SuppressFinalize(this);
    }
}